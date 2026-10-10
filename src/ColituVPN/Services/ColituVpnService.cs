using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ServiceLib.Handler.Builder;
using ServiceLib.Handler.Fmt;
using ServiceLib.Services.CoreConfig;
using ServiceLib.Handler.SysProxy;
using ServiceLib.Helper;

namespace v2rayN.Services;

public sealed class ColituVpnService
{
    public static ColituVpnService Instance { get; } = new();

    private const string ColituSubId = "colitu-api";
    private const string ColituRoutingRemarks = "Colitu VPN Protection";
    internal const string LanDirectRuleId = "colitu-lan-direct";
    private readonly ColituApiClient _api = ColituApiClient.Instance;
    private readonly Config _config = AppManager.Instance.Config;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private bool _coreReady;
    private ColituVpnSession _session = new();
    private ESysProxyType? _restoreSysProxyType;
    private string? _lastCoreMessage;
    private volatile bool _credentialsRefused;

    private List<ColituVpnServer> _lastServers = [];
    private Timer? _watchdog;
    /// <summary>When the last traffic and DNS checks of this session ran (Environment.TickCount64).</summary>
    private long _lastTrafficCheckAt;
    private long _lastDnsCheckAt;
    /// <summary>The last automatic transport switch after a mid-session Hysteria2 stall.</summary>
    private DateTimeOffset? _lastTransportSwitch;
    private volatile bool _autoReconnecting;
    private int _autoReconnectBusy;
    private bool _proxyModeWarned;
    /// <summary>Status saved by the previous run, before this run overwrote the state file.</summary>
    private ColituVpnStatus _previousRunStatus;
    private readonly object _stateLock = new();
    private bool _userDisconnected;
    private CancellationTokenSource? _connectCts;
    private bool _otherVpnWarned;
    /// <summary>
    /// Adaptive Connect memory per network: last-good server and transport, stalled transports,
    /// penalized servers (all expire). Saved with the state; written by the watchdog (thread pool)
    /// and read while the UI connects.
    /// </summary>
    private readonly ColituAdaptiveMemory _adaptive = new();
    /// <summary>Last ping of each server (failed ones too), with the network it was taken on.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ColituPingSample> _pings = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Network key of the current connect / tunnel: stall marks and penalties are filed under it.</summary>
    private string _networkKey = ColituAdaptiveConnect.NetworkKey("other", null);
    private string? _linkKind;
    private long _linkKindAt;
    /// <summary>The spare path the next core config gets behind the primary; null for none.</summary>
    private volatile ColituSparePlan? _sparePlan;
    /// <summary>Automatic mode: the next ranked server's transports, fetched next to the primary's settings.</summary>
    private ColituSpareServer? _spareServer;
    /// <summary>The running core's loopback check inbound that reaches the primary only; null without a spare.</summary>
    private volatile ColituVerifyInbound? _verifyInbound;
    /// <summary>The running core's second check inbound, to the spare alone; null without a spare.</summary>
    private volatile ColituVerifyInbound? _spareVerifyInbound;
    /// <summary>The tunnel as a whole (<see cref="ColituWarmSpare.CheckInboundTag"/>), set at every core start.</summary>
    private volatile ColituVerifyInbound? _checkInbound;
    /// <summary>The profile whose config gets the check inbound (not the TUN front of an Xray tunnel).</summary>
    private volatile string? _checkIndexId;
    /// <summary>Adaptive Connect 2.0 mid-session watcher (every transport; spare-aware).</summary>
    private readonly ColituTunnelWatch _watch = new();
    private readonly ColituSpareHealth _spareHealth = new();
    private long _lastSpareProbeAt;
    /// <summary>A replacement for a dead spare, waiting for a quiet moment to reload the core.</summary>
    private ColituSparePlan? _pendingSpare;
    /// <summary>When the pending spare swap was first held back (0: not held back); reset by a swap or a new spare target.</summary>
    private long _spareSwapDeferredSince;
    private string? _spareSwapDeferKey;
    /// <summary>Bytes on the physical adapter, sampled every watchdog second (for "the tunnel is quiet").</summary>
    private readonly Queue<(long Tick, long Bytes)> _trafficSamples = new();
    /// <summary>The primary server's transports of the current connect (the same-server spare picks from them).</summary>
    private List<(ColituConfigCandidate Candidate, ProfileItem Profile)> _currentProfiles = [];
    /// <summary>Transports that failed during the current connect (they go last when marks are ignored).</summary>
    private readonly HashSet<string> _failedThisConnect = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The last settings fetch of this connect failed on every API base at the network level (the cached settings, if any, were used).</summary>
    private volatile bool _apiUnreachable;
    /// <summary>The stall marks covered (almost) every transport in this round and are ignored.</summary>
    private bool _marksIgnored;

    /// <summary>A manual server where every transport failed: the error offers "Try the fastest server".</summary>
    public bool OfferFastestServer { get; private set; }
    /// <summary>Health checks in a row that carried no traffic through the tunnel.</summary>
    private int _failedChecks;
    /// <summary>DNS checks in a row the tunnel's resolver did not answer.</summary>
    private int _failedDnsChecks;
    /// <summary>The tunnel's resolver answered once this session, so a silence means it hangs (not that the check can't work).</summary>
    private bool _dnsCheckWorks;
    private Timer? _networkChangeDebounce;
    /// <summary><see cref="ColituNetwork.PhysicalNetworkFingerprint"/> when the tunnel was last verified.</summary>
    private volatile string? _verifiedNetwork;
    /// <summary>"No internet" was shown for the current outage; not repeated every check.</summary>
    private bool _offlineNoticeShown;
    /// <summary>Path checks in a row that found no internet at all.</summary>
    private int _offlineVerdicts;
    /// <summary>"Waiting for the network" was logged for the current outage.</summary>
    private bool _waitingForNetwork;
    /// <summary>DNS recoveries (a restart, then a reconnect) in the current 10-minute window.</summary>
    private int _dnsRecoveries;
    private DateTimeOffset _dnsRecoveryWindowStart;
    private string? _activeTransport;
    /// <summary>Names and addresses of the current VPN server; the only hosts kept readable in core log lines.</summary>
    // Replaced, never changed in place: core output threads read it while a connect adds to it.
    private volatile HashSet<string> _serverHosts = new(StringComparer.OrdinalIgnoreCase);
    private int _watchdogBusy;

    /// <summary>The transport the current tunnel runs on (hysteria2, vless-reality, …); for support diagnostics.</summary>
    public string? ConnectedProtocol => Status == ColituVpnStatus.Connected ? _activeTransport : null;

    private ColituVpnService()
    {
        LoadState();
        // The warm spare goes into the generated core config before the core starts.
        CoreConfigHandler.ClientConfigPostProcessor = PostProcessConfig;
        DeleteLegacyConfigCache();
        StartWatchdog();
        // Instantly, not at the next watchdog tick.
        CoreManager.Instance.CoreExited += OnCoreExited;
    }

    public event Action<ColituVpnStatus>? StatusChanged;

    public ColituVpnStatus Status { get; private set; } = ColituVpnStatus.Disconnected;
    /// <summary>The location the user chose; null means "best server".</summary>
    public ColituVpnServer? SelectedServer { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset? ConnectedAt { get; private set; }
    public ColituVpnPreferences Preferences => _session.Preferences.Normalize();

    /// <summary>
    /// Raised with a localization key for events the user should hear about even
    /// when they did not trigger them (automatic reconnects, dropped tunnels).
    /// </summary>
    public event Action<string>? Notice;

    /// <summary>The node the tunnel actually runs through (the panel may pick or fall back).</summary>
    public ColituVpnServer? ConnectedServer { get; private set; }

    /// <summary>Country of the server the current routing profile was built for (decides the Russian-sites rule).</summary>
    private string? _routingServerCountry;

    /// <summary>
    /// Connected, and Russian sites and addresses leave outside the tunnel (privacy mode off, server
    /// outside Russia): the home screen says so next to "Connected".
    /// </summary>
    public bool RussianSitesDirectActive =>
        Status == ColituVpnStatus.Connected && RussianSitesDirect(_routingServerCountry, Preferences.PrivacyModeEnabled);

    /// <summary>"Best server": the panel chooses the recommended node.</summary>
    public bool IsAutoSelection => SelectedServer == null
        && (_session.SelectedServerId.IsNullOrEmpty() || string.Equals(_session.SelectionMode, ColituServerSelectionModes.Best, StringComparison.OrdinalIgnoreCase));

    public string? SavedServerId => IsAutoSelection ? null : SelectedServer?.Id ?? _session.SelectedServerId;

    // ── Simple / Advanced mode ─────────────────────────────────────────────
    /// <summary>Advanced mode: the expert settings are shown. Hidden settings keep their values and keep working.</summary>
    public bool AdvancedMode => Preferences.AdvancedMode;

    /// <summary>
    /// Simple mode only shows this as one line on the home screen: an expert setting changes what
    /// the connection does (split tunnelling, a rotating exit, a multihop route, or the kill switch
    /// holding the internet closed).
    /// </summary>
    public bool AdvancedSettingsActive => AdvancedSettingsOn(Preferences, EffectiveTunMode, RotationActive,
        SelectedServer is { IsMultihop: true }, KillSwitchEngaged && Status != ColituVpnStatus.Connected);

    /// <summary>
    /// The kill switch is on by default on desktop, so it only counts while it holds the internet
    /// closed; split tunnelling, a rotating exit and a chosen multihop route always count.
    /// </summary>
    internal static bool AdvancedSettingsOn(ColituVpnPreferences preferences, bool tun, bool rotation, bool multihopSelected, bool killSwitchHolding) =>
        ColituSplitTunnel.IsActive(preferences, tun) || rotation || multihopSelected || killSwitchHolding;

    /// <summary>
    /// Switches between Simple and Advanced mode: only what the screens show changes (no reconnect).
    /// Going Simple with a multihop route picked, the next connect uses the automatic choice; a live
    /// connection is not touched.
    /// </summary>
    public void SetAdvancedMode(bool advanced)
    {
        _session = _session with { Preferences = _session.Preferences with { AdvancedMode = advanced } };
        SaveState();
        if (!advanced && SelectedServer is { IsMultihop: true })
        {
            LogConnection("Simple mode: the multihop route is no longer the connection target; best server from the next connect");
            SetSelection(null);
        }
    }

    /// <summary>A localization key telling what the connect does right now ("Trying another server…"); null otherwise.</summary>
    public string? ConnectStage { get; private set; }

    /// <summary>
    /// The server "best server" connects to first (rank[0] of <see cref="RankedServers"/>); the
    /// location list and the home card show the same one. Null before the list is loaded.
    /// </summary>
    public ColituVpnServer? RecommendedServer => RankedServers().FirstOrDefault();

    /// <summary>The order automatic mode tries the servers in on the current network (see <see cref="ColituAdaptiveConnect.Rank"/>).</summary>
    public List<ColituVpnServer> RankedServers() =>
        ColituAdaptiveConnect.Rank(_lastServers, _pings, _adaptive, CurrentNetworkKey(), _session.ClientCountry, DateTimeOffset.UtcNow);

    /// <summary>
    /// Pings every location with a probe address (see <see cref="ColituLatency"/>) and keeps the
    /// results, failed ones too, for the automatic order. Returns the successful pings by server id.
    /// </summary>
    public async Task<Dictionary<string, int>> MeasurePingsAsync(IReadOnlyCollection<ColituVpnServer> servers)
    {
        var network = CurrentNetworkKey();
        var measured = await ColituLatency.MeasureAllAsync(servers);
        var at = DateTimeOffset.UtcNow;
        foreach (var server in servers.Where(ColituLatency.CanProbe))
        {
            _pings[server.Id!] = new ColituPingSample(measured.TryGetValue(server.Id!, out var ms) ? ms : null, at, network);
        }
        return measured;
    }

    /// <summary>"&lt;link&gt;|&lt;client_network&gt;" of the network this PC is on now; the link kind is looked up at most every 5 s.</summary>
    private string CurrentNetworkKey()
    {
        var now = Environment.TickCount64;
        if (_linkKind == null || now - Interlocked.Read(ref _linkKindAt) > 5000)
        {
            _linkKind = ColituNetwork.LinkKind();
            Interlocked.Exchange(ref _linkKindAt, now);
        }
        return ColituAdaptiveConnect.NetworkKey(_linkKind, _session.ClientNetwork);
    }

    /// <summary>Saves the panel's view of this device's network; values fetched through the VPN come back empty and never overwrite.</summary>
    private void RememberClientNetwork(ColituServersResponse servers)
    {
        var country = servers.ClientCountry.NullIfEmpty() ?? _session.ClientCountry;
        var network = ColituAdaptiveConnect.ResolveClientNetwork(servers.ClientNetwork, _session.ClientNetwork);
        var session = _session with { ClientCountry = country, ClientNetwork = network };
        if (servers.ClientNetwork.IsNotEmpty())
        {
            // Fetched with the VPN off: the token and the hints belong to this network (no hints = none blocked).
            session = session with
            {
                NetworkHintsBlocked = servers.NetworkHintsBlocked,
                NetworkHintsPreferred = servers.NetworkHintsPreferred,
                NetworkHintsNetwork = servers.ClientNetwork
            };
            if (servers.NetworkToken.IsNotEmpty())
            {
                session = session with { NetworkToken = servers.NetworkToken, NetworkTokenNetwork = servers.ClientNetwork, NetworkTokenAt = DateTimeOffset.UtcNow };
            }
        }
        if (session == _session)
        {
            return;
        }
        _session = session;
        SaveState();
    }

    /// <summary>The panel's tokens are valid for 48 h.</summary>
    private static readonly TimeSpan NetworkTokenValidFor = TimeSpan.FromHours(48);

    /// <summary>The network token for the network this PC is on (the last one seen with the VPN off), unless expired.</summary>
    private string? CurrentNetworkToken() =>
        _session.NetworkToken.IsNotEmpty() && string.Equals(_session.NetworkTokenNetwork, _session.ClientNetwork, StringComparison.Ordinal)
        && _session.NetworkTokenAt is { } at && DateTimeOffset.UtcNow - at < NetworkTokenValidFor
            ? _session.NetworkToken
            : null;

    /// <summary>Protocols the panel's hints call blocked on the current network.</summary>
    private IReadOnlyCollection<string> HintedBlockedList() =>
        string.Equals(_session.NetworkHintsNetwork, _session.ClientNetwork, StringComparison.Ordinal) ? _session.NetworkHintsBlocked ?? [] : [];

    private bool HintedBlocked(string protocol) =>
        ColituAdaptiveConnect.HintSaysBlocked(HintedBlockedList(), protocol, _adaptive, _networkKey, DateTimeOffset.UtcNow);

    /// <summary>Protocols the panel's hints call preferred on the current network (same network binding as the blocked ones).</summary>
    private IReadOnlyList<string> HintedPreferredList() =>
        string.Equals(_session.NetworkHintsNetwork, _session.ClientNetwork, StringComparison.Ordinal) ? _session.NetworkHintsPreferred ?? [] : [];

    public async Task<ColituServersResponse> GetServersAsync()
    {
        try
        {
            var servers = await _api.GetServersAsync();
            RememberClientNetwork(servers);
            // Routes are looked up by id like nodes: a saved choice may be one of them.
            _lastServers = servers.Servers.Concat(servers.Multihop).ToList();
            _multihopRoutes = servers.Multihop;
            // Never blocks the caller: the recovery set follows the server list (VPN on or off).
            _ = RefreshRecoverySetIfDueAsync();
            if (!IsAutoSelection && SelectedServer == null && _session.SelectedServerId.IsNotEmpty())
            {
                SelectedServer = _lastServers.FirstOrDefault(s => string.Equals(s.Id, _session.SelectedServerId, StringComparison.OrdinalIgnoreCase));
            }
            if (Rotation == null)
            {
                // Known before the first connect: a rotation restricts the transports to VLESS.
                _ = TryLoadRotationAsync();
            }
            return servers;
        }
        catch (ColituApiException ex) when (IsPlanError(ex))
        {
            return new ColituServersResponse { PlanRequired = true };
        }
    }

    // ── Multihop routes and the rotating exit IP ───────────────────────────
    private List<ColituVpnServer> _multihopRoutes = [];

    /// <summary>The double-VPN routes of the last server list.</summary>
    public IReadOnlyList<ColituVpnServer> MultihopRoutes => _multihopRoutes;

    /// <summary>The account's rotating-IP preference; null until it was fetched.</summary>
    public ColituRotationPreference? Rotation { get; private set; }

    /// <summary>The exit rotates on a schedule (VLESS only, so the connection avoids Hysteria2).</summary>
    public bool RotationActive => Rotation?.Active == true;

    /// <summary>The connected tunnel is a multihop route.</summary>
    public bool OnMultihopRoute => Status == ColituVpnStatus.Connected && ConnectedServer is { IsMultihop: true };

    public async Task<ColituRotationPreference> LoadRotationAsync(CancellationToken token = default)
    {
        var preference = await _api.GetRotationAsync(token);
        Rotation = preference;
        return preference;
    }

    private async Task TryLoadRotationAsync()
    {
        try
        {
            await LoadRotationAsync();
        }
        catch (Exception ex)
        {
            // An older panel has no rotation; the preference is simply unknown (off).
            LogConnection($"Rotation preference not loaded: {ex.GetType().Name}");
        }
    }

    public async Task<ColituRotationPreference> SaveRotationAsync(int intervalSeconds, IEnumerable<string> countries, CancellationToken token = default)
    {
        var preference = await _api.SetRotationAsync(intervalSeconds, countries, token);
        Rotation = preference;
        return preference;
    }

    /// <summary>
    /// Rotation status of the node this device is connected to; null when not connected to a
    /// node (a multihop route has fixed ends and does not rotate).
    /// </summary>
    public async Task<ColituRotationStatus?> GetRotationStatusAsync(CancellationToken token = default)
    {
        if (Status != ColituVpnStatus.Connected || ConnectedServer is not { IsMultihop: false, Id: { Length: > 0 } nodeId })
        {
            return null;
        }
        return await _api.GetRotationStatusAsync(nodeId, token);
    }

    public async Task<ColituStatsResponse?> GetStatsAsync()
    {
        try
        {
            return await _api.GetStatsAsync();
        }
        catch (ColituApiException ex) when (IsPlanError(ex))
        {
            return null;
        }
    }

    /// <summary>Connects to <paramref name="server"/>, or to the panel's recommended node when null.</summary>
    public async Task ConnectAsync(ColituVpnServer? server)
    {
        await _connectionLock.WaitAsync();
        try
        {
            _userDisconnected = false;
            await ConnectCoreAsync(server);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <summary>Resets the per-session health counters when a new tunnel comes up.</summary>
    private void ResetHealthChecks()
    {
        _watch.Reset();
        _spareHealth.Reset();
        _lastSpareProbeAt = Environment.TickCount64;
        _failedChecks = 0;
        _failedDnsChecks = 0;
        _dnsCheckWorks = false;
        _offlineNoticeShown = false;
        _offlineVerdicts = 0;
        _waitingForNetwork = false;
        // First traffic check 10 s (Hysteria2: 5 s) after the connect, first DNS check after 30 s.
        _lastTrafficCheckAt = _lastDnsCheckAt = Environment.TickCount64;
    }

    /// <summary>Connects with the saved choice (a specific location or the best server).</summary>
    public Task ConnectSavedAsync() => ConnectAsync(IsAutoSelection ? null : SelectedServer ?? FindServer(_session.SelectedServerId));

    private ColituVpnServer? FindServer(string? id)
    {
        return id.IsNullOrEmpty()
            ? null
            : _lastServers.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <param name="quick">
    /// An automatic reconnect: no preference call and at most 3 s for the panel's settings before
    /// the cached ones are used (the panel took up to 8 s while the kill switch held the internet
    /// closed). See <see cref="FetchConfigAsync"/>.
    /// </param>
    private async Task ConnectCoreAsync(ColituVpnServer? server, bool quick = false)
    {
        if (server is { Available: false })
        {
            throw new ColituConnectException(Loc.I["err.unreachable"]);
        }

        EnsureAdministratorForVpn();
        await EnsureCoreReadyAsync();
        _connectCts?.Cancel();
        var attempt = _connectCts = new CancellationTokenSource();
        var token = attempt.Token;
        _serverHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var killSwitchEngagedHere = false;
        // Unknown until this attempt's settings are imported: the kill switch pins the cores to them.
        _killSwitchServers = null;
        _killSwitchPortHopping = false;
        ConnectStage = null;
        _sparePlan = null;
        _spareServer = null;
        _pendingSpare = null;
        ResetSpareSwapDeferral();
        _failedThisConnect.Clear();
        _apiUnreachable = false;
        OfferFastestServer = false;
        SetStatus(ColituVpnStatus.Connecting);
        LastError = null;
        _lastCoreMessage = null;
        _credentialsRefused = false;
        SaveState();
        // "Best server": this app ranks the servers itself and moves on to the next one when one
        // carries no traffic on this network. A server the user picked is never switched.
        var automatic = server == null;
        var connectWatch = Stopwatch.StartNew();

        try
        {
            LogConnection($"Connecting to {(server == null ? "best server" : $"server id={server.Id}, name={server.Name}")}");
            // Wi-Fi just reconnected (or the PC woke up): give the adapter a moment to get its
            // address and route instead of failing every transport in a row.
            for (var waited = 0; waited < 20 && !(ColituNetwork.HasPhysicalNetwork() && ColituNetwork.HasDefaultRoute()); waited++)
            {
                await Task.Delay(1000, token);
            }
            _linkKind = null;
            _networkKey = CurrentNetworkKey();
            if (EffectiveTunMode && !_otherVpnWarned && ColituNetwork.CompetingVpnAdapter() is { } other)
            {
                _otherVpnWarned = true;
                LogConnection($"Another VPN adapter holds a default route: {other}");
                Notice?.Invoke("warn.otherVpn");
            }
            if (Preferences.KillSwitchEnabled)
            {
                // The kill switch blocks DNS outside the tunnel, and Windows resolves names in the
                // system DNS client, not in this app: look the panel up now (the last good
                // addresses stay when it is already armed) and dial those addresses later.
                await ColituPinnedHosts.RefreshAsync(PinnedAppHosts(), token);
                // Armed before the first packet of the session (TUN and proxy mode): this app may
                // reach the panel and resolve the server, nothing else gets out until the tunnel is up.
                var wasEngaged = KillSwitchEngaged;
                var engaged = await EngageKillSwitchAsync(EffectiveTunMode ? "TUN connecting" : "proxy connecting");
                killSwitchEngagedHere = engaged && !wasEngaged;
                if (!engaged && EffectiveTunMode)
                {
                    Notice?.Invoke("err.killSwitch");
                }
            }

            var ranked = automatic ? RankedServers() : [];
            if (automatic)
            {
                LogConnection(ranked.Count == 0
                    ? "Automatic order: no server list yet; the panel chooses"
                    : $"Automatic order: {string.Join(" > ", ranked.Take(5).Select(item => item.Id))}");
            }
            // Servers that carried nothing during this connect: not tried again, and excluded when
            // the panel chooses.
            var failed = new List<string>();
            ColituVpnServer? connected = null;
            // Parallel connect: only the spare on the next server carried traffic, so it leads the next round.
            ColituSpareServerWins? swap = null;
            // Adaptive Connect 3.0: once set, the recovery set's servers replace the panel (API
            // unreachable, no usable cache); the last server failure is what the connect ends with.
            ColituRecoverySet? recovery = null;
            Exception? lastServerFailure = null;
            for (var round = 1; ; round++)
            {
                var wasSwap = swap != null;
                var viaRecovery = recovery != null && !wasSwap;
                var recoveryEnvelope = viaRecovery ? recovery!.NextServer(failed) : null;
                if (viaRecovery && recoveryEnvelope == null)
                {
                    LogConnection($"Recovery set: all {recovery!.Configs.Count} server(s) tried, giving up");
                    throw lastServerFailure ?? new ColituConnectException(Loc.I["err.noServers"]);
                }
                var target = swap != null
                    ? FindServer(swap.Spare.ServerId) ?? swap.Spare.Config.Server ?? new ColituVpnServer { Id = swap.Spare.ServerId }
                    : viaRecovery
                    ? null
                    : automatic
                    ? ranked.FirstOrDefault(item => !failed.Contains(item.Id!, StringComparer.OrdinalIgnoreCase))
                    : server;
                if (round > 1)
                {
                    ConnectStage = "connect.tryingOther";
                    StatusChanged?.Invoke(Status);
                    LogConnection($"Trying another server ({(viaRecovery ? $"recovery set, id={recoveryEnvelope!.Server?.Id}" : target == null ? "the panel chooses" : $"id={target.Id}")}, {connectWatch.ElapsedMilliseconds} ms into the connect)");
                }
                var configTask = swap != null ? Task.FromResult(swap.Spare.Config)
                    : viaRecovery ? FetchRecoveryConfigAsync(recoveryEnvelope!, token)
                    : FetchConfigAsync(target, token, quick, automatic, failed, preferenceSent: round > 1);
                // Started after the primary's request reset the panel connections; never awaited.
                var spareTask = viaRecovery ? StartRecoverySpare(recovery!, recoveryEnvelope!, failed, token) : StartSpareFetch(automatic, ranked, target, failed, token);
                ColituVpnConfigResponse config;
                try
                {
                    config = await configTask;
                }
                catch (Exception ex) when (viaRecovery && !token.IsCancellationRequested)
                {
                    // Its settings could not be turned into a connection (e.g. a rotating exit needs VLESS).
                    var skipped = recoveryEnvelope!.Server?.Id;
                    LogConnection($"Recovery server {skipped} unusable ({ex.GetType().Name}: {ex.Message}); trying the next one");
                    failed.Add(skipped!);
                    lastServerFailure = ex;
                    continue;
                }
                catch (Exception ex) when (!viaRecovery && !wasSwap && !token.IsCancellationRequested
                    && ColituAdaptiveConnect3.ShouldUseRecovery(automatic, _apiUnreachable && ColituRecoverySet.IsApiUnreachable(ex), cacheUsable: false))
                {
                    // Every API base failed at the network level and nothing cached is left to try.
                    recovery = BeginRecovery();
                    if (recovery == null)
                    {
                        throw;
                    }
                    lastServerFailure = ex;
                    continue;
                }
                var forcedFirst = swap?.Protocol;
                swap = null;
                token.ThrowIfCancellationRequested();
                if (config.ServerId.IsNotEmpty() && target?.Id is { } requested && !string.Equals(config.ServerId, requested, StringComparison.OrdinalIgnoreCase))
                {
                    // The panel falls back to another healthy node when the preferred one is unavailable.
                    LogConnection($"Panel selected fallback node {config.ServerId} instead of {requested}");
                }

                connected = FindServer(config.ServerId) ?? config.Server ?? target;
                var connectedId = config.ServerId ?? connected?.Id;
                try
                {
                    var profiles = await ImportConfigAsync(config, connected);
                    if (forcedFirst != null && profiles.FindIndex(item => item.Candidate.Protocol == forcedFirst) is > 0 and var first)
                    {
                        // It just carried traffic as the spare: it leads.
                        var winner = profiles[first];
                        profiles.RemoveAt(first);
                        profiles.Insert(0, winner);
                    }
                    _currentProfiles = profiles;
                    await PrepareConnectionModeAsync(config.Server?.CountryCode ?? connected?.CountryCode);
                    // Whatever arrived by now (or was cached); the connect never waits for the spare.
                    _spareServer = TakeSpareServer(spareTask, connectedId);
                    _killSwitchServers = profiles.Select(item => (item.Profile.Address ?? "", item.Profile.Port, item.Candidate.Protocol)).ToList();
                    _killSwitchPortHopping = profiles.Any(item => UsesPortHopping(item.Profile));
                    if (_spareServer is { } spareServer)
                    {
                        // The spare server is allowed exactly like the primary (every transport it offers).
                        _killSwitchServers.AddRange(spareServer.Items.Select(item => (item.Item.Address ?? "", item.Item.Port, item.Protocol)));
                        _killSwitchPortHopping |= spareServer.Items.Any(item => UsesPortHopping(item.Item));
                    }
                    if (Preferences.KillSwitchEnabled)
                    {
                        // Now the servers are known: the cores may reach exactly them (or everything when
                        // direct routing is on). Replaces the filters in one transaction.
                        if (!await EngageKillSwitchAsync(EffectiveTunMode ? "TUN connecting, server known" : "proxy connecting, server known") && EffectiveTunMode)
                        {
                            // No WFP filters: let sing-box's strict route keep DNS inside the tunnel instead.
                            _config.TunModeItem.StrictRoute = true;
                        }
                    }
                    // Automatic mode: about 20 s per server and 45 s for the whole connect.
                    TimeSpan? serverBudget = automatic ? ServerBudget(connectWatch.Elapsed) : null;
                    try
                    {
                        await StartFirstWorkingProfileAsync(profiles, config, connected, token, serverBudget);
                    }
                    catch (Exception ex) when (_credentialsRefused && ex is not (OperationCanceledException or ColituSpareServerWins))
                    {
                        // The panel creates this device's credentials for a server the first time it is
                        // chosen; the server applies them some seconds later and refuses us until then.
                        LogConnection("The server refused the credentials (new on this server); retrying in 15 s");
                        await StopCoreAsync();
                        await Task.Delay(TimeSpan.FromSeconds(15), token);
                        _credentialsRefused = false;
                        await StartFirstWorkingProfileAsync(profiles, config, connected, token, serverBudget);
                    }
                    break;
                }
                catch (ColituSpareServerWins wins) when (connectWatch.Elapsed < ConnectBudget)
                {
                    // Roles swap: the spare's server and transport lead the next round (one quick reload).
                    swap = wins;
                    await StopCoreAsync();
                }
                catch (Exception ex) when (automatic && IsServerFailure(ex) && connectedId.IsNotEmpty())
                {
                    // Every transport of this server failed on this network: it goes last here for
                    // 30 minutes, and the next ranked server gets its turn.
                    _adaptive.Penalize(_networkKey, connectedId, DateTimeOffset.UtcNow);
                    failed.Add(connectedId!);
                    if (target?.Id is { Length: > 0 } targetId && !failed.Contains(targetId, StringComparer.OrdinalIgnoreCase))
                    {
                        failed.Add(targetId);
                    }
                    SaveState();
                    lastServerFailure = ex;
                    if (recovery != null)
                    {
                        // The set has at most four servers; the loop ends when none is left.
                        LogConnection($"Server {connectedId} carried no traffic on this network ({ex.Message}); penalized for 30 minutes, trying the next recovery server");
                        await StopCoreAsync();
                        continue;
                    }
                    if (round >= ColituAdaptiveConnect.MaxServersPerConnect || connectWatch.Elapsed > NextServerDeadline)
                    {
                        // The settings that failed came from the cache because the panel was unreachable:
                        // the cache is spent, so the recovery set is the last resort.
                        if (ColituAdaptiveConnect3.ShouldUseRecovery(automatic, _apiUnreachable, cacheUsable: false) && BeginRecovery() is { } begun)
                        {
                            recovery = begun;
                            await StopCoreAsync();
                            continue;
                        }
                        LogConnection($"Server {connectedId} carried no traffic ({ex.Message}); {round} server(s) tried in {connectWatch.ElapsedMilliseconds} ms, giving up");
                        throw;
                    }
                    LogConnection($"Server {connectedId} carried no traffic on this network ({ex.Message}); penalized for 30 minutes, trying the next one");
                    await StopCoreAsync();
                }
            }

            ConnectedServer = connected;
            ConnectedAt = DateTimeOffset.Now;
            ConnectStage = null;
            if (recovery != null)
            {
                LogConnection($"Connected through the recovery set (server {connected?.Id}); the server list, settings and recovery set are refreshed through the tunnel");
                _ = RefreshAfterRecoveryAsync();
            }
            if (connected is { IsMultihop: false, Id.Length: > 0 })
            {
                var now = DateTimeOffset.UtcNow;
                _adaptive.RememberGoodServer(_networkKey, connected.Id, now);
                _adaptive.RememberGoodTransport(_networkKey, connected.Id, _activeTransport, now);
            }
            ResetHealthChecks();
            _killSwitchHeldFromPreviousRun = false;
            await ApplyKillSwitchForConnectedTunnelAsync();
            SetStatus(ColituVpnStatus.Connected);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            ConnectStage = null;
            LogConnection("Connection attempt cancelled");
            if (killSwitchEngagedHere) await ReleaseKillSwitchAsync("connection attempt cancelled");
            await StopCoreAsync();
            await SysProxyHandler.UpdateSysProxy(_config, true);
            await RestoreProxyPreferenceAsync();
            await RestoreRoutingPreferenceAsync();
            ConnectedServer = null;
            ConnectedAt = null;
            SetStatus(ColituVpnStatus.Disconnected);
        }
        catch (Exception ex)
        {
            ConnectStage = null;
            // A server the user picked where every transport failed: offer the fastest server (one tap).
            OfferFastestServer = !automatic && server is not null && IsServerFailure(ex);
            LastError = FriendlyConnectionError(ex);
            LogConnection($"Connection failed: {ex}");
            // Engaged by this attempt (not by a lost tunnel): a failed manual connect must not
            // leave the computer offline.
            if (killSwitchEngagedHere || IsDevicePaused(ex))
            {
                // A paused device never keeps the internet closed: there is nothing to reconnect to.
                await ReleaseKillSwitchAsync(IsDevicePaused(ex) ? "device paused" : "connection failed");
            }
            await StopCoreAsync();
            await SysProxyHandler.UpdateSysProxy(_config, true);
            await RestoreProxyPreferenceAsync();
            await RestoreRoutingPreferenceAsync();
            ConnectedServer = null;
            ConnectedAt = null;
            SetStatus(ColituVpnStatus.Error);
            if (ex is ColituApiException api && IsPlanError(api))
            {
                throw new ColituPlanRequiredException();
            }
            if (FindDevicePaused(ex) is { } paused)
            {
                DevicePaused = paused;
                throw new ColituDevicePausedException(paused);
            }
            throw new ColituConnectException(LastError, ex);
        }
    }

    // ── Warm spare: a second path inside the running core ──────────────────
    /// <summary>The spare's settings may take this long; after that the connect goes on without them.</summary>
    private static readonly TimeSpan SpareFetchBudget = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Automatic mode: fetches the settings of the next ranked server (not failed, not penalized
    /// on this network) for the warm spare, next to the primary's. Null when there is no such
    /// server or the setting is off.
    /// </summary>
    private ColituSpareFetch? StartSpareFetch(bool automatic, IReadOnlyList<ColituVpnServer> ranked, ColituVpnServer? target, IReadOnlyCollection<string> failed, CancellationToken token)
    {
        if (!automatic || !Preferences.WarmSpareEnabled)
        {
            return null;
        }
        var now = DateTimeOffset.UtcNow;
        var spare = ranked.FirstOrDefault(item => item.Id is { Length: > 0 } id
            && !string.Equals(id, target?.Id, StringComparison.OrdinalIgnoreCase)
            && !failed.Contains(id, StringComparer.OrdinalIgnoreCase)
            && !_adaptive.IsPenalized(_networkKey, id, now));
        return spare == null ? null : new ColituSpareFetch(spare.Id!, FetchSpareServerAsync(spare, token));
    }

    private async Task<ColituSpareServer?> FetchSpareServerAsync(ColituVpnServer server, CancellationToken token)
    {
        var cacheKey = SpareCacheKey(server.Id!);
        ColituVpnConfigResponse? config = null;
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(SpareFetchBudget);
            config = await _api.GetConfigAsync(null, budget.Token, node: server.Id);
            if (config != null)
            {
                SaveCachedConfig(cacheKey, config);
            }
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            LogConnection($"Warm spare settings for server {server.Id} not fetched in {SpareFetchBudget.TotalSeconds:0} s ({ex.GetType().Name})");
        }
        return ToSpareServer(config ?? LoadCachedConfig(cacheKey), server.Id!, cached: config == null);
    }

    /// <summary>The spare server's transports as profiles (never stored in the profile list); null when unusable.</summary>
    private ColituSpareServer? ToSpareServer(ColituVpnConfigResponse? config, string serverId, bool cached = true)
    {
        if (config == null || config.Server?.IsMultihop == true
            || (config.ServerId.IsNotEmpty() && !string.Equals(config.ServerId, serverId, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }
        if (RotationActive)
        {
            // A rotating exit runs on VLESS only, the spare too.
            config = ColituApiClient.RestrictToVless(config);
            if (config == null)
            {
                return null;
            }
        }
        var items = config.Candidates
            .Select(candidate => (candidate.Protocol, Item: FmtHandler.ResolveConfig(candidate.ShareLink, out _)))
            .Where(item => item.Item != null && item.Protocol.IsNotEmpty())
            .Select(item => (item.Protocol, Item: item.Item!))
            .ToList();
        return items.Count == 0 ? null : new ColituSpareServer(serverId, items, config, cached);
    }

    /// <summary>The spare server if its settings are already here (or cached); the connect never waits for them.</summary>
    private ColituSpareServer? TakeSpareServer(ColituSpareFetch? fetch, string? primaryId)
    {
        if (fetch == null)
        {
            return null;
        }
        ColituSpareServer? spare;
        if (fetch.Task.IsCompleted)
        {
            spare = fetch.Task.IsCompletedSuccessfully ? fetch.Task.Result : null;
        }
        else
        {
            LogConnection("Warm spare settings not here yet; connecting with the cached ones, if any");
            spare = ToSpareServer(LoadCachedConfig(SpareCacheKey(fetch.ServerId)), fetch.ServerId, cached: true);
        }
        return spare != null && !string.Equals(spare.ServerId, primaryId, StringComparison.OrdinalIgnoreCase) ? spare : null;
    }

    private static string SpareCacheKey(string serverId) => $"{ColituAuthService.Instance.CurrentUser?.Id ?? "-"}|{serverId}";

    /// <summary>
    /// The spare behind <paramref name="primaryProtocol"/> (Adaptive Connect 2.0). Automatic mode: a
    /// transport proven on this network on the next ranked server (the primary's own first; same
    /// family allowed), only if nothing is proven the other family. Otherwise (manual server, or
    /// nothing fits there) the same server: the other family, Shadowsocks last, never the primary's
    /// own transport. Stalled transports, penalized servers, multihop routes and what failed as a
    /// spare in this connect (<paramref name="excluded"/>, "server|transport") never; hinted-blocked
    /// ones only when nothing else is left. Only transports the primary's core can run.
    /// </summary>
    private ColituSparePlan? PlanSpare(string primaryProtocol, ProfileItem primary, List<(ColituConfigCandidate Candidate, ProfileItem Profile)> profiles,
        ColituVpnConfigResponse config, ColituVpnServer? server, ISet<string>? excluded = null)
    {
        if (!SpareAllowed(Preferences, server, config))
        {
            LogConnection(Preferences.WarmSpareEnabled ? "warm spare none: multihop route" : "warm spare none: turned off");
            return null;
        }
        var singBox = ColituWarmSpare.IsUdp(primaryProtocol) || ColituSplitTunnel.NeedsSingBox(Preferences, EffectiveTunMode);
        var serverId = config.ServerId ?? server?.Id;
        var now = DateTimeOffset.UtcNow;
        var proven = _adaptive.ProvenOnNetwork(_networkKey, now);
        bool Excluded(string? id, string protocol) => excluded?.Contains(SpareKey(id, protocol)) == true;
        if (_spareServer is { } other && !string.Equals(other.ServerId, serverId, StringComparison.OrdinalIgnoreCase)
            && !_adaptive.IsPenalized(_networkKey, other.ServerId, now)
            && ColituWarmSpare.ChooseOnNextServer(primaryProtocol, other.Items.Select(item => item.Protocol), singBox,
                protocol => proven.Contains(protocol, StringComparer.OrdinalIgnoreCase),
                protocol => SpareStalled(other.ServerId, protocol) || Excluded(other.ServerId, protocol), HintedBlocked) is { } choice)
        {
            var plan = new ColituSparePlan(primary.IndexId, other.Items.First(item => item.Protocol == choice.Protocol).Item, choice.Protocol, other.ServerId, choice.Reason, other.ServerId);
            LogSpareAttached(plan, primaryProtocol, proven, other.Items.Select(item => item.Protocol), other.Cached ? "cached" : "fetched");
            return plan;
        }
        if (ColituWarmSpare.ChooseOnSameServer(primaryProtocol, profiles.Select(item => item.Candidate.Protocol), singBox,
                protocol => SpareStalled(serverId, protocol), HintedBlocked, protocol => Excluded(serverId, protocol)) is { } same)
        {
            var plan = new ColituSparePlan(primary.IndexId, profiles.First(item => item.Candidate.Protocol == same.Protocol).Profile, same.Protocol, serverId, same.Reason);
            LogSpareAttached(plan, primaryProtocol, proven, profiles.Select(item => item.Candidate.Protocol), "fetched");
            return plan;
        }
        LogConnection($"warm spare none: nothing fits behind {primaryProtocol} (proven here [{string.Join(", ", proven)}])");
        return null;
    }

    private void LogSpareAttached(ColituSparePlan plan, string primaryProtocol, IEnumerable<string> proven, IEnumerable<string> offered, string source)
    {
        var stalled = offered.Distinct(StringComparer.OrdinalIgnoreCase).Where(protocol => _adaptive.IsStalled(_networkKey, plan.ServerId, protocol, DateTimeOffset.UtcNow));
        LogConnection($"warm spare attached: {plan.NextServerId ?? "same server"}/{plan.Protocol} (primary {primaryProtocol}; spare reason: {plan.Reason}; proven here [{string.Join(", ", proven)}]; stalled [{string.Join(", ", stalled)}]; profile {source})");
    }

    /// <summary>Stall marks for the spare choice; ignored in a round where they cover (almost) every transport.</summary>
    private bool SpareStalled(string? serverId, string protocol) =>
        !_marksIgnored && _adaptive.IsStalled(_networkKey, serverId, protocol, DateTimeOffset.UtcNow);

    private static string SpareKey(string? serverId, string protocol) => $"{serverId ?? "-"}|{protocol}";

    private static string Describe(ColituSparePlan plan) => $"{plan.NextServerId ?? "same server"}/{plan.Protocol}";

    /// <summary>No spare with the setting off or on a multihop route (its ends are fixed).</summary>
    internal static bool SpareAllowed(ColituVpnPreferences preferences, ColituVpnServer? server, ColituVpnConfigResponse config) =>
        preferences.WarmSpareEnabled && server is not { IsMultihop: true } && config.Server is not { IsMultihop: true };

    /// <summary>
    /// <see cref="CoreConfigHandler.ClientConfigPostProcessor"/>: the warm spare, then the tunnel
    /// check inbound, to the main core's config.
    /// </summary>
    private string PostProcessConfig(CoreConfigContext context, string json)
    {
        json = ApplyWarmSpare(context, json);
        if (_checkIndexId == null || !string.Equals(context.Node?.IndexId, _checkIndexId, StringComparison.Ordinal))
        {
            return json;
        }
        try
        {
            var check = ColituWarmSpare.NewVerifyInbound();
            if (ColituWarmSpare.AddCheckInbound(json, context.RunCoreType == ECoreType.sing_box, check) is { } added)
            {
                _checkInbound = check;
                return added;
            }
            LogConnection("Tunnel check inbound could not be added; checks use the local proxy port");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituVpnService.AddCheckInbound", ex);
        }
        return json;
    }

    /// <summary>Where a check of the whole tunnel goes: the check inbound, else the local proxy port (routing rules apply there).</summary>
    private (int Port, NetworkCredential? Credentials) TunnelCheckTarget() => _checkInbound is { } check
        ? (check.Port, new NetworkCredential(check.User, check.Password))
        : (AppManager.Instance.GetLocalPort(EInboundProtocol.socks), null);

    /// <summary>
    /// Adds the planned spare to the main
    /// core's config (the TUN front of an Xray tunnel and other configs stay as generated). Any
    /// problem leaves the config without a spare.
    /// </summary>
    private string ApplyWarmSpare(CoreConfigContext context, string json)
    {
        var plan = _sparePlan;
        if (plan == null || !string.Equals(context.Node?.IndexId, plan.PrimaryIndexId, StringComparison.Ordinal))
        {
            return json;
        }
        try
        {
            var singBox = context.RunCoreType == ECoreType.sing_box;
            if (!ColituWarmSpare.RunsOn(plan.Protocol, singBox))
            {
                LogConnection($"Warm spare {plan.Protocol} does not run in {context.RunCoreType}; none this time");
                return json;
            }
            var spareContext = context with { Node = plan.Spare };
            var generated = singBox
                ? new CoreConfigSingboxService(spareContext).GenerateClientConfigContent()
                : new CoreConfigV2rayService(spareContext).GenerateClientConfigContent();
            var outbound = generated.Success && generated.Data?.ToString() is { } spareJson ? ColituWarmSpare.FindOutbound(spareJson) : null;
            // The connect check reaches the primary through this inbound, never the spare.
            var verify = ColituWarmSpare.NewVerifyInbound();
            // And the spare alone, for the parallel connect and the spare health probe.
            var spareVerify = ColituWarmSpare.NewVerifyInbound();
            var merged = outbound == null ? null
                : singBox ? ColituWarmSpare.ApplySingbox(json, outbound, verify: verify, spareVerify: spareVerify)
                : ColituWarmSpare.ApplyXray(json, outbound, verify: verify, spareVerify: spareVerify);
            if (merged == null)
            {
                LogConnection($"Warm spare {plan.Protocol} could not be added to the {context.RunCoreType} config; none this time");
                return json;
            }
            var problems = singBox ? ColituWarmSpare.SingboxProblems(merged) : ColituWarmSpare.XrayProblems(merged);
            if (problems.Count > 0)
            {
                LogConnection($"Warm spare config failed its tag check ({string.Join("; ", problems.Take(3))}); none this time");
                return json;
            }
            _verifyInbound = verify;
            _spareVerifyInbound = spareVerify;
            var hosts = new HashSet<string>(_serverHosts, StringComparer.OrdinalIgnoreCase);
            foreach (var host in new[] { plan.Spare.Address, plan.Spare.Sni })
            {
                if (host.IsNotEmpty()) hosts.Add(host);
            }
            _serverHosts = hosts;
            LogConnection($"Warm spare: {plan.Protocol} on server {plan.ServerId} behind the primary ({context.RunCoreType}, probe every {(singBox ? ColituWarmSpare.SingboxProbeInterval : ColituWarmSpare.XrayProbeInterval).TotalSeconds:0} s)");
            return merged;
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituVpnService.ApplyWarmSpare", ex);
            return json;
        }
    }

    // ── Recovery set (Adaptive Connect 3.0) ────────────────────────────────
    // GET /client/recovery: up to four config envelopes, fetched in the background next to the
    // server list and stored (DPAPI, they carry the device's server credentials) next to the config
    // cache. Used only by an automatic connect that cannot reach the panel on any API base and has
    // no usable cached settings. The nodes still check the device credential: it grants nothing new.
    private int _recoveryRefreshing;

    private static string RecoverySetPath() => ColituHardening.UserConfigPath("colitu-recovery.bin");

    /// <summary>The stored set (also an expired one, so that the caller deletes it); null when none or unreadable.</summary>
    private static ColituRecoverySet? LoadRecoverySet()
    {
        try
        {
            if (!File.Exists(RecoverySetPath())) return null;
            var bytes = System.Security.Cryptography.ProtectedData.Unprotect(File.ReadAllBytes(RecoverySetPath()), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return ColituRecoverySet.Parse(System.Text.Encoding.UTF8.GetString(bytes));
        }
        catch
        {
            return null;
        }
    }

    private static void SaveRecoveryJson(string json)
    {
        try
        {
            var protectedBytes = System.Security.Cryptography.ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(json), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            var temp = RecoverySetPath() + ".tmp";
            File.WriteAllBytes(temp, protectedBytes);
            File.Move(temp, RecoverySetPath(), overwrite: true);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituVpnService.SaveRecoveryJson", ex);
        }
    }

    /// <summary>Sign-out, account deletion, 401/403 on a fetch, or a set past its <c>recovery_until</c>.</summary>
    internal static void DeleteRecoverySet()
    {
        try
        {
            if (File.Exists(RecoverySetPath())) File.Delete(RecoverySetPath());
        }
        catch
        {
            // Best effort.
        }
    }

    /// <summary>
    /// Fetches the set when none is stored or it is 24 h old (at most one attempt per 6 h). Never
    /// blocks start-up or a connect; works with the VPN on or off. 401/403 delete the stored set,
    /// network errors and 5xx keep it.
    /// </summary>
    private async Task RefreshRecoverySetIfDueAsync()
    {
        if (!ColituAdaptiveConnect3.RecoveryFetchAllowed() || Interlocked.Exchange(ref _recoveryRefreshing, 1) == 1)
        {
            return;
        }
        try
        {
            if (!ColituAuthService.Instance.HasSession)
            {
                return;
            }
            var user = ColituAuthService.Instance.CurrentUser?.Id ?? "-";
            var now = DateTimeOffset.UtcNow;
            var lastAttempt = string.Equals(_session.RecoveryAttemptUser, user, StringComparison.Ordinal) ? _session.RecoveryAttemptAt : null;
            if (!ColituRecoverySet.RefreshDue(LoadRecoverySet()?.GeneratedAt, lastAttempt, now))
            {
                return;
            }
            _session = _session with { RecoveryAttemptAt = now, RecoveryAttemptUser = user };
            SaveState();
            try
            {
                var json = await _api.GetRecoveryJsonAsync();
                if (ColituRecoverySet.Parse(json) is { } fresh)
                {
                    SaveRecoveryJson(json);
                    LogConnection($"Recovery set stored: {fresh.Configs.Count} server(s), until {fresh.RecoveryUntil:O}");
                }
                else
                {
                    LogConnection("Recovery set answer not usable; the stored one is kept");
                }
            }
            catch (ColituApiException ex) when (ColituRecoverySet.DeletesSetOnFetchFailure(ex.StatusCode, ex.ErrorCode))
            {
                DeleteRecoverySet();
                LogConnection($"Recovery set refused ({(int)ex.StatusCode}); the stored one is deleted");
            }
            catch (Exception ex)
            {
                LogConnection($"Recovery set not fetched ({ex.GetType().Name}); the stored one is kept");
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituVpnService.RefreshRecoverySetIfDueAsync", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _recoveryRefreshing, 0);
        }
    }

    /// <summary>Connected through the recovery set: the panel is reachable through the tunnel now, refresh everything from it.</summary>
    private async Task RefreshAfterRecoveryAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(3));
        await RefreshServerListQuietlyAsync();
    }

    /// <summary>
    /// The stored set for a connect that cannot use the panel or the cache: null (give up as before)
    /// when there is none, or it is past <c>recovery_until</c> (then it is deleted).
    /// </summary>
    private ColituRecoverySet? BeginRecovery()
    {
        if (!ColituAdaptiveConnect3.RecoveryFetchAllowed())
        {
            return null;
        }
        var set = LoadRecoverySet();
        if (set == null)
        {
            LogConnection("API unreachable and no usable cache; no recovery set stored");
            return null;
        }
        if (set.IsExpired(DateTimeOffset.UtcNow))
        {
            DeleteRecoverySet();
            LogConnection($"API unreachable and no usable cache; the recovery set is past {set.RecoveryUntil:O} and was deleted");
            return null;
        }
        LogConnection($"API unreachable and no usable cache: recovery set, {set.Configs.Count} servers (until {set.RecoveryUntil:O})");
        return set;
    }

    /// <summary>One envelope of the set as connection settings (pinned addresses, its own offline grace ignored).</summary>
    private async Task<ColituVpnConfigResponse> FetchRecoveryConfigAsync(ColituConfigEnvelopeDto envelope, CancellationToken token)
    {
        var config = await _api.GetRecoveryConfigAsync(envelope, token) ?? throw new ColituConnectException(Loc.I["err.noServers"]);
        return RestrictToVlessIfNeeded(config, null);
    }

    /// <summary>The warm spare behind a recovery server: the next server of the set that did not fail and is not penalized here.</summary>
    private ColituSpareFetch? StartRecoverySpare(ColituRecoverySet recovery, ColituConfigEnvelopeDto current, IReadOnlyCollection<string> failed, CancellationToken token)
    {
        if (!Preferences.WarmSpareEnabled)
        {
            return null;
        }
        var now = DateTimeOffset.UtcNow;
        var skip = failed
            .Append(current.Server?.Id ?? "")
            .Concat(recovery.Configs.Select(item => item.Server?.Id ?? "").Where(id => _adaptive.IsPenalized(_networkKey, id, now)))
            .ToList();
        if (recovery.NextServer(skip) is not { Server.Id.Length: > 0 } next)
        {
            return null;
        }
        return new ColituSpareFetch(next.Server.Id, SpareFromRecoveryAsync(next, next.Server.Id, token));
    }

    private async Task<ColituSpareServer?> SpareFromRecoveryAsync(ColituConfigEnvelopeDto envelope, string serverId, CancellationToken token)
    {
        try
        {
            return ToSpareServer(await _api.GetRecoveryConfigAsync(envelope, token), serverId, cached: true);
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            LogConnection($"Warm spare settings for recovery server {serverId} not built ({ex.GetType().Name})");
            return null;
        }
    }

    // ── Speed budget of an automatic connect ───────────────────────────────
    /// <summary>One transport: core start plus traffic check.</summary>
    internal static readonly TimeSpan TransportBudget = TimeSpan.FromSeconds(8);
    internal static readonly TimeSpan PerServerBudget = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan ConnectBudget = TimeSpan.FromSeconds(45);
    /// <summary>No further server is started after this (it would end past <see cref="ConnectBudget"/>).</summary>
    private static readonly TimeSpan NextServerDeadline = ConnectBudget - PerServerBudget;

    /// <summary>Time for the next server: at most 20 s, at least one transport, within the 45 s of the connect.</summary>
    internal static TimeSpan ServerBudget(TimeSpan elapsed)
    {
        var left = ConnectBudget - elapsed;
        return left > PerServerBudget ? PerServerBudget : left < TransportBudget ? TransportBudget : left;
    }

    /// <summary>
    /// A failure of the server on this network (no transport carried traffic, its settings did not
    /// start), not one of the panel, the account, or a device that is offline.
    /// </summary>
    internal static bool IsServerFailure(Exception ex) =>
        ex is not (OperationCanceledException or ColituApiException or ColituPlanRequiredException or ColituDevicePausedException or ColituSpareServerWins)
        && ex is not ColituConnectException { Offline: true };

    /// <summary>
    /// Asks the panel for fresh connection settings. When the panel is slow or cannot be
    /// reached (offline, blocked, down) the last settings received for the same choice are
    /// used until their offline grace period ends ("best server": any cached server).
    /// An automatic reconnect (<paramref name="quick"/>) skips the preference call (the choice
    /// did not change) and waits at most 3 s for the panel, a user's connect 10 s.
    /// In <paramref name="automatic"/> mode <paramref name="server"/> is the ranked node, asked for
    /// with <c>node=</c> (the stored preference stays "best server"); without one the panel
    /// chooses and skips the servers in <paramref name="exclude"/>.
    /// </summary>
    private async Task<ColituVpnConfigResponse> FetchConfigAsync(ColituVpnServer? server, CancellationToken token, bool quick = false,
        bool automatic = false, IReadOnlyCollection<string>? exclude = null, bool preferenceSent = false)
    {
        // Bound to the account: settings cached for one account never connect another one.
        var cacheKey = $"{ColituAuthService.Instance.CurrentUser?.Id ?? "-"}|{server?.Id ?? "auto"}";
        var fallback = LoadCachedConfig(cacheKey) ?? (automatic ? LoadAnyCachedConfig(exclude) : null);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (fallback != null)
        {
            // With settings to fall back on, a slow panel (up to 20 s per request from Russia)
            // must not hold the connection: 3 s for an automatic reconnect, 10 s otherwise.
            budget.CancelAfter(quick ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(10));
        }
        var watch = Stopwatch.StartNew();
        // The previous tunnel may just have stopped: never reuse a connection opened over its route.
        ResetDirectConnections();
        try
        {
            // A route is not a node: it is never stored as this device's preferred node.
            if (!quick && !preferenceSent && server is not { IsMultihop: true })
            {
                await _api.SetPreferredServerAsync(automatic ? null : server?.Id, budget.Token);
            }
            if (!quick && Rotation == null && server is not { IsMultihop: true })
            {
                // Whether the exit rotates decides the transports; do not connect blind if it is quickly known.
                using var rotationBudget = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
                rotationBudget.CancelAfter(TimeSpan.FromSeconds(3));
                try
                {
                    await LoadRotationAsync(rotationBudget.Token);
                }
                catch (Exception ex) when (!budget.Token.IsCancellationRequested)
                {
                    LogConnection($"Rotation preference not loaded: {ex.GetType().Name}");
                }
            }
            var preferenceMs = watch.ElapsedMilliseconds;
            var config = await _api.GetConfigAsync(server, budget.Token,
                    node: automatic ? server?.Id : null,
                    exclude: automatic && server == null ? exclude : null)
                ?? throw new ColituConnectException(Loc.I["err.noServers"]);
            LogConnection($"Panel answered in {watch.ElapsedMilliseconds} ms (preference {preferenceMs} ms, config {watch.ElapsedMilliseconds - preferenceMs} ms)");
            // The panel handed out settings: this device is not paused (any more).
            DevicePaused = null;
            _apiUnreachable = false;
            SaveCachedConfig(cacheKey, config);
            return RestrictToVlessIfNeeded(config, server);
        }
        catch (ColituApiException ex) when (ex.ErrorCode is "MULTIHOP_ROUTE_NOT_FOUND")
        {
            // The route was removed or renamed: learn the current list for the next try.
            LogConnection("Multihop route no longer offered by the panel");
            _ = RefreshServerListQuietlyAsync();
            throw new ColituConnectException(Loc.I["multihop.gone"], ex);
        }
        catch (Exception ex) when (!token.IsCancellationRequested && fallback == null && ColituRecoverySet.IsApiUnreachable(ex))
        {
            // Nothing cached to fall back on: the recovery set may take over (see ConnectCoreAsync).
            _apiUnreachable = true;
            throw;
        }
        catch (Exception ex) when (!token.IsCancellationRequested && fallback != null && (ex is OperationCanceledException || IsNetworkFailure(ex)))
        {
            _apiUnreachable = ColituRecoverySet.IsApiUnreachable(ex);
            LogConnection($"Panel did not answer in {watch.ElapsedMilliseconds} ms ({ex.GetType().Name}); using cached connection settings (server {fallback.ServerId}, revision {fallback.Revision})");
            return RestrictToVlessIfNeeded(fallback, server);
        }
    }

    /// <summary>
    /// A multihop route and a rotating exit run on VLESS only: the other transports (Hysteria2, Trojan,
    /// Shadowsocks) are dropped from the candidates, and a node without VLESS cannot be used for them.
    /// </summary>
    private ColituVpnConfigResponse RestrictToVlessIfNeeded(ColituVpnConfigResponse config, ColituVpnServer? server)
    {
        if (server is not { IsMultihop: true } && !RotationActive)
        {
            return config;
        }
        var restricted = ColituApiClient.RestrictToVless(config);
        if (restricted == null)
        {
            throw new ColituConnectException(Loc.I[server is { IsMultihop: true } ? "multihop.needsVless" : "rotation.needsVless"]);
        }
        if (restricted != config)
        {
            LogConnection($"VLESS only ({(server is { IsMultihop: true } ? "multihop route" : "rotating IP")}): {config.Candidates.Count - restricted.Candidates.Count} other transport(s) skipped");
        }
        return restricted;
    }

    private async Task RefreshServerListQuietlyAsync()
    {
        try
        {
            await GetServersAsync();
        }
        catch (Exception ex)
        {
            LogConnection($"Server list refresh failed: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// Keep-alive connections to the panel and the DNS resolvers die when the tunnel goes up or
    /// down (their route changes underneath them); reusing one hangs until its timeout.
    /// </summary>
    private static void ResetDirectConnections()
    {
        ColituAuthService.Instance.ResetConnections();
        ColituNetwork.ResetConnections();
    }

    public async Task DisconnectAsync()
    {
        _userDisconnected = true;
        _connectCts?.Cancel();
        await _connectionLock.WaitAsync();
        try
        {
            await DisconnectCoreAsync();
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private async Task DisconnectCoreAsync()
    {
        await EnsureCoreReadyAsync();
        await StopCoreAsync();
        var proxyResult = await SysProxyHandler.UpdateSysProxy(_config, true);
        LogConnection($"Disconnect proxy restore result={proxyResult}");
        await RestoreProxyPreferenceAsync();
        await RestoreRoutingPreferenceAsync();
        ConnectedAt = null;
        ConnectedServer = null;
        await ReleaseKillSwitchAsync("disconnected by the user");
        SetStatus(ColituVpnStatus.Disconnected);
    }

    /// <summary>Moves an active connection to <paramref name="server"/> (null = best server).</summary>
    public async Task SwitchServerAsync(ColituVpnServer? server)
    {
        _connectCts?.Cancel();
        await _connectionLock.WaitAsync();
        try
        {
            SetSelection(server);
            SetStatus(ColituVpnStatus.Reconnecting);
            await StopCoreAsync();
            await ConnectCoreAsync(server);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public Task ReconnectAsync() => ReconnectAsync(quick: false);

    private async Task ReconnectAsync(bool quick)
    {
        _connectCts?.Cancel();
        await _connectionLock.WaitAsync();
        try
        {
            SetStatus(ColituVpnStatus.Reconnecting);
            await StopCoreAsync();
            await ConnectCoreAsync(IsAutoSelection ? null : SelectedServer ?? FindServer(_session.SelectedServerId), quick);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task<bool> TryAutoConnectAsync()
    {
        var connected = await TryAutoConnectCoreAsync();
        if (!connected)
        {
            // The kill switch kept the internet closed for this connection since the last run.
            await ReleaseHeldKillSwitchAsync("auto-connect did not connect");
        }
        return connected;
    }

    private async Task<bool> TryAutoConnectCoreAsync()
    {
        if (DevicePaused != null)
        {
            LogConnection("Auto-connect skipped: this device is paused");
            return false;
        }
        if (!Preferences.AutoConnectEnabled || GetStatus() is ColituVpnStatus.Connected or ColituVpnStatus.Connecting or ColituVpnStatus.Reconnecting)
        {
            return false;
        }

        // At sign-in a USB Wi-Fi adapter can take well over a minute to get its network.
        var waited = Stopwatch.StartNew();
        while (!NetworkInterface.GetIsNetworkAvailable() || !ColituNetwork.HasDefaultRoute())
        {
            if (waited.Elapsed > TimeSpan.FromMinutes(3) || _userDisconnected)
            {
                LogConnection("Auto-connect skipped: no network");
                return false;
            }
            await Task.Delay(1000);
            if (GetStatus() is ColituVpnStatus.Connected or ColituVpnStatus.Connecting or ColituVpnStatus.Reconnecting)
            {
                // The user connected meanwhile.
                return false;
            }
        }
        if (waited.Elapsed > TimeSpan.FromSeconds(2))
        {
            LogConnection($"Network available after {waited.ElapsedMilliseconds} ms; auto-connecting");
        }

        for (var round = 1; round <= 2; round++)
        {
            try
            {
                await ConnectSavedAsync();
                return Status == ColituVpnStatus.Connected;
            }
            catch (ColituPlanRequiredException)
            {
                // Auto-connect now starts before the plan is loaded; a second try can't help.
                LogConnection("Auto-connect skipped: no active plan");
                return false;
            }
            catch (ColituDevicePausedException)
            {
                // Never connect a paused device by itself; the home screen asks the user.
                LogConnection("Auto-connect skipped: this device is paused");
                return false;
            }
            catch (Exception ex)
            {
                LogConnection($"Auto-connect attempt {round} failed: {ex.Message}");
                if (_userDisconnected || round == 2)
                {
                    return false;
                }
                await Task.Delay(TimeSpan.FromSeconds(8));
                if (_userDisconnected || Status is ColituVpnStatus.Connected or ColituVpnStatus.Connecting)
                {
                    return false;
                }
            }
        }
        return false;
    }

    public ColituVpnStatus GetStatus() => Status;

    public TimeSpan GetConnectionDuration()
    {
        return Status == ColituVpnStatus.Connected && ConnectedAt != null
            ? DateTimeOffset.Now - ConnectedAt.Value
            : TimeSpan.Zero;
    }

    public ColituVpnServer? GetSelectedServer() => SelectedServer;

    public string? GetLastError() => LastError;

    public void SelectServer(ColituVpnServer server) => SetSelection(server);

    public void SelectAuto() => SetSelection(null);

    private void SetSelection(ColituVpnServer? server)
    {
        SelectedServer = server;
        _session = _session with
        {
            SelectedServerId = server?.Id,
            SelectionMode = server == null ? ColituServerSelectionModes.Best : ColituServerSelectionModes.Manual
        };
        SaveState();
    }

    public async Task UpdatePreferencesAsync(ColituVpnPreferences preferences)
    {
        _session = _session with { Preferences = preferences.Normalize() };
        SaveState();

        if (!_session.Preferences.KillSwitchEnabled)
        {
            await ReleaseKillSwitchAsync("kill switch turned off");
        }
        else if (Status == ColituVpnStatus.Connected)
        {
            await ApplyKillSwitchForConnectedTunnelAsync();
        }

        if (_coreReady && Status is not (ColituVpnStatus.Connected or ColituVpnStatus.Connecting or ColituVpnStatus.Reconnecting))
        {
            await ApplyRuntimePreferencesAsync(_session.Preferences);
        }
    }

    /// <summary>Starts or stops launching Colitu when the user signs in to Windows.</summary>
    public async Task SetLaunchAtStartupAsync(bool enabled)
    {
        _config.GuiItem.AutoRun = enabled;
        await ConfigHandler.SaveConfig(_config);
        await AutoStartupHandler.UpdateTask(_config);
    }

    public bool LaunchAtStartup => _config.GuiItem.AutoRun;

    /// <summary>
    /// A crash or forced shutdown while connected leaves Windows pointing at the
    /// dead local proxy, which cuts the internet. Undo that before anything else.
    /// </summary>
    public async Task RecoverFromPreviousRunAsync()
    {
        try
        {
            await RecoverKillSwitchAsync();
            if (KillSwitchEngaged || _previousRunStatus is ColituVpnStatus.Connected or ColituVpnStatus.Connecting or ColituVpnStatus.Reconnecting)
            {
                // Held from the last run (or just released after it): the system resolver is
                // blocked or not back yet, so the first panel call at start-up failed ("no such
                // host") and the account and plan stayed unloaded until the next connect pinned the
                // addresses. Pin them now (DoH from this app is allowed).
                using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                await ColituPinnedHosts.RefreshAsync(PinnedAppHosts(), budget.Token);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituVpnService.RecoverKillSwitchAsync", ex);
        }
        try
        {
            await EnsureCoreReadyAsync();
            var wasActive = _previousRunStatus is ColituVpnStatus.Connected or ColituVpnStatus.Connecting or ColituVpnStatus.Reconnecting;
            if (!wasActive && _config.SystemProxyItem.SysProxyType != ESysProxyType.ForcedChange)
            {
                return;
            }

            LogConnection($"Recovering after unclean exit (saved status={_previousRunStatus}, proxy={_config.SystemProxyItem.SysProxyType})");
            KillOrphanCoreProcesses();
            await SysProxyHandler.UpdateSysProxy(_config, true);
            if (_config.SystemProxyItem.SysProxyType == ESysProxyType.ForcedChange)
            {
                _config.SystemProxyItem.SysProxyType = ESysProxyType.ForcedClear;
                await ConfigHandler.SaveConfig(_config);
            }
            await RestoreRoutingPreferenceAsync();
            ConnectedAt = null;
            SetStatus(ColituVpnStatus.Disconnected);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituVpnService.RecoverFromPreviousRunAsync", ex);
        }
    }

    /// <summary>
    /// Windows is signing out or shutting down: stop the core and hand the system
    /// proxy back, otherwise the next boot starts with Windows pointing at a dead
    /// local proxy until Colitu runs again. Blocks for at most a few seconds.
    /// </summary>
    public void CleanupForSessionEnd()
    {
        try
        {
            // Without this the watchdog sees the stopped core as a dropped tunnel and starts
            // reconnecting (proxy, TUN) while Windows is shutting down.
            // The watchdog keeps running: shutdown can still be cancelled (an app refusing to
            // close), and the next connection must be watched again. It ignores a stopped tunnel.
            _userDisconnected = true;
            _connectCts?.Cancel();
            SetStatus(ColituVpnStatus.Disconnected);
            Task.Run(async () =>
            {
                await StopCoreAsync();
                await SysProxyHandler.UpdateSysProxy(_config, true);
                await RestoreProxyPreferenceAsync();
                // Windows is shutting down on purpose: not a crash, so the next boot starts open.
                await ReleaseKillSwitchAsync("Windows session ending");
            }).Wait(TimeSpan.FromSeconds(6));
            LogConnection("Session ending: core stopped and system proxy restored");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituVpnService.CleanupForSessionEnd", ex);
        }
    }

    /// <summary>Stops the tunnel on sign-out and forgets cached connection settings.</summary>
    public async Task ForgetAccountAsync()
    {
        // Also after a failed reconnect (Error): the kill switch may still hold the internet
        // closed and the background retry loop must stop.
        if (Status != ColituVpnStatus.Disconnected || KillSwitchEngaged)
        {
            await DisconnectAsync();
        }
        DeleteConfigCache();
        DeleteRecoverySet();
        _session = _session with { RecoveryAttemptAt = null, RecoveryAttemptUser = null };
        try
        {
            // The imported transports carry this account's server credentials.
            await ConfigHandler.RemoveServersViaSubid(_config, ColituSubId, false);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituVpnService.ForgetAccountAsync", ex);
        }
        _lastServers = [];
        _multihopRoutes = [];
        _pings.Clear();
        Rotation = null;
        SelectedServer = null;
        ConnectedServer = null;
    }

    // ── Health watch: keep the tunnel working ──────────────────────────────
    private void StartWatchdog()
    {
        // Every second: cheap (is the core running?); the traffic checks keep their own pace.
        _watchdog = new Timer(_ => _ = WatchdogTickAsync(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        Microsoft.Win32.SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode == Microsoft.Win32.PowerModes.Resume && Status == ColituVpnStatus.Connected)
            {
                _ = VerifyAfterResumeAsync();
            }
        };
        // Wi-Fi ↔ Ethernet, another Wi-Fi network, a USB modem: the core stays bound to the adapter
        // it started on and keeps sending into the old one. Windows raises several events per change
        // (our own TUN adapter too), so act once things have settled.
        _networkChangeDebounce = new Timer(_ => _ = OnNetworkSettledAsync(), null, Timeout.Infinite, Timeout.Infinite);
        NetworkChange.NetworkAddressChanged += (_, _) => _networkChangeDebounce?.Change(TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
    }

    private async Task WatchdogTickAsync()
    {
        // The timer fires every second while a probe can take up to 14 s: one tick at a time,
        // or two overlapping ticks would both start an automatic reconnect.
        if (Interlocked.Exchange(ref _watchdogBusy, 1) == 1)
        {
            return;
        }
        try
        {
            await WatchdogCheckAsync();
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituVpnService.Watchdog", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _watchdogBusy, 0);
        }
    }

    /// <summary>A connected tunnel nobody else is changing right now.</summary>
    private bool CanHeal() => Status == ColituVpnStatus.Connected && _connectionLock.CurrentCount > 0 && !_autoReconnecting && !_userDisconnected;

    private async Task WatchdogCheckAsync()
    {
        if (!CanHeal())
        {
            return;
        }
        // Both processes: with Xray behind a sing-box TUN front, the front can die while Xray
        // (and every probe through its loopback inbound) keeps answering, and the TUN routes
        // that kept traffic in the tunnel are gone. OnCoreExited may have skipped it while a
        // check was running, so the tick looks for it too.
        if (!CoreManager.Instance.IsCoreRunning || CoreManager.Instance.HasPreCoreExited)
        {
            await RecoverTunnelAsync("VPN core stopped unexpectedly", coreStopped: true);
            return;
        }

        await WatchTrafficAsync(Environment.TickCount64);
    }

    private bool CanWatch() => CanHeal();

    private async Task OnWatchHealthyAsync(long now)
    {
        _failedChecks = 0;
        _offlineVerdicts = 0;
        _offlineNoticeShown = false;
        _waitingForNetwork = false;
        // Every 30 s, and on every check while the resolver is silent.
        if (_failedDnsChecks > 0 || now - _lastDnsCheckAt >= 30_000)
        {
            _lastDnsCheckAt = now;
            await CheckTunnelDnsAsync();
        }
    }

    private Task OnWatchOfflineAsync()
    {
        if (!_offlineNoticeShown)
        {
            _offlineNoticeShown = true;
            LogConnection("No internet outside the tunnel either; keeping the tunnel and waiting for the connection to return");
            Notice?.Invoke("warn.noInternet");
        }
        return Task.CompletedTask;
    }

    /// <summary>3 misses, no spare: Hysteria2 switches transport; TCP restarts the core, then reconnects.</summary>
    private async Task OnWatchPrimaryDeadAsync(string detail)
    {
        if (_activeTransport == "hysteria2")
        {
            await OnHysteriaStalledAsync(detail);
            return;
        }
        await RecoverTunnelAsync($"tunnel carries no traffic: {detail}", deferAs: ColituWatchAction.PrimaryDead);
    }

    private async Task OnWatchReconnectAsync(string detail)
    {
        if (_watch.SpareDeadUnreplaced)
        {
            LogConnection($"The primary misses behind a dead spare ({detail}); reconnecting");
            await OnTunnelLostAsync(fresh: true);
            return;
        }
        await RecoverTunnelAsync($"both paths carry no traffic: {detail}", deferAs: ColituWatchAction.Reconnect);
    }

    private async Task AllowSpareServerAsync(ColituSpareServer spare)
    {
        _killSwitchServers ??= [];
        _killSwitchServers.AddRange(spare.Items.Select(item => (item.Item.Address ?? "", item.Item.Port, item.Protocol)));
        _killSwitchPortHopping |= spare.Items.Any(item => UsesPortHopping(item.Item));
        if (KillSwitchEngaged)
        {
            await ApplyKillSwitchForConnectedTunnelAsync();
        }
    }

    private Task<bool> ReloadCoreForSpareAsync(string reason) => TryRestartCoreAsync(reason);



    // ── Adaptive Connect 2.0: mid-session watcher, spare probe, spare swap ──
    private static readonly TimeSpan WatchProbeTimeout = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Every 5 s for the first 90 s after the connect, then every 30 s (every transport). With a spare
    /// the primary alone (colitu-verify) and the normal path are checked at once; see <see cref="ColituTunnelWatch"/>.
    /// Between the rounds the spare's own health is probed and a replacement swapped in when quiet.
    /// </summary>
    private async Task WatchTrafficAsync(long now)
    {
        SampleTraffic(now);
        var sinceConnect = ConnectedAt is { } at ? DateTimeOffset.Now - at : TimeSpan.Zero;
        if (now - _lastTrafficCheckAt < (long)ColituTunnelWatch.Interval(sinceConnect).TotalMilliseconds)
        {
            await WatchSpareAsync(now);
            return;
        }
        _lastTrafficCheckAt = now;
        var (port, credentials) = TunnelCheckTarget();
        var verify = _verifyInbound;
        var normalTask = ProbeThroughLocalProxyAsync(port, 1, roundTimeout: WatchProbeTimeout, credentials: credentials);
        var primaryTask = verify == null ? null
            : ProbeThroughLocalProxyAsync(verify.Port, 1, roundTimeout: WatchProbeTimeout, credentials: new NetworkCredential(verify.User, verify.Password));
        var normal = await normalTask;
        var primary = primaryTask == null ? null : await primaryTask;
        if (!CanWatch())
        {
            return;
        }
        var miss = !normal.Success || primary is { Success: false };
        var online = !miss || await InternetReachableDirectAsync();
        if (!CanWatch())
        {
            return;
        }
        var action = _watch.Round(primary?.Success, normal.Success, online);
        var detail = primary is { Success: false } ? primary.Detail : normal.Detail;
        if (miss && action == ColituWatchAction.None)
        {
            LogConnection($"Tunnel check missed (primary {(primary == null ? "-" : primary.Success ? "ok" : "miss")} {_watch.PrimaryMisses}/{ColituTunnelWatch.MissesForDead}, normal {(normal.Success ? "ok" : "miss")} {_watch.NormalMisses}/{ColituTunnelWatch.MissesForDead}): {detail}");
        }
        switch (action)
        {
            case ColituWatchAction.None:
                if (normal.Success)
                {
                    await OnWatchHealthyAsync(now);
                }
                return;
            case ColituWatchAction.Offline:
                await OnWatchOfflineAsync();
                return;
            case ColituWatchAction.SpareCarries:
                OnSpareCarries();
                return;
            case ColituWatchAction.PrimaryDead:
                await OnWatchPrimaryDeadAsync(detail);
                return;
            case ColituWatchAction.Reconnect:
                await OnWatchReconnectAsync(detail);
                return;
        }
    }

    /// <summary>
    /// The primary is dead and the warm spare carries the traffic: no reconnect. The primary is marked
    /// for the next connect; the spare's server and transport become the remembered good ones (they lead it).
    /// </summary>
    private void OnSpareCarries()
    {
        var plan = _sparePlan;
        var serverId = ConnectedServer?.Id;
        var now = DateTimeOffset.UtcNow;
        if (plan == null)
        {
            return;
        }
        var shortMark = _adaptive.MarkMidSessionStall(_networkKey, serverId, _activeTransport, now, provisional: true);
        LogConnection($"{serverId}/{_activeTransport} is dead, the warm spare {plan.ServerId}/{plan.Protocol} carries the traffic; it leads the next connect{(shortMark ? " (proven here: short penalty)" : "")}");
        if (plan.ServerId is { Length: > 0 } spareServer)
        {
            _adaptive.RememberGoodServer(_networkKey, spareServer, now);
            _adaptive.RememberGoodTransport(_networkKey, spareServer, plan.Protocol, now);
        }
        SaveState();
    }

    /// <summary>
    /// Spare health probe while the primary is healthy (60 s for a UDP spare, 180 s for a TCP one);
    /// 2 misses while online: a replacement by the spare rules, swapped in when the tunnel is quiet.
    /// </summary>
    private async Task WatchSpareAsync(long now)
    {
        if (_pendingSpare != null)
        {
            await TrySwapSpareAsync(now);
            return;
        }
        var plan = _sparePlan;
        var spareVerify = _spareVerifyInbound;
        if (plan == null || spareVerify == null || _watch.PrimaryMisses > 0 || _watch.PrimaryDeclaredDead || _watch.SpareDeadUnreplaced
            || now - _lastSpareProbeAt < (long)ColituSpareHealth.Interval(plan.Protocol).TotalMilliseconds)
        {
            return;
        }
        _lastSpareProbeAt = now;
        var probe = await ProbeThroughLocalProxyAsync(spareVerify.Port, 1, roundTimeout: TimeSpan.FromSeconds(5), credentials: new NetworkCredential(spareVerify.User, spareVerify.Password));
        if (!CanWatch() || !ReferenceEquals(plan, _sparePlan))
        {
            return;
        }
        var online = probe.Success || await InternetReachableDirectAsync();
        LogConnection(probe.Success ? $"spare probe ok: {Describe(plan)} in {probe.LatencyMs} ms" : $"spare probe miss: {Describe(plan)} ({probe.Detail})");
        if (!_spareHealth.Probe(probe.Success, online))
        {
            return;
        }
        var replacement = await FindReplacementSpareAsync(plan);
        if (replacement == null)
        {
            _watch.SpareDeadUnreplaced = true;
            LogConnection($"spare replaced: nothing can replace {Describe(plan)} (dead); keeping it, 2 primary misses reconnect");
            return;
        }
        _pendingSpare = replacement;
        ResetSpareSwapDeferral();
        await TrySwapSpareAsync(now);
    }

    /// <summary>A dead spare's replacement: another ranked server first (not the dead spare's), then the primary's own server.</summary>
    private async Task<ColituSparePlan?> FindReplacementSpareAsync(ColituSparePlan dead)
    {
        var primary = await ConfigHandler.GetDefaultServer(_config);
        var primaryProtocol = _activeTransport;
        var serverId = ConnectedServer?.Id;
        if (primary == null || primaryProtocol == null)
        {
            return null;
        }
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { SpareKey(dead.ServerId, dead.Protocol) };
        var config = new ColituVpnConfigResponse { ServerId = serverId, Server = ConnectedServer };
        if (IsAutoSelection)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var candidate in RankedServers().Where(item => item.Id is { Length: > 0 } id
                && !string.Equals(id, serverId, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(id, dead.NextServerId, StringComparison.OrdinalIgnoreCase)
                && !_adaptive.IsPenalized(_networkKey, id, now)).Take(2))
            {
                if (await FetchSpareServerAsync(candidate, CancellationToken.None) is not { } fetched)
                {
                    continue;
                }
                _spareServer = fetched;
                if (PlanSpare(primaryProtocol, primary, _currentProfiles, config, ConnectedServer, excluded) is { NextServerId: not null } plan)
                {
                    return plan;
                }
            }
        }
        _spareServer = null;
        return PlanSpare(primaryProtocol, primary, _currentProfiles, config, ConnectedServer, excluded);
    }

    private void ResetSpareSwapDeferral()
    {
        _spareSwapDeferredSince = 0;
        _spareSwapDeferKey = null;
    }

    /// <summary>
    /// Swaps the pending spare in by reloading the core: at once when the tunnel carried under 10 KB in
    /// the last 10 s (or the traffic is unknown), after 2 minutes of deferral also under 32 KB
    /// (<see cref="ColituSpareHealth.SwapDeferral"/>). The log says why only when the reason changes.
    /// </summary>
    private async Task TrySwapSpareAsync(long now)
    {
        var next = _pendingSpare;
        if (next == null)
        {
            return;
        }
        var bytes = TrafficInWindow();
        var deferredFor = _spareSwapDeferredSince == 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(now - _spareSwapDeferredSince);
        var decision = ColituSpareHealth.SwapDeferral(bytes < 0 ? null : bytes, deferredFor);
        if (!decision.Now)
        {
            if (_spareSwapDeferredSince == 0)
            {
                _spareSwapDeferredSince = now;
            }
            if (_spareSwapDeferKey != decision.Key)
            {
                _spareSwapDeferKey = decision.Key;
                LogConnection($"spare swap deferred: {decision.Reason}");
            }
            return;
        }
        if (decision.Key == "light")
        {
            LogConnection($"spare swap: {decision.Reason}");
        }
        ResetSpareSwapDeferral();
        var old = _sparePlan;
        _pendingSpare = null;
        if (next.NextServerId != null && _spareServer is { } spareServer)
        {
            await AllowSpareServerAsync(spareServer);
        }
        _sparePlan = next;
        _spareHealth.Reset();
        LogConnection($"spare replaced: {(old == null ? "none" : Describe(old))} → {Describe(next)} ({next.Reason}; the old one missed {ColituSpareHealth.MissesForDead} probes)");
        if (!await ReloadCoreForSpareAsync("spare replaced") && !_userDisconnected)
        {
            await OnTunnelLostAsync();
        }
    }

    private void SampleTraffic(long now)
    {
        var bytes = ColituNetwork.PhysicalBytes();
        if (bytes < 0)
        {
            return;
        }
        _trafficSamples.Enqueue((now, bytes));
        while (_trafficSamples.Count > 0 && now - _trafficSamples.Peek().Tick > 15_000)
        {
            _trafficSamples.Dequeue();
        }
    }

    /// <summary>Bytes over the physical adapter in the last 10 s; -1 when unknown (counts as quiet).</summary>
    private long TrafficInWindow()
    {
        var samples = _trafficSamples.ToArray();
        if (samples.Length < 2)
        {
            return -1;
        }
        var latest = samples[^1];
        var start = samples.LastOrDefault(sample => latest.Tick - sample.Tick >= (long)ColituSpareHealth.QuietWindow.TotalMilliseconds, samples[0]);
        return Math.Max(0, latest.Bytes - start.Bytes);
    }

    private const int HysteriaStallChecks = ColituTunnelWatch.MissesForDead;
    private static readonly TimeSpan TransportSwitchMinInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Hysteria2 stopped carrying traffic in the middle of a session (Russian mobile networks
    /// throttle long-lived UDP flows). With the internet still there, the transport is demoted on
    /// this server and network for 6 hours and the same server is reconnected with the next transport,
    /// silently (one log line, no notice), at most once a minute. Without internet the usual
    /// recovery decides (it tells the user and waits).
    /// </summary>
    private async Task OnHysteriaStalledAsync(string detail)
    {
        if (!await InternetReachableDirectAsync())
        {
            await RecoverTunnelAsync($"tunnel carries no traffic: {detail}");
            return;
        }
        if (!CanHeal() || !ShouldSwitchTransport(_lastTransportSwitch, DateTimeOffset.UtcNow))
        {
            // Switched less than a minute ago: the verdict stays (one miss short), so the switch
            // happens as soon as the gap ends instead of after three more misses.
            _watch.Defer(ColituWatchAction.PrimaryDead);
            return;
        }
        _lastTransportSwitch = DateTimeOffset.UtcNow;
        var server = ConnectedServer;
        var shortMark = _adaptive.MarkMidSessionStall(_networkKey, server?.Id, "hysteria2", DateTimeOffset.UtcNow, provisional: false);
        SaveState();
        LogConnection($"Hysteria2 stalled mid-session ({HysteriaStallChecks} checks, last: {detail}); demoted on this server and network for {(shortMark ? "90 s (proven here: short penalty)" : "6 hours")}, switching to the next transport");
        await SwitchTransportAsync(server);
    }

    /// <summary>At most one automatic transport switch per <see cref="TransportSwitchMinInterval"/>.</summary>
    internal static bool ShouldSwitchTransport(DateTimeOffset? lastSwitch, DateTimeOffset now) =>
        lastSwitch == null || now - lastSwitch.Value >= TransportSwitchMinInterval;

    /// <summary>
    /// Reconnects to <paramref name="server"/> (the node the tunnel ran through) without a notice;
    /// the stall mark puts the demoted transport last. When that fails, the usual automatic
    /// reconnect takes over (with its notices: the tunnel is down then).
    /// </summary>
    private async Task SwitchTransportAsync(ColituVpnServer? server)
    {
        if (Interlocked.Exchange(ref _autoReconnectBusy, 1) == 1)
        {
            return;
        }
        // "Best server" ranks again: the server that just worked on this network comes first
        // (unless it is penalized), and a failure moves on to the next one.
        var target = IsAutoSelection ? null
            : server is { IsMultihop: false, Id.Length: > 0 } ? server
            : SelectedServer ?? FindServer(_session.SelectedServerId);
        var failed = false;
        _autoReconnecting = true;
        try
        {
            if (Preferences.KillSwitchEnabled && !KillSwitchEngaged)
            {
                // Before the core goes down: nothing may leave outside the tunnel meanwhile.
                await EngageKillSwitchAsync("switching transport");
            }
            await _connectionLock.WaitAsync();
            try
            {
                if (_userDisconnected)
                {
                    return;
                }
                SetStatus(ColituVpnStatus.Reconnecting);
                await StopCoreAsync();
                await ConnectCoreAsync(target, quick: true);
                failed = Status != ColituVpnStatus.Connected;
            }
            finally
            {
                _connectionLock.Release();
            }
        }
        catch (Exception ex)
        {
            failed = true;
            LogConnection($"Transport switch did not connect: {ex.Message}");
        }
        finally
        {
            _autoReconnecting = false;
            Interlocked.Exchange(ref _autoReconnectBusy, 0);
        }
        if (failed && !_userDisconnected)
        {
            await OnTunnelLostAsync(fresh: true);
        }
    }

    /// <summary>
    /// The tunnel stopped carrying traffic. First look outside the tunnel which part broke:
    /// with no internet at all nothing can be fixed (keep the tunnel, tell the user, wait); when
    /// only the server is unreachable a restart can't help (reconnect, the panel may move to
    /// another node); otherwise restart the core on the same server and transport (a second or
    /// two), and only when that does not help, reconnect from scratch (next transport).
    /// </summary>
    /// <param name="deferAs">The watcher verdict this recovery answers; when nothing could be done now, it is kept for the next miss.</param>
    private async Task RecoverTunnelAsync(string reason, bool coreStopped = false, ColituWatchAction? deferAs = null)
    {
        if (!ColituNetwork.HasPhysicalNetwork())
        {
            if (deferAs is { } noNetwork)
            {
                _watch.Defer(noNetwork);
            }
            if (!_waitingForNetwork)
            {
                _waitingForNetwork = true;
                LogConnection($"{reason}; no network adapter is connected, waiting for the network");
            }
            return;
        }
        _waitingForNetwork = false;
        LogConnection(reason);
        if (!coreStopped)
        {
            var path = await DiagnosePathAsync();
            if (!CanHeal())
            {
                if (deferAs is { } cannotHeal)
                {
                    _watch.Defer(cannotHeal);
                }
                return;
            }
            // Three "offline" verdicts in a row (about a minute) while an adapter is connected: the
            // check itself may be what is blocked, so the restart goes ahead anyway.
            if (path == ColituPathState.Offline && ++_offlineVerdicts < 3)
            {
                if (deferAs is { } offline)
                {
                    _watch.Defer(offline);
                }
                LogConnection("No internet outside the tunnel either; keeping the tunnel and waiting for the connection to return");
                if (!_offlineNoticeShown)
                {
                    _offlineNoticeShown = true;
                    Notice?.Invoke("warn.noInternet");
                }
                return;
            }
            _offlineVerdicts = 0;
            if (path == ColituPathState.ServerUnreachable)
            {
                // Not the transport's fault: no stall mark, and fresh settings from the panel
                // (which can move to a healthy node) instead of the cached ones for this server.
                LogConnection("The server does not answer outside the tunnel either; reconnecting");
                await OnTunnelLostAsync(fresh: true);
                return;
            }
        }
        if (Preferences.KillSwitchEnabled && !KillSwitchEngaged)
        {
            // Before the core goes down for the restart: nothing may leave outside the tunnel meanwhile.
            await EngageKillSwitchAsync("restarting the VPN core");
        }
        if (await TryRestartCoreAsync(reason) || _userDisconnected)
        {
            return;
        }
        if (_activeTransport != null)
        {
            // Only with the internet there outside the tunnel: an outage is nobody's fault.
            if (await InternetReachableDirectAsync())
            {
                var shortMark = _adaptive.MarkMidSessionStall(_networkKey, ConnectedServer?.Id, _activeTransport, DateTimeOffset.UtcNow, provisional: true);
                SaveState();
                LogConnection(shortMark
                    ? $"Transport {_activeTransport} stalled; proven here: short penalty, it goes last on this server and network for 90 s"
                    : $"Transport {_activeTransport} stalled; it goes last on this server and network for 10 minutes (6 h once another transport carries traffic here)");
            }
            else
            {
                LogConnection($"Transport {_activeTransport} carried nothing, but there is no internet outside the tunnel either; not marked");
            }
        }
        LogConnection("Restarting the core did not bring traffic back; reconnecting");
        await OnTunnelLostAsync();
    }

    /// <summary>A Russian and a foreign address that accept TCP connections (Yandex DNS, Cloudflare).</summary>
    // Port 443 only: sing-box's strict route refuses port 53 outside the tunnel ("access denied").
    private static readonly (string Name, IPAddress Address, int Port)[] PathReferences =
    [
        ("yandex-dns", IPAddress.Parse("77.88.8.8"), 443),
        ("yandex", IPAddress.Parse("77.88.55.242"), 443),
        ("cloudflare", IPAddress.Parse("1.1.1.1"), 443),
        ("google", IPAddress.Parse("8.8.8.8"), 443),
    ];

    /// <summary>
    /// TCP connects outside the tunnel (pinned to the physical adapter) to the server and to
    /// <see cref="PathReferences"/>; logged, so a drop can be told apart afterwards too.
    /// </summary>
    private async Task<ColituPathState> DiagnosePathAsync()
    {
        try
        {
            var index = ColituNetwork.PhysicalInterfaceIndex();
            if (index == null)
            {
                // Not pinned (PPPoE, mobile broadband) the connects would go through the dead tunnel.
                return ColituPathState.Reachable;
            }
            var server = await CurrentServerEndpointAsync();
            var serverCheck = server is { } endpoint
                ? ColituLatency.ConnectMsAsync(endpoint.Address, endpoint.Port, index, 3000)
                : Task.FromResult<int?>(null);
            var references = PathReferences.Select(item => ColituLatency.ConnectMsAsync(item.Address, item.Port, index, 3000)).ToArray();
            await Task.WhenAll(references.Append(serverCheck));
            var answers = PathReferences.Select((item, i) => (item.Name, Ms: references[i].Result)).ToList();
            if (server != null)
            {
                answers.Insert(0, ("server", serverCheck.Result));
            }
            LogConnection("Direct path check (outside the tunnel): "
                + string.Join(", ", answers.Select(item => $"{item.Name}={(item.Ms is { } ms ? $"{ms} ms" : "no answer")}")));
            return ClassifyPath(server != null, serverCheck.Result, references.Select(task => task.Result));
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituVpnService.DiagnosePath", ex);
            return ColituPathState.Reachable;
        }
    }

    /// <summary>
    /// Whether any <see cref="PathReferences"/> address answers outside the tunnel (pinned to the
    /// physical adapter). True when that can't be checked (no physical adapter index).
    /// </summary>
    private static async Task<bool> InternetReachableDirectAsync()
    {
        if (!ColituNetwork.HasPhysicalNetwork())
        {
            return false;
        }
        var index = ColituNetwork.PhysicalInterfaceIndex();
        if (index == null)
        {
            return true;
        }
        var checks = PathReferences.Select(item => ColituLatency.ConnectMsAsync(item.Address, item.Port, index, 2500)).ToList();
        while (checks.Count > 0)
        {
            var done = await Task.WhenAny(checks);
            if (await done != null)
            {
                return true;
            }
            checks.Remove(done);
        }
        return false;
    }

    internal static ColituPathState ClassifyPath(bool serverKnown, int? serverMs, IEnumerable<int?> referenceMs)
    {
        var internet = referenceMs.Any(ms => ms != null) || (serverKnown && serverMs != null);
        if (!internet)
        {
            return ColituPathState.Offline;
        }
        return serverKnown && serverMs == null ? ColituPathState.ServerUnreachable : ColituPathState.Reachable;
    }

    /// <summary>
    /// The current server's address and the TCP port of its transport. Null for Hysteria2: it
    /// is UDP, and it is chosen exactly where TCP to the server may be blocked, so a TCP check
    /// would call a working server unreachable.
    /// </summary>
    private async Task<IPEndPoint?> CurrentServerEndpointAsync()
    {
        var profile = await ConfigHandler.GetDefaultServer(_config);
        if (profile == null || profile.ConfigType == EConfigType.Hysteria2
            || !IPAddress.TryParse(profile.Address, out var address) || !ColituLatency.IsPublic(address))
        {
            return null;
        }
        return profile.Port is > 0 and < 65536 ? new IPEndPoint(address, profile.Port) : null;
    }

    /// <summary>
    /// Restarts the core with the profile it already runs: new connections to the server, the
    /// adapter that carries the internet now, a fresh DNS client. True when traffic flows again
    /// (or when a connect, switch or disconnect took over meanwhile).
    /// </summary>
    /// <param name="quick">
    /// One short check round: after a network change the transport may simply not work on the new
    /// network (a mobile operator blocking it), and the connect that follows a failure picks another.
    /// </param>
    private async Task<bool> TryRestartCoreAsync(string reason, bool quick = false)
    {
        if (!await _connectionLock.WaitAsync(0))
        {
            return true;
        }
        var token = CancellationToken.None;
        try
        {
            if (Status != ColituVpnStatus.Connected || _userDisconnected)
            {
                return true;
            }
            // Disconnect, a server switch and Windows shutting down cancel it like a connect.
            var attempt = _connectCts = new CancellationTokenSource();
            token = attempt.Token;
            SetStatus(ColituVpnStatus.Reconnecting);
            LogConnection($"Restarting the VPN core on the same server ({reason})");
            _lastCoreMessage = null;
            await StopCoreAsync();
            ResetDirectConnections();
            await ReloadCoreAsync(token);
            var probe = quick
                ? await VerifyConnectionActiveAsync(ConnectedServer, 1, token, TimeSpan.FromSeconds(5))
                : await VerifyConnectionActiveAsync(ConnectedServer, 2, token);
            if (token.IsCancellationRequested || _userDisconnected)
            {
                // The user's action, waiting for the lock, takes it from here.
                return true;
            }
            if (!probe.Success)
            {
                return false;
            }
            _failedChecks = 0;
            _failedDnsChecks = 0;
            SetStatus(ColituVpnStatus.Connected);
            LogConnection("VPN core restarted; traffic flows again");
            Notice?.Invoke("info.reconnected");
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            LogConnection("VPN core restart cancelled");
            if (_userDisconnected)
            {
                // Windows is shutting down (or the user disconnected): no core may stay behind.
                await StopCoreAsync();
            }
            return true;
        }
        catch (Exception ex)
        {
            LogConnection($"VPN core restart failed: {ex.Message}");
            return false;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <summary>
    /// In TUN mode apps resolve names through the core's DNS (DoH over the tunnel). That
    /// connection can hang while the tunnel itself still carries traffic, and then every app
    /// waits for names. Ask the core's resolver directly; three silences in a row restart the core.
    /// </summary>
    private async Task CheckTunnelDnsAsync()
    {
        if (!_config.TunModeItem.EnableTun)
        {
            return;
        }
        var answered = await ColituNetwork.TunnelDnsAnswersAsync(TimeSpan.FromSeconds(5));
        if (answered == null)
        {
            return;
        }
        if (answered.Value)
        {
            if (!_dnsCheckWorks || _failedDnsChecks > 0)
            {
                LogConnection("Tunnel DNS answers");
            }
            _dnsCheckWorks = true;
            _failedDnsChecks = 0;
            return;
        }
        // Never answered in this session: the check can't reach the resolver here, which says
        // nothing about the apps.
        if (!_dnsCheckWorks || !CanHeal())
        {
            return;
        }
        if (++_failedDnsChecks < 3)
        {
            LogConnection($"Tunnel DNS did not answer ({_failedDnsChecks}/3)");
            return;
        }
        _failedDnsChecks = 0;
        // Per 10 minutes: a restart, then a reconnect; after that only wait, so a resolver that
        // keeps failing can't restart the tunnel every minute.
        var now = DateTimeOffset.UtcNow;
        if (now - _dnsRecoveryWindowStart > TimeSpan.FromMinutes(10))
        {
            _dnsRecoveryWindowStart = now;
            _dnsRecoveries = 0;
        }
        if (++_dnsRecoveries > 2)
        {
            if (_dnsRecoveries == 3)
            {
                LogConnection("Tunnel DNS keeps failing; no further restarts for a while");
            }
            return;
        }
        if (_dnsRecoveries == 1)
        {
            if (!await TryRestartCoreAsync("tunnel DNS stopped answering"))
            {
                if (!_userDisconnected)
                {
                    await OnTunnelLostAsync();
                }
                return;
            }
            // The restart's HTTP probe sends names to the server and never asks this resolver.
            if (_userDisconnected || Status != ColituVpnStatus.Connected
                || await ColituNetwork.TunnelDnsAnswersAsync(TimeSpan.FromSeconds(5)) != false)
            {
                return;
            }
            _dnsRecoveries = 2;
        }
        if (CanHeal())
        {
            LogConnection("Tunnel DNS still does not answer; reconnecting");
            await OnTunnelLostAsync();
        }
    }

    /// <summary>The access network was replaced: the old client network no longer describes this PC.</summary>
    private void ForgetClientNetwork()
    {
        var old = _session.ClientNetwork;
        if (string.IsNullOrEmpty(old))
        {
            return;
        }
        _session = _session with { ClientNetwork = null };
        SaveState();
        LogConnection($"access network replaced, client network {old} unknown until the next server list");
    }

    private async Task OnNetworkSettledAsync()
    {
        if (!CanHeal())
        {
            return;
        }
        if (Interlocked.Exchange(ref _watchdogBusy, 1) == 1)
        {
            // A check is running: look again once it is done.
            _networkChangeDebounce?.Change(TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
            return;
        }
        try
        {
            var bound = _config.CoreBasicItem.BindInterface;
            var physical = ColituNetwork.PhysicalInterfaceName();
            var network = ColituNetwork.PhysicalNetworkFingerprint();
            var adapterChanged = bound.IsNotEmpty() && physical != null && !string.Equals(physical, bound, StringComparison.OrdinalIgnoreCase);
            // The same adapter on another network (home Wi-Fi to a phone hotspot): the old
            // connections, the tunnel's DNS connection among them, are dead, yet nothing failed
            // yet. Until 2.8.3 the app kept saying "protected" for up to 90 s without names.
            var networkChanged = network != null && _verifiedNetwork != null && !string.Equals(network, _verifiedNetwork, StringComparison.Ordinal);
            if (!CanHeal() || physical == null || (!adapterChanged && !networkChanged))
            {
                return;
            }
            LogConnection(adapterChanged
                ? $"Network changed ({bound} -> {physical}); moving the tunnel to the new adapter"
                : $"Network changed on {physical} (new address or gateway); restarting the tunnel there");
            // Marks from now on belong to the new network. Its ISP (client network) is only learnt at the
            // next server list fetched with the VPN off; until then it is the "unknown network" key, not the old ISP's.
            ForgetClientNetwork();
            _linkKind = null;
            _networkKey = CurrentNetworkKey();
            if (!await TryRestartCoreAsync("network changed", quick: true) && !_userDisconnected)
            {
                await OnTunnelLostAsync();
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituVpnService.NetworkChanged", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _watchdogBusy, 0);
        }
    }

    /// <param name="fresh">Ask the panel for settings first: the server itself is unreachable, so the cached ones point at it.</param>
    private async Task OnTunnelLostAsync(bool fresh = false)
    {
        if (_userDisconnected)
        {
            return;
        }
        if (Preferences.KillSwitchEnabled && await EngageKillSwitchAsync("tunnel lost"))
        {
            Notice?.Invoke("info.killSwitch");
        }
        await AutoReconnectAsync(fresh);
    }

    private async Task VerifyAfterResumeAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(4));
        // One check at a time with the watchdog, or both could start a reconnect.
        if (Interlocked.Exchange(ref _watchdogBusy, 1) == 1)
        {
            return;
        }
        try
        {
            if (!CanHeal())
            {
                return;
            }
            var (port, credentials) = TunnelCheckTarget();
            var probe = await ProbeThroughLocalProxyAsync(port, credentials: credentials);
            if (!probe.Success && CanHeal())
            {
                // Connections to the server died while the computer slept.
                await RecoverTunnelAsync($"Tunnel check after resume failed: {probe.Detail}");
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituVpnService.VerifyAfterResume", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _watchdogBusy, 0);
        }
    }

    private async Task AutoReconnectAsync(bool fresh = false)
    {
        if (Interlocked.Exchange(ref _autoReconnectBusy, 1) == 1)
        {
            return;
        }
        _autoReconnecting = true;
        try
        {
            for (var attempt = 1; attempt <= 3 && !_userDisconnected; attempt++)
            {
                try
                {
                    // First quickly (cached settings if the panel is slow); then fresh ones from the panel.
                    await ReconnectAsync(quick: attempt == 1 && !fresh);
                    if (Status == ColituVpnStatus.Connected)
                    {
                        Notice?.Invoke("info.reconnected");
                    }
                    return;
                }
                catch (ColituPlanRequiredException)
                {
                    // Nothing to reconnect to until the plan is renewed: don't keep the internet closed.
                    await ReleaseKillSwitchAsync("no active plan");
                    Notice?.Invoke("err.noPlan");
                    return;
                }
                catch (ColituDevicePausedException)
                {
                    // Another device took this plan's place: no reconnect until the user decides.
                    _userDisconnected = true;
                    Notice?.Invoke("paused.notice");
                    return;
                }
                catch (Exception ex)
                {
                    LogConnection($"Automatic reconnect attempt {attempt} failed: {ex.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 3));
                }
            }

            if (_userDisconnected)
            {
                return;
            }

            LastError = Loc.I["err.reconnectFailed"];
            SetStatus(ColituVpnStatus.Error);
            Notice?.Invoke("err.reconnectFailed");

            // With the kill switch holding the connection closed, keep retrying in
            // the background so the internet returns as soon as the VPN can. "Best server" moves
            // on each time: the servers that failed are penalized on this network and rank last
            // (the oldest penalty first), so the retries walk down the list instead of hitting
            // the same node every 20 s. A server the user picked stays the target.
            while (!_userDisconnected && KillSwitchEngaged && Status == ColituVpnStatus.Error)
            {
                await Task.Delay(TimeSpan.FromSeconds(20));
                if (_userDisconnected || !KillSwitchEngaged || Status != ColituVpnStatus.Error || _connectionLock.CurrentCount == 0)
                {
                    continue;
                }
                try
                {
                    await ReconnectAsync();
                    if (Status == ColituVpnStatus.Connected)
                    {
                        Notice?.Invoke("info.reconnected");
                    }
                    return;
                }
                catch (ColituPlanRequiredException)
                {
                    await ReleaseKillSwitchAsync("no active plan");
                    Notice?.Invoke("err.noPlan");
                    return;
                }
                catch (ColituDevicePausedException)
                {
                    _userDisconnected = true;
                    Notice?.Invoke("paused.notice");
                    return;
                }
                catch (Exception ex)
                {
                    LogConnection($"Background reconnect failed: {ex.Message}");
                    if (!_userDisconnected)
                    {
                        SetStatus(ColituVpnStatus.Error);
                    }
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _autoReconnectBusy, 0);
            _autoReconnecting = false;
        }
    }

    // ── Kill switch ────────────────────────────────────────────────────────
    // The Colitu kill-switch service (ColituKillSwitchService.exe, LocalSystem) owns persistent WFP
    // filters: a crash of this app or of the core leaves the internet closed. Builds without the
    // service (or a stopped service) fall back to the old dynamic WFP session in this process,
    // which Windows removes when the process dies, and say so.

    private enum KillSwitchBackend
    {
        None,
        Service,
        Dynamic
    }

    private readonly ColituKillSwitch _dynamicKillSwitch = new();
    private readonly ColituKillSwitchClient _killSwitchService = new();
    private readonly SemaphoreSlim _killSwitchLock = new(1, 1);
    private volatile bool _serviceArmed;
    private bool _killSwitchFallbackWarned;
    private bool _killSwitchProxyWarned;
    /// <summary>Server endpoints of the current connection attempt (address, port, transport).</summary>
    private List<(string Address, int Port, string Protocol)>? _killSwitchServers;
    /// <summary>A transport of the current attempt hops over a UDP port range (Hysteria2 mport).</summary>
    private bool _killSwitchPortHopping;
    /// <summary>Armed by a previous run that ended without disarming (crash, power loss); kept for the auto-connect.</summary>
    private volatile bool _killSwitchHeldFromPreviousRun;

    /// <summary>The cores' bootstrap resolvers (EnsureCoreReadyAsync): reachable outside the tunnel to find the DoH servers.</summary>
    private static readonly string[] BootstrapResolvers = ["1.1.1.1", "8.8.8.8"];

    /// <summary>Hosts the app itself calls while the kill switch is armed: the panel API and the update manifest.</summary>
    private static IEnumerable<string?> PinnedAppHosts()
    {
        // Every API base and list URL of the signed endpoint list: failover must never hit the kill switch.
        foreach (var url in ColituAuthService.Instance.PinnedUrls())
        {
            yield return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null;
        }
    }

    /// <summary>True while the kill switch is blocking traffic outside the VPN.</summary>
    public bool KillSwitchEngaged => _serviceArmed || _dynamicKillSwitch.IsEngaged;

    /// <summary>The internet stays blocked because Colitu closed while connected (crash, power loss).</summary>
    public bool KillSwitchHeldAfterCrash => _killSwitchHeldFromPreviousRun && KillSwitchEngaged;

    /// <summary>
    /// With the kill switch on, it stays armed for the whole session in both modes: from before the
    /// first packet of a connection until the user disconnects. In proxy mode that also means apps
    /// that ignore the system proxy have no internet while protected (the user is told once).
    /// </summary>
    private async Task ApplyKillSwitchForConnectedTunnelAsync()
    {
        if (Preferences.KillSwitchEnabled)
        {
            await EngageKillSwitchAsync(EffectiveTunMode ? "TUN session" : "proxy session");
        }
        else
        {
            await ReleaseKillSwitchAsync("kill switch is off");
        }
    }

    /// <summary>
    /// Arms (or re-arms with the current allow list) the kill switch. Re-arming replaces the
    /// filters in one WFP transaction, so there is no open moment between two allow lists.
    /// </summary>
    private async Task<bool> EngageKillSwitchAsync(string reason)
    {
        await _killSwitchLock.WaitAsync();
        try
        {
            var preferences = Preferences;
            var tun = EffectiveTunMode;
            // Known once this connection's settings are imported (the server's country decides the regional rule).
            var directRouting = _killSwitchServers != null && RussianSitesDirect(_routingServerCountry, preferences.PrivacyModeEnabled);
            var arm = BuildKillSwitchArm(preferences, tun, _killSwitchServers, directRouting, BootstrapResolvers,
                keepAfterReboot: preferences.AutoConnectEnabled && LaunchAtStartup, portHopping: _killSwitchPortHopping);

            if (_killSwitchService.ServiceInstalled)
            {
                try
                {
                    var response = await _killSwitchService.ArmAsync(arm, reason);
                    if (response.Ok && response.Status?.Armed == true)
                    {
                        _serviceArmed = true;
                        if (_dynamicKillSwitch.IsEngaged)
                        {
                            _dynamicKillSwitch.Release();
                        }
                        LogConnection($"Kill switch armed by the service ({reason}; {response.Status.Filters} filters, cores={arm.CoreAccess}, endpoints={arm.Endpoints?.Count ?? 0}, split={arm.SplitMode})");
                        WarnProxyModeOnce(tun);
                        return true;
                    }
                    LogConnection($"Kill-switch service refused to arm: {response.Error}");
                }
                catch (Exception ex) when (ex is ColituKillSwitchUnavailableException or ArgumentException)
                {
                    LogConnection($"Kill-switch service unavailable: {ex.Message}");
                }
            }

            return EngageDynamicFallback(reason, preferences, tun);
        }
        finally
        {
            _killSwitchLock.Release();
        }
    }

    private bool EngageDynamicFallback(string reason, ColituVpnPreferences preferences, bool tun)
    {
        if (!_killSwitchFallbackWarned)
        {
            _killSwitchFallbackWarned = true;
            Notice?.Invoke("warn.killSwitchFallback");
        }
        if (_dynamicKillSwitch.IsEngaged)
        {
            return true;
        }
        var (splitMode, apps, _) = ColituSplitTunnel.KillSwitchEntries(preferences, tun);
        if (splitMode == Colitu.KillSwitch.KsProtocol.SplitOnly)
        {
            // The fallback can only block everything but a few programs: it can't express "only these apps".
            LogConnection($"Kill switch not engaged ({reason}): the in-app fallback can't do \"only selected apps use the VPN\"");
            return false;
        }
        try
        {
            var programs = new List<string>
            {
                Utils.GetBinPath("xray.exe", "xray"),
                Utils.GetBinPath("sing-box.exe", "sing_box"),
                Environment.ProcessPath ?? ""
            };
            programs.AddRange(apps);
            _dynamicKillSwitch.Engage(programs);
            LogConnection($"Kill switch engaged in the app, without the service ({reason}); it ends if the app closes");
            WarnProxyModeOnce(tun);
            return true;
        }
        catch (Exception ex)
        {
            LogConnection($"Kill switch could not be engaged: {ex.Message}");
            Notice?.Invoke("err.killSwitch");
            return false;
        }
    }

    private void WarnProxyModeOnce(bool tun)
    {
        if (!tun && !_killSwitchProxyWarned)
        {
            _killSwitchProxyWarned = true;
            Notice?.Invoke("warn.killSwitchProxy");
        }
    }

    private async Task ReleaseKillSwitchAsync(string reason)
    {
        await _killSwitchLock.WaitAsync();
        try
        {
            _killSwitchHeldFromPreviousRun = false;
            if (_serviceArmed)
            {
                try
                {
                    var response = await _killSwitchService.DisarmAsync(reason);
                    _serviceArmed = !response.Ok;
                    LogConnection(response.Ok ? $"Kill switch released by the service ({reason})" : $"Kill-switch service could not disarm: {response.Error}");
                }
                catch (ColituKillSwitchUnavailableException ex)
                {
                    // The service is gone; its filters are persistent, so remove them here (the app is elevated).
                    LogConnection($"Kill-switch service unavailable while releasing ({ex.Message}); removing its filters directly");
                    try
                    {
                        new Colitu.KillSwitch.WfpFirewall().RemoveFilters();
                        _serviceArmed = false;
                    }
                    catch (Exception removeError)
                    {
                        LogConnection($"Removing the kill-switch filters failed: {removeError.Message}");
                    }
                }
            }
            if (_dynamicKillSwitch.IsEngaged)
            {
                _dynamicKillSwitch.Release();
                LogConnection($"Kill switch released ({reason})");
            }
        }
        finally
        {
            _killSwitchLock.Release();
        }
    }

    /// <summary>The kill switch held the internet closed since the last run; the planned auto-connect will not happen.</summary>
    public async Task ReleaseHeldKillSwitchAsync(string reason)
    {
        if (!_killSwitchHeldFromPreviousRun || Status is ColituVpnStatus.Connected or ColituVpnStatus.Connecting or ColituVpnStatus.Reconnecting)
        {
            return;
        }
        await ReleaseKillSwitchAsync(reason);
        Notice?.Invoke("info.killSwitchRecovered");
        StatusChanged?.Invoke(Status);
    }

    /// <summary>
    /// The allow list for the kill-switch service. Before the server is known (or while its address
    /// is a host name) the cores get only the bootstrap resolvers or, for a host name, full access;
    /// direct routing (the regional rule, split tunnelling) needs the cores to reach anything.
    /// </summary>
    internal static Colitu.KillSwitch.KsArm BuildKillSwitchArm(ColituVpnPreferences preferences, bool tun,
        IEnumerable<(string Address, int Port, string Protocol)>? servers, bool directRouting, IEnumerable<string> resolvers, bool keepAfterReboot,
        bool portHopping = false)
    {
        preferences = preferences.Normalize();
        var endpoints = new List<Colitu.KillSwitch.KsEndpoint>();
        // Port hopping sends to any port of a range (20000-40000) and the service pins single
        // ports: the cores may reach anything (as for a host name); other apps stay blocked.
        var coreFull = directRouting || portHopping;
        foreach (var (address, port, protocol) in servers ?? [])
        {
            if (!Colitu.KillSwitch.KsValidator.TryParseHostAddress(address, out var ip) || port is < 1 or > 65535)
            {
                // The core resolves a host name itself: no address to pin the cores to.
                coreFull = true;
                continue;
            }
            endpoints.Add(new Colitu.KillSwitch.KsEndpoint
            {
                Ip = ip.ToString(),
                Port = port,
                Proto = protocol switch
                {
                    "hysteria2" => "udp",
                    // XHTTP can run over HTTP/3 (QUIC).
                    "vless-xhttp" => "any",
                    _ => "tcp"
                }
            });
        }
        foreach (var resolver in resolvers)
        {
            endpoints.Add(new Colitu.KillSwitch.KsEndpoint { Ip = resolver, Port = 53, Proto = "any" });
        }

        var (splitMode, apps, networks) = ColituSplitTunnel.KillSwitchEntries(preferences, tun);
        if (splitMode != Colitu.KillSwitch.KsProtocol.SplitOff)
        {
            // Split traffic is sent direct by the core itself.
            coreFull = true;
        }
        return new Colitu.KillSwitch.KsArm
        {
            AllowLan = preferences.KillSwitchAllowLan,
            CoreAccess = coreFull ? Colitu.KillSwitch.KsProtocol.CoreAccessFull : Colitu.KillSwitch.KsProtocol.CoreAccessEndpoints,
            Endpoints = coreFull ? [] : endpoints.DistinctBy(item => (item.Ip, item.Port, item.Proto)).Take(Colitu.KillSwitch.KsProtocol.MaxEndpoints).ToList(),
            SplitMode = splitMode,
            Apps = apps,
            Networks = networks,
            KeepAfterReboot = keepAfterReboot
        };
    }

    /// <summary>
    /// Start-up: the service still blocks from a run that ended without disarming. With auto-connect
    /// the next connection takes the filters over (re-arms them); otherwise they are removed and the
    /// user is told why the internet was blocked.
    /// </summary>
    private async Task RecoverKillSwitchAsync()
    {
        if (!_killSwitchService.ServiceInstalled)
        {
            return;
        }
        Colitu.KillSwitch.KsStatus? status;
        try
        {
            status = (await _killSwitchService.StatusAsync()).Status;
        }
        catch (ColituKillSwitchUnavailableException ex)
        {
            LogConnection($"Kill-switch service unavailable at start-up: {ex.Message}");
            return;
        }
        if (status is not { Armed: true })
        {
            return;
        }
        _serviceArmed = true;
        LogConnection($"The kill switch was still armed from the previous run (since {status.ArmedAt:u}, {status.Reason}, owner alive={status.OwnerAlive}, before boot={status.ArmedBeforeBoot})");
        var preferences = Preferences;
        if (preferences.KillSwitchEnabled && preferences.AutoConnectEnabled && ColituAuthService.Instance.HasSession)
        {
            _killSwitchHeldFromPreviousRun = true;
            Notice?.Invoke("info.killSwitchHeld");
            return;
        }
        await ReleaseKillSwitchAsync("previous run ended while armed; no auto-connect");
        Notice?.Invoke("info.killSwitchRecovered");
    }

    /// <summary>The core process ended by itself (crash, killed): close the gap now, then recover.</summary>
    private void OnCoreExited()
    {
        if (Status != ColituVpnStatus.Connected || _userDisconnected)
        {
            return;
        }
        LogConnection("The VPN core process exited unexpectedly");
        _ = Task.Run(async () =>
        {
            try
            {
                if (Preferences.KillSwitchEnabled)
                {
                    await EngageKillSwitchAsync("VPN core exited");
                }
                if (Interlocked.Exchange(ref _watchdogBusy, 1) == 1)
                {
                    // A check is running; the next tick sees the stopped core (main or pre-socks).
                    return;
                }
                try
                {
                    if (CanHeal())
                    {
                        await RecoverTunnelAsync("VPN core stopped unexpectedly", coreStopped: true);
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _watchdogBusy, 0);
                }
            }
            catch (Exception ex)
            {
                Logging.SaveLog("ColituVpnService.OnCoreExited", ex);
            }
        });
    }

    /// <summary>The uninstaller (--colitu-cleanup): the service removes every Colitu WFP object, or this process does.</summary>
    public static void RemoveKillSwitchForUninstall()
    {
        var client = new ColituKillSwitchClient();
        try
        {
            var disarm = client.ServiceInstalled ? client.DisarmAsync("Colitu VPN uninstalled", purge: true) : null;
            if (disarm != null && disarm.Wait(TimeSpan.FromSeconds(10)) && disarm.Result.Ok)
            {
                Logging.SaveLog("Uninstall cleanup: kill-switch service removed its filters");
                return;
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"Uninstall cleanup: kill-switch service unavailable ({ex.GetBaseException().Message})");
        }
        try
        {
            new Colitu.KillSwitch.WfpFirewall().RemoveAll();
            Logging.SaveLog("Uninstall cleanup: kill-switch filters removed directly");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("Uninstall cleanup: removing kill-switch filters failed", ex);
        }
    }

    // ── Cached connection settings (DPAPI protected: they carry credentials) ──
    private void SaveCachedConfig(string key, ColituVpnConfigResponse config)
    {
        try
        {
            var cache = ReadConfigCache();
            cache[key] = config;
            var json = JsonSerializer.SerializeToUtf8Bytes(cache, _jsonOptions);
            var protectedBytes = System.Security.Cryptography.ProtectedData.Protect(json, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            var temp = ConfigCachePath() + ".tmp";
            File.WriteAllBytes(temp, protectedBytes);
            File.Move(temp, ConfigCachePath(), overwrite: true);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituVpnService.SaveCachedConfig", ex);
        }
    }

    private ColituVpnConfigResponse? LoadCachedConfig(string key)
    {
        var cache = ReadConfigCache();
        return cache.TryGetValue(key, out var config)
            && config.OfflineGraceUntil is { } grace && grace > DateTimeOffset.UtcNow
            && config.Candidates.Count > 0
            ? config
            : null;
    }

    /// <summary>
    /// "Best server" with the panel unreachable and nothing cached for it (from Russia the panel
    /// is sometimes unreachable for minutes): any server this account connected to before beats
    /// no connection at all. A server the user picked is never swapped for another one.
    /// </summary>
    private ColituVpnConfigResponse? LoadAnyCachedConfig(IReadOnlyCollection<string>? exclude = null)
    {
        var prefix = $"{ColituAuthService.Instance.CurrentUser?.Id ?? "-"}|";
        return ReadConfigCache()
            .Where(item => item.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(item => LoadCachedConfig(item.Key))
            // A multihop route is a choice of its own, never what "best server" falls back to;
            // a server that failed during this connect is not tried again from the cache.
            .FirstOrDefault(config => config != null && config.Server?.IsMultihop != true
                && (exclude == null || config.ServerId == null || !exclude.Contains(config.ServerId, StringComparer.OrdinalIgnoreCase)));
    }

    private Dictionary<string, ColituVpnConfigResponse> ReadConfigCache()
    {
        try
        {
            if (!File.Exists(ConfigCachePath())) return [];
            var bytes = System.Security.Cryptography.ProtectedData.Unprotect(File.ReadAllBytes(ConfigCachePath()), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<Dictionary<string, ColituVpnConfigResponse>>(bytes, _jsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static string ConfigCachePath() => ColituHardening.UserConfigPath("colitu-config-cache.bin");

    /// <summary>Drops the cached connection settings (they carry the account's server credentials).</summary>
    internal static void DeleteConfigCache()
    {
        try
        {
            if (File.Exists(ConfigCachePath())) File.Delete(ConfigCachePath());
        }
        catch
        {
            // Best effort.
        }
    }

    /// <summary>The cache shared by every Windows user before 2.4.1; only an offline fallback, so it is dropped.</summary>
    private static void DeleteLegacyConfigCache()
    {
        try
        {
            var legacy = Utils.GetConfigPath("colitu-config-cache.bin");
            if (File.Exists(legacy)) File.Delete(legacy);
        }
        catch
        {
            // Best effort.
        }
    }

    internal static bool IsPlanError(ColituApiException ex) => ex.ErrorCode is "ENTITLEMENT_INACTIVE" or "ENTITLEMENT_EXPIRED";

    /// <summary>The plan's device limit paused this computer: no connecting (and no auto-connect) until the user decides.</summary>
    public ColituDeviceOverLimit? DevicePaused
    {
        get => ColituAuthService.Instance.DevicePaused;
        private set => ColituAuthService.Instance.DevicePaused = value;
    }

    internal static ColituDeviceOverLimit? FindDevicePaused(Exception? ex) => ex switch
    {
        null => null,
        ColituApiException { ErrorCode: "DEVICE_OVER_LIMIT" } api => api.OverLimit ?? new ColituDeviceOverLimit(),
        ColituDevicePausedException paused => paused.Info,
        _ => FindDevicePaused(ex.InnerException)
    };

    private static bool IsDevicePaused(Exception ex) => FindDevicePaused(ex) != null;

    /// <summary>
    /// The paused screen: the kill switch must not hold the internet closed while nothing can connect.
    /// </summary>
    public async Task EnterDevicePausedAsync()
    {
        _userDisconnected = true;
        if (Status is ColituVpnStatus.Connected or ColituVpnStatus.Connecting or ColituVpnStatus.Reconnecting || KillSwitchEngaged)
        {
            await DisconnectAsync();
        }
        await ReleaseKillSwitchAsync("device paused");
    }

    internal static bool IsNetworkFailure(Exception ex)
    {
        return ex switch
        {
            HttpRequestException or TaskCanceledException or TimeoutException => true,
            ColituApiException api => (int)api.StatusCode >= 500 || api.ErrorCode is "REFRESH_FAILED",
            _ => ex.InnerException != null && IsNetworkFailure(ex.InnerException)
        };
    }

    private async Task EnsureCoreReadyAsync()
    {
        if (_coreReady) return;
        await ConfigHandler.InitBuiltinDNS(_config);
        await ConfigHandler.InitBuiltinFullConfigTemplate(_config);
        await ProfileExManager.Instance.Init();
        await CoreManager.Instance.Init(_config, UpdateCoreMessageAsync);
        // v2rayN's built-in resolvers are Chinese; use global ones for the bootstrap of the encrypted DNS.
        _config.SimpleDNSItem ??= new SimpleDNSItem();
        _config.SimpleDNSItem.DirectDNS = "1.1.1.1,8.8.8.8";
        _config.SimpleDNSItem.BootstrapDNS = "1.1.1.1,8.8.8.8";
        // No hosts overrides: they only add DNS rules that sing-box 1.14 rejects without extra evaluate steps.
        _config.SimpleDNSItem.AddCommonHosts = false;
        _config.SimpleDNSItem.UseSystemHosts = false;
        _config.SimpleDNSItem.Hosts = "";
        // Info level makes the cores print every DNS lookup and connection (the user's browsing);
        // warnings and errors still show why the tunnel failed. No access log either.
        _config.CoreBasicItem.Loglevel = "warning";
        _config.CoreBasicItem.LogEnabled = false;
        // The local proxy has no password: it stays on 127.0.0.1, whatever an old or edited
        // settings file says (v2rayN's "allow LAN" would open it to everyone on the network).
        foreach (var inbound in _config.Inbound ?? [])
        {
            inbound.AllowLANConn = false;
        }
        // Hysteria2 is not an Xray protocol; it runs on the bundled sing-box core.
        _config.CoreTypeItem = CoreTypes(ColituSplitTunnel.NeedsSingBox(Preferences, EffectiveTunMode));
        // No declared bandwidth: Hysteria2 then uses BBR, as on Android. v2rayN's 100/100 Mbps
        // default selects Brutal, which sends at 100 Mbps whatever the line can carry; on slower
        // links that means heavy loss and stalls.
        _config.HysteriaItem ??= new HysteriaItem();
        _config.HysteriaItem.UpMbps = 0;
        _config.HysteriaItem.DownMbps = 0;
        // Port hopping (mport on the panel's hysteria2:// links): a new UDP port every 30 s, so a
        // mobile network that throttles one long-lived UDP flow never sees one. Both cores ignore
        // values under 5 s; a stale value from an old settings file must not slow or break the hops.
        _config.HysteriaItem.HopInterval = Global.Hysteria2DefaultHopInt;
        _coreReady = true;
    }

    /// <summary>
    /// Imports every transport offered by the panel for the selected location
    /// (primary first) and returns the matching v2rayN profiles in that order.
    /// </summary>
    private static readonly string[] ImportSchemes = ["vless://", "trojan://", "hysteria2://", "ss://"];

    private async Task<List<(ColituConfigCandidate Candidate, ProfileItem Profile)>> ImportConfigAsync(ColituVpnConfigResponse config, ColituVpnServer? server)
    {
        var candidates = config.Candidates;
        LogConnection($"Importing config for serverId={config.ServerId ?? server?.Id}, revision={config.Revision}, candidates={string.Join(",", candidates.Select(item => item.Protocol))}");
        if (candidates.Count == 0 || string.IsNullOrWhiteSpace(config.RawConfig))
        {
            throw new InvalidOperationException("Server config is not ready.");
        }

        // RawConfig is built locally from share links. Anything else must never reach v2rayN's
        // importer, whose fallbacks register unrecognised text as a full core config.
        if (config.RawConfig.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0)
            .Any(line => !ImportSchemes.Any(scheme => line.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))))
        {
            throw new InvalidOperationException("Server config could not be imported.");
        }
        var imported = await ConfigHandler.AddBatchServers(_config, config.RawConfig, ColituSubId, true);
        if (imported <= 0) throw new InvalidOperationException("Server config could not be imported.");

        var profiles = await AppManager.Instance.ProfileItems(ColituSubId) ?? [];
        var used = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<(ColituConfigCandidate Candidate, ProfileItem Profile)>();
        foreach (var candidate in candidates)
        {
            var type = candidate.Protocol switch
            {
                "hysteria2" => EConfigType.Hysteria2,
                "vless-reality" or "vless-xhttp" => EConfigType.VLESS,
                "trojan" => EConfigType.Trojan,
                "shadowsocks" => EConfigType.Shadowsocks,
                _ => (EConfigType?)null
            };
            // Both VLESS transports import as VLESS profiles; the network tells them apart.
            var xhttp = candidate.Protocol == "vless-xhttp";
            var profile = profiles.FirstOrDefault(item => type != null && item.ConfigType == type && !used.Contains(item.IndexId)
                && (type != EConfigType.VLESS || string.Equals(item.Network, "xhttp", StringComparison.OrdinalIgnoreCase) == xhttp));
            if (profile == null) continue;
            used.Add(profile.IndexId);
            ordered.Add((candidate, profile));
        }

        if (ordered.Count == 0)
        {
            throw new InvalidOperationException("Imported server was not found in v2rayN profiles.");
        }

        if (ColituSplitTunnel.NeedsSingBox(Preferences, EffectiveTunMode) && ordered.Any(item => item.Candidate.Protocol != "vless-xhttp"))
        {
            // sing-box (which runs every transport while apps are split) has no XHTTP transport.
            var removed = ordered.RemoveAll(item => item.Candidate.Protocol == "vless-xhttp");
            if (removed > 0)
            {
                LogConnection("Split tunnelling by app: vless-xhttp skipped (sing-box has no XHTTP transport)");
            }
        }

        var serverId = config.ServerId ?? server?.Id;
        var lastGood = _adaptive.LastGoodTransport(_networkKey, serverId, DateTimeOffset.UtcNow);
        var offered = ordered.Select(item => item.Candidate.Protocol).ToList();
        _marksIgnored = ColituTransportOrder.MarksCoverAlmostAll(offered, protocol => StalledRecently(serverId, protocol));
        // Adaptive Connect 3.0: no memory of this network (no last good transport for this server),
        // so what worked for most devices here goes first, and the latency probe is skipped. Own
        // experience (a last good transport) and a round with ignored marks keep the usual order.
        if (ColituAdaptiveConnect3.HintedStartActive(_marksIgnored)
            && ColituNetworkHintsPolicy.HintedStart(ordered, item => item.Candidate.Protocol, HintedPreferredList(),
                protocol => StalledRecently(serverId, protocol) || HintedBlocked(protocol), lastGood,
                rest => rest.OrderBy(item => TransportRank(item.Candidate.Protocol, 0, StalledRecently(serverId, item.Candidate.Protocol),
                    hintedBlocked: HintedBlocked(item.Candidate.Protocol)))) is { } hinted)
        {
            LogConnection($"no memory of this network, starting with the hinted [{string.Join(", ", hinted.Select(item => item.Candidate.Protocol))}]");
            LogConnection($"Transport order: {string.Join(" > ", hinted.Select(item => item.Candidate.Protocol))} (hinted, no probe)");
            return hinted;
        }
        var latency = await MeasureTransportLatencyAsync(ordered);
        if (_marksIgnored)
        {
            var proven = _adaptive.ProvenOnNetwork(_networkKey, DateTimeOffset.UtcNow);
            LogConnection($"stall marks cover (almost) every transport ({string.Join(", ", offered.Where(protocol => StalledRecently(serverId, protocol)))}): a network problem, ignored for this round");
            ordered = ordered
                .OrderBy(item => ColituTransportOrder.IgnoredMarksTier(item.Candidate.Protocol,
                    protocol => string.Equals(lastGood, protocol, StringComparison.OrdinalIgnoreCase) || proven.Contains(protocol, StringComparer.OrdinalIgnoreCase),
                    protocol => StalledRecently(serverId, protocol), _failedThisConnect.Contains))
                .ThenBy(item => TransportRank(item.Candidate.Protocol, latency.GetValueOrDefault(item.Candidate.Protocol, -1), false, false, HintedBlocked(item.Candidate.Protocol)))
                .ThenBy(item => latency.GetValueOrDefault(item.Candidate.Protocol, int.MaxValue))
                .ToList();
        }
        else
        {
            ordered = ordered
                .OrderBy(item => TransportRank(item.Candidate.Protocol, latency.GetValueOrDefault(item.Candidate.Protocol, -1),
                    StalledRecently(serverId, item.Candidate.Protocol),
                    string.Equals(lastGood, item.Candidate.Protocol, StringComparison.OrdinalIgnoreCase),
                    HintedBlocked(item.Candidate.Protocol)))
                .ThenBy(item => latency.GetValueOrDefault(item.Candidate.Protocol, int.MaxValue))
                .ToList();
        }
        LogConnection($"Transport order: {string.Join(" > ", ordered.Select(item => $"{item.Candidate.Protocol}({LatencyText(latency, item.Candidate.Protocol)})"))}");
        return ordered;
    }

    private static string LatencyText(Dictionary<string, int> latency, string protocol)
    {
        if (!latency.TryGetValue(protocol, out var ms)) return "udp";
        return ms < 0 ? "unreachable" : $"{ms} ms";
    }

    /// <summary>
    /// TCP connect time to each TCP transport's endpoint, two attempts in parallel, best of two.
    /// Hysteria2 is UDP and has no cheap handshake, so it is not measured. -1 = no answer.
    /// </summary>
    private async Task<Dictionary<string, int>> MeasureTransportLatencyAsync(List<(ColituConfigCandidate Candidate, ProfileItem Profile)> profiles)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var tasks = profiles
            .Where(item => item.Profile.ConfigType != EConfigType.Hysteria2)
            .Select(async item =>
            {
                var attempts = await Task.WhenAll(TcpConnectMsAsync(item.Profile.Address, item.Profile.Port), TcpConnectMsAsync(item.Profile.Address, item.Profile.Port));
                var reachable = attempts.Where(ms => ms >= 0).ToList();
                return (item.Candidate.Protocol, Ms: reachable.Count > 0 ? reachable.Min() : -1);
            })
            .ToList();
        foreach (var (protocol, ms) in await Task.WhenAll(tasks))
        {
            result[protocol] = ms;
        }
        return result;
    }

    private static async Task<int> TcpConnectMsAsync(string address, int port)
    {
        // Pinned to the physical adapter: during a reconnect the old tunnel's routes can still be
        // in place, and a connect through them timed the dying tunnel (1.3-2.2 s or "unreachable"
        // for every transport) instead of the server, which picked the wrong transport first.
        if (IPAddress.TryParse(address, out var ip) && ColituLatency.IsPublic(ip))
        {
            return await ColituLatency.ConnectMsAsync(ip, port, ColituNetwork.PhysicalInterfaceIndex()) ?? -1;
        }
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(2500));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await client.ConnectAsync(address, port, cts.Token);
            return (int)watch.ElapsedMilliseconds;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// Fixed order of the transports by how well they survive lossy links and censorship.
    /// Hysteria2 (QUIC) keeps working where TCP transports stall; Reality with the vision flow is
    /// the most robust TCP transport; Trojan and plain Shadowsocks are recognised by DPI the
    /// fastest, so they only follow. Measured latency only breaks ties.
    /// </summary>
    internal static int TransportPriority(string protocol) => protocol switch
    {
        "hysteria2" => 0,
        "vless-reality" => 1,
        "vless-xhttp" => 2,
        "trojan" => 3,
        "shadowsocks" => 4,
        _ => 5
    };

    /// <summary>
    /// <see cref="TransportPriority"/>, with unreachable endpoints and transports that stalled
    /// recently moved to the back of the line for a while. The transport that last carried traffic
    /// on this server and network (<paramref name="lastGood"/>) goes first while it is reachable.
    /// </summary>
    /// <param name="hintedBlocked">The network hints call it blocked on this network (and it has not
    /// worked here in the last 24 h): it goes behind every other transport.</param>
    internal static int TransportRank(string protocol, int latencyMs, bool stalledRecently, bool lastGood = false, bool hintedBlocked = false)
    {
        if (lastGood && !stalledRecently && (latencyMs >= 0 || protocol == "hysteria2"))
        {
            return -1;
        }
        var rank = TransportPriority(protocol);
        if (latencyMs < 0 && protocol != "hysteria2")
        {
            rank += 10;
        }
        if (stalledRecently)
        {
            rank += 20;
        }
        if (hintedBlocked)
        {
            rank += 40;
        }
        return rank;
    }

    /// <summary>
    /// Stalled on this server on the current network, or on the network as a whole (it stalled on
    /// two servers there). Another network starts clean.
    /// </summary>
    private bool StalledRecently(string? serverId, string protocol) =>
        _adaptive.IsStalled(_networkKey, serverId, protocol, DateTimeOffset.UtcNow);

    /// <summary>A Hysteria2 profile whose share link carried a port range to hop over (mport=20000-40000).</summary>
    internal static bool UsesPortHopping(ProfileItem profile) =>
        profile.ConfigType == EConfigType.Hysteria2
        && profile.GetProtocolExtra().Ports is { Length: > 0 } ports
        && (ports.Contains('-') || ports.Contains(':') || ports.Contains(','));

    /// <summary>
    /// Starts the core with each candidate transport until traffic flows through
    /// one of them, then reports the observations back to the panel. With a
    /// <paramref name="serverBudget"/> (automatic mode) no further transport is started once
    /// another one would not fit in it: the next server gets the time instead.
    /// </summary>
    private async Task StartFirstWorkingProfileAsync(List<(ColituConfigCandidate Candidate, ProfileItem Profile)> profiles, ColituVpnConfigResponse config, ColituVpnServer? server, CancellationToken token, TimeSpan? serverBudget = null)
    {
        var observations = new List<ColituProtocolObservation>();
        var watch = Stopwatch.StartNew();
        // Spares that failed together with their primary in this connect ("server|transport"): not reused.
        var excludedSpares = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            for (var index = 0; index < profiles.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var (candidate, profile) = profiles[index];
                if (index > 0 && serverBudget is { } budget && watch.Elapsed + TransportBudget > budget)
                {
                    LogConnection($"Server time budget spent ({watch.ElapsedMilliseconds} ms of {budget.TotalSeconds:0} s); {profiles.Count - index} transport(s) not tried");
                    throw new ColituConnectException(Loc.I["err.unreachable"]);
                }
                var isLast = index == profiles.Count - 1;
                var hosts = new HashSet<string>(_serverHosts, StringComparer.OrdinalIgnoreCase);
                foreach (var host in new[] { profile.Address, profile.Sni })
                {
                    if (host.IsNotEmpty()) hosts.Add(host);
                }
                _serverHosts = hosts;
                if (await ConfigHandler.SetDefaultServerIndex(_config, profile.IndexId) != 0)
                {
                    throw new InvalidOperationException("Imported server could not be selected as default.");
                }
                LogConnection($"Trying transport {candidate.Protocol}: index={profile.IndexId}, address={profile.Address}:{profile.Port}");

                try
                {
                    _lastCoreMessage = null;
                    _sparePlan = PlanSpare(candidate.Protocol, profile, profiles, config, server, excludedSpares);
                    await ReloadCoreAsync(token);
                    // Hysteria2's first QUIC handshake right after the TUN adapter appears sometimes
                    // needs longer than one round; it gets a second one unless the server refused us.
                    // Speed budget: start plus check stay within about 8 s per pair (2 x 4 s, or 6 s).
                    var rounds = isLast || candidate.Protocol == "hysteria2" ? 2 : 1;
                    var roundTimeout = TimeSpan.FromSeconds(rounds == 2 ? 4 : 6);
                    var pairWatch = Stopwatch.StartNew();
                    var plan = _sparePlan;
                    var spareVerify = plan != null ? _spareVerifyInbound : null;
                    // Parallel connect: the primary alone (colitu-verify) and the spare alone
                    // (colitu-verify-spare) at the same time, in one core start.
                    var primaryTask = VerifyConnectionActiveAsync(server, rounds, token, roundTimeout, primaryOnly: true);
                    var spareTask = spareVerify == null ? null
                        : ProbeThroughLocalProxyAsync(spareVerify.Port, rounds, token, roundTimeout: roundTimeout, credentials: new NetworkCredential(spareVerify.User, spareVerify.Password));
                    var probe = await primaryTask;
                    var spareProbe = spareTask == null ? null : await spareTask;
                    var graceOk = false;
                    if (!probe.Success && spareProbe is { Success: true } && _verifyInbound is { } verify)
                    {
                        // Only the spare answered: the primary still gets 1.5 s.
                        graceOk = (await ProbeThroughLocalProxyAsync(verify.Port, 1, token, roundTimeout: ColituParallelConnect.PrimaryGrace,
                            credentials: new NetworkCredential(verify.User, verify.Password))).Success;
                    }
                    var outcome = ColituParallelConnect.Decide(probe.Success, spareProbe?.Success, graceOk);
                    if (plan != null && spareProbe != null)
                    {
                        var winner = outcome switch
                        {
                            ColituParallelOutcome.PrimaryWins => candidate.Protocol,
                            ColituParallelOutcome.SwapRoles => Describe(plan),
                            _ => "none"
                        };
                        LogConnection($"parallel round: {candidate.Protocol} vs {Describe(plan)} → winner {winner} in {pairWatch.ElapsedMilliseconds} ms");
                    }
                    observations.Add(new ColituProtocolObservation { Protocol = candidate.Protocol, Reachable = probe.Success || graceOk, LatencyMs = probe.LatencyMs });
                    if (outcome == ColituParallelOutcome.PrimaryWins)
                    {
                        _activeTransport = candidate.Protocol;
                        return;
                    }
                    // Without internet every transport fails: say so instead of blaming (and marking)
                    // each one in turn. Checked directly, outside the tunnel.
                    if (!_credentialsRefused && spareProbe is not { Success: true } && !await InternetReachableDirectAsync())
                    {
                        observations.Clear();
                        LogConnection("No internet outside the tunnel either; not trying the other transports");
                        throw new ColituConnectException(Loc.I["warn.noInternet"], offline: true);
                    }
                    // A transport that carried nothing goes last on the next attempts too (UDP blocked on
                    // this network): 10 minutes, 6 h once another transport carries traffic here. Refused
                    // credentials are a server still applying new ones, not a property of the transport.
                    if (!_credentialsRefused)
                    {
                        _adaptive.MarkStalled(_networkKey, config.ServerId ?? server?.Id, candidate.Protocol, DateTimeOffset.UtcNow, provisional: true);
                        _failedThisConnect.Add(candidate.Protocol);
                    }
                    if (outcome == ColituParallelOutcome.SwapRoles && plan != null)
                    {
                        if (plan.NextServerId != null && _spareServer is { } spareServer)
                        {
                            // The spare's server and transport take the lead (one quick reload, new spare).
                            throw new ColituSpareServerWins(spareServer, plan.Protocol);
                        }
                        // Same server: the spare's transport is the next primary; a new spare is picked
                        // (neither the failed transport, now marked, nor the new primary).
                        var next = profiles.FindIndex(index + 1, item => item.Candidate.Protocol == plan.Protocol);
                        if (next > index + 1)
                        {
                            var promoted = profiles[next];
                            profiles.RemoveAt(next);
                            profiles.Insert(index + 1, promoted);
                        }
                        isLast = index == profiles.Count - 1;
                    }
                    else if (outcome == ColituParallelOutcome.BothFailed && plan != null)
                    {
                        excludedSpares.Add(SpareKey(plan.ServerId, plan.Protocol));
                    }
                    if (isLast)
                    {
                        // No transport carried traffic: report it instead of claiming a connection.
                        throw new ColituConnectException(Loc.I["err.unreachable"]);
                    }
                }
                catch (Exception ex) when (!isLast && ex is not (OperationCanceledException or ColituConnectException or ColituSpareServerWins))
                {
                    observations.Add(new ColituProtocolObservation { Protocol = candidate.Protocol, Reachable = false });
                    LogConnection($"Transport {candidate.Protocol} failed: {ex.Message}");
                }

                await StopCoreAsync();
            }
        }
        finally
        {
            // Observations are per node; a route's id is not one.
            if (config.Server?.IsMultihop != true && server?.IsMultihop != true)
            {
                _ = _api.ReportProtocolObservationsAsync(config.ServerId ?? server?.Id, observations, CurrentNetworkToken());
            }
        }
    }

    /// <summary>
    /// TUN mode, either chosen or forced because the system proxy can't reach the user's apps
    /// (see <see cref="ProxyModeUnusable"/>).
    /// </summary>
    private bool EffectiveTunMode => Preferences.IsTunMode || ColituHardening.ElevatedAsAnotherUser;

    /// <summary>
    /// A standard account approves the UAC prompt with an administrator's password, so the app
    /// runs as that administrator: the system proxy would be set for the administrator, and the
    /// signed-in user's browsers would go out unprotected while the app says "connected". Only
    /// TUN mode (which covers the whole computer) protects them then.
    /// </summary>
    private bool ProxyModeUnusable()
    {
        if (!ColituHardening.ElevatedAsAnotherUser)
        {
            return false;
        }
        if (!_proxyModeWarned && !Preferences.IsTunMode)
        {
            _proxyModeWarned = true;
            LogConnection("Running as another Windows account than the signed-in user: using TUN mode instead of the system proxy");
            Notice?.Invoke("warn.proxyOtherUser");
        }
        return true;
    }

    private async Task PrepareConnectionModeAsync(string? serverCountry)
    {
        EnsureAdministratorForVpn();
        var preferences = _session.Preferences.Normalize();
        await ApplyRuntimePreferencesAsync(preferences);
        await EnsureColituRoutingAsync(preferences, serverCountry);

        if (!_config.TunModeItem.EnableTun && _config.SystemProxyItem.SysProxyType != ESysProxyType.ForcedChange)
        {
            _restoreSysProxyType ??= _config.SystemProxyItem.SysProxyType;
            _config.SystemProxyItem.SysProxyType = ESysProxyType.ForcedChange;
            if (_config.SystemProxyItem.SystemProxyAdvancedProtocol.IsNullOrEmpty())
            {
                _config.SystemProxyItem.SystemProxyAdvancedProtocol = string.Empty;
            }
            await ConfigHandler.SaveConfig(_config);
            LogConnection($"System proxy mode forced for Colitu connection. Previous={_restoreSysProxyType}");
        }
    }

    private async Task ApplyRuntimePreferencesAsync(ColituVpnPreferences preferences)
    {
        preferences = preferences.Normalize();
        var tun = preferences.IsTunMode || ProxyModeUnusable();
        _config.TunModeItem.EnableTun = tun;
        _config.TunModeItem.AutoRoute = tun;
        // With the kill switch on, its WFP filters stop DNS and other traffic leaving outside the
        // tunnel, and sing-box's strict route would add a second, conflicting set of rules. Without
        // it, strict route is what keeps Windows from also asking the network adapter's DNS server.
        _config.TunModeItem.StrictRoute = tun && !preferences.KillSwitchEnabled;
        // sing-box runs the TUN adapter for every transport and hands Xray's connections to Xray
        // (as on Linux). Xray's own TUN sets no DNS server on its adapter, so Windows kept asking
        // the network adapter's resolver: the internet provider saw every name without the kill
        // switch, and with it (port 53 blocked outside the tunnel) names did not resolve at all.
        _config.TunModeItem.EnableLegacyProtect = true;

        if (_config.Inbound.Count > 0)
        {
            // The domain/geosite rules need the sniffed name; in TUN mode it is used for routing
            // only, so the connection keeps the address the app resolved and the IP rules
            // (Russian addresses direct, ad-block 0.0.0.0) still see it.
            var inbound = _config.Inbound.First();
            inbound.SniffingEnabled = tun || preferences.DnsLeakProtectionEnabled || inbound.SniffingEnabled;
            inbound.RouteOnly = tun;
        }

        // Ad blocking: lookups go to Colitu's AdGuard Home servers (DoH through the tunnel), which
        // answer 0.0.0.0 for ad and tracker domains. In TUN mode that already stops the apps; in
        // proxy mode the browser hands the domain to the core, so the core resolves it first
        // (IPIfNonMatch) and the 0.0.0.0 rule in BuildColituRoutingRules drops the connection.
        // IPIfNonMatch is also what sends a domain the Russian rules do not name out directly
        // when its address is in Russia (as on iOS and Android).
        _config.SimpleDNSItem ??= new SimpleDNSItem();
        _config.SimpleDNSItem.RemoteDNS = (preferences.AdBlockEnabled && AdBlockAvailable)
            ? string.Join(",", ColituAdBlockDohServers)
            : Global.DomainRemoteDNSAddress.First();
        // A lookup that times out answers from the expired cache instead of failing the page.
        _config.SimpleDNSItem.ServeStale = true;
        _config.RoutingBasicItem.DomainStrategy = RoutingDomainStrategy(tun);
        _config.CoreTypeItem = CoreTypes(ColituSplitTunnel.NeedsSingBox(preferences, tun));

        await ConfigHandler.SaveConfig(_config);
    }

    /// <summary>
    /// Which core runs which transport. Hysteria2 always runs on sing-box (Xray does not speak it).
    /// Split tunnelling by app in TUN mode needs sing-box for every transport: only sing-box can tell
    /// which program sent a packet into the TUN adapter on Windows.
    /// </summary>
    internal static List<CoreTypeItem> CoreTypes(bool singBoxForAll)
    {
        var types = new List<CoreTypeItem> { new() { ConfigType = EConfigType.Hysteria2, CoreType = ECoreType.sing_box } };
        if (singBoxForAll)
        {
            foreach (var type in new[] { EConfigType.VLESS, EConfigType.Trojan, EConfigType.Shadowsocks })
            {
                types.Add(new CoreTypeItem { ConfigType = type, CoreType = ECoreType.sing_box });
            }
        }
        return types;
    }

    /// <summary>
    /// TUN mode: apps resolve names themselves (through the tunnel's DNS) and the core receives
    /// addresses, so routing needs no lookup of its own. With IPIfNonMatch the core first asked
    /// its DoH resolver about every new domain, and one hanging DoH connection stalled every
    /// connection until the watchdog tore the tunnel down. Proxy mode keeps IPIfNonMatch: there
    /// the browser hands over bare domains, and ad blocking and Russian addresses need the lookup.
    /// </summary>
    internal static string RoutingDomainStrategy(bool tun) => tun ? Global.AsIs : Global.IPIfNonMatch;

    /// <summary>
    /// Colitu's ad-blocking DNS servers, tried in order. They are Colitu's own nodes, so they are
    /// not in the public source: scripts/build-installer.ps1 embeds them as assembly metadata.
    /// Builds without them have no ad blocking (the switch is hidden).
    /// </summary>
    internal static readonly string[] ColituAdBlockDohServers = ParseAdBlockDohServers(
        typeof(ColituVpnService).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "ColituAdBlockDoh")?.Value);

    internal static bool AdBlockAvailable => ColituAdBlockDohServers.Length > 0;

    internal static string[] ParseAdBlockDohServers(string? value) =>
        (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(url => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(url, UriKind.Absolute, out _))
            .ToArray();

    /// <summary>Whether a node (by its probe host) runs one of <see cref="ColituAdBlockDohServers"/>; the server list tags it.</summary>
    internal static bool HostsAdBlockDns(string? host) =>
        !string.IsNullOrWhiteSpace(host)
        && ColituAdBlockDohServers.Any(url => string.Equals(new Uri(url).Host, host.Trim(), StringComparison.OrdinalIgnoreCase));

    private async Task EnsureColituRoutingAsync(ColituVpnPreferences preferences, string? serverCountry)
    {
        preferences = preferences.Normalize();
        await ConfigHandler.InitBuiltinRouting(_config);
        var items = await AppManager.Instance.RoutingItems() ?? [];
        var activeRouting = items.FirstOrDefault(item => item.IsActive);
        var routing = items.FirstOrDefault(item => string.Equals(item.Remarks, ColituRoutingRemarks, StringComparison.OrdinalIgnoreCase));
        var rules = BuildColituRoutingRules(preferences, serverCountry, EffectiveTunMode);

        routing ??= new RoutingItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Remarks = ColituRoutingRemarks,
            Url = string.Empty,
            Sort = items.Count + 1,
            Enabled = true
        };

        routing.RuleSet = JsonSerializer.Serialize(rules, _jsonOptions);
        routing.RuleNum = rules.Count;
        routing.DomainStrategy = _config.RoutingBasicItem.DomainStrategy;
        routing.DomainStrategy4Singbox = _config.RoutingBasicItem.DomainStrategy4Singbox;
        await SQLiteHelper.Instance.ReplaceAsync(routing);

        if (activeRouting != null
            && activeRouting.Id != routing.Id
            && _session.Preferences.PreviousRoutingId.IsNullOrEmpty())
        {
            _session = _session with { Preferences = _session.Preferences with { PreviousRoutingId = activeRouting.Id } };
            SaveState();
        }

        await ConfigHandler.SetDefaultRouting(_config, routing);
        _routingServerCountry = serverCountry;
        LogConnection(RussianSitesDirect(serverCountry, preferences.PrivacyModeEnabled)
            ? "Routing profile applied: DNS protection, Russian sites direct"
            : preferences.PrivacyModeEnabled
                ? "Routing profile applied: DNS protection, privacy mode (all traffic through the tunnel)"
                : "Routing profile applied: DNS protection, Russian sites through the Russian server");
    }

    /// <summary>
    /// Russian sites skip the tunnel unless the server itself is in Russia (someone abroad who
    /// picks the Moscow server wants exactly those sites to see a Russian address) or the user
    /// turned on privacy mode (everything through the tunnel, no exceptions).
    /// </summary>
    internal static bool RussianSitesDirect(string? serverCountry, bool privacyMode = false) =>
        !privacyMode && !string.Equals(serverCountry?.Trim(), "RU", StringComparison.OrdinalIgnoreCase);

    /// <param name="tun">TUN mode (app rules only apply there); the preference when not given.</param>
    internal static List<RulesItem> BuildColituRoutingRules(ColituVpnPreferences preferences, string? serverCountry = null, bool? tun = null)
    {
        preferences = preferences.Normalize();
        var ruDirect = RussianSitesDirect(serverCountry, preferences.PrivacyModeEnabled);
        var (splitRules, splitRest) = ColituSplitTunnel.BuildRules(preferences, tun ?? preferences.IsTunMode);
        var rules = new List<RulesItem>();

        rules.Add(new RulesItem
        {
            Id = "colitu-dns-protection",
            Remarks = "DNS leak protection",
            OutboundTag = Global.ProxyTag,
            Port = "53",
            Network = "tcp,udp",
            Enabled = preferences.DnsLeakProtectionEnabled
        });

        rules.Add(new RulesItem
        {
            Id = "colitu-ad-block",
            Remarks = "Ad blocking: domains Colitu DNS answers with 0.0.0.0",
            OutboundTag = Global.BlockTag,
            Ip = ["0.0.0.0/32", "::/128"],
            Enabled = (preferences.AdBlockEnabled && AdBlockAvailable)
        });

        // The local network (printers, NAS, another PC, the router page) never goes to the server,
        // which cannot reach it: in TUN mode the adapter took it and the connection hung. After the
        // DNS rule, so a resolver on the LAN still gets no names. Off only when the kill switch is
        // told to block the local network.
        rules.Add(new RulesItem
        {
            Id = LanDirectRuleId,
            Remarks = "Local network direct",
            OutboundTag = Global.DirectTag,
            Ip = ["geoip:private"],
            Enabled = !(preferences.KillSwitchEnabled && !preferences.KillSwitchAllowLan)
        });

        // The user's split-tunnel lists come before the regional rule: an app or site the user
        // asked to keep in (or out of) the tunnel is decided by the user's choice.
        rules.AddRange(splitRules);

        // Russian sites and apps (banks, Gosuslugi, Wildberries, ...) refuse connections from a
        // foreign IP ("turn off your VPN"), so they go out directly, as on iOS and Android; through
        // a Russian server they already arrive from a Russian address and stay in the tunnel.
        // Xray reads these from bin\xray\geo*.dat (XRAY_LOCATION_ASSET, CoreInfoManager), sing-box
        // from bin\srss\*.srs.
        rules.Add(new RulesItem
        {
            Id = "colitu-ru-direct-domain",
            Remarks = "Russian sites direct",
            OutboundTag = Global.DirectTag,
            Domain = ["geosite:category-ru"],
            Enabled = ruDirect
        });

        rules.Add(new RulesItem
        {
            Id = "colitu-ru-direct-ip",
            Remarks = "Russian IPs direct",
            OutboundTag = Global.DirectTag,
            Ip = ["geoip:ru"],
            Enabled = ruDirect
        });

        if (splitRest != null)
        {
            // "Only selected apps and sites use the VPN": everything else goes direct. Last, so it
            // is also the final rule sing-box reads to resolve names directly.
            rules.Add(splitRest);
        }

        return rules;
    }

    private void EnsureAdministratorForVpn()
    {
        if (Utils.IsWindows() && !Utils.IsAdministrator())
        {
            throw new ColituConnectException(Loc.I["err.admin"]);
        }
    }

    private async Task RestoreProxyPreferenceAsync()
    {
        if (_restoreSysProxyType == null) return;
        _config.SystemProxyItem.SysProxyType = _restoreSysProxyType.Value;
        _restoreSysProxyType = null;
        await ConfigHandler.SaveConfig(_config);
    }

    private async Task RestoreRoutingPreferenceAsync()
    {
        var previousRoutingId = _session.Preferences.PreviousRoutingId;
        if (previousRoutingId.IsNullOrEmpty()) return;

        var previous = await AppManager.Instance.GetRoutingItem(previousRoutingId);
        if (previous != null)
        {
            await ConfigHandler.SetDefaultRouting(_config, previous);
        }

        _session = _session with { Preferences = _session.Preferences with { PreviousRoutingId = null } };
        SaveState();
    }

    private async Task ReloadCoreAsync(CancellationToken token = default)
    {
        var profileItem = await ConfigHandler.GetDefaultServer(_config);
        if (profileItem == null)
        {
            throw new InvalidOperationException("No v2rayN profile is selected.");
        }

        // Send through the physical adapter, never through another VPN's tunnel.
        _config.CoreBasicItem.BindInterface = ColituNetwork.PhysicalInterfaceName();
        LogConnection($"Building core config for profile={profileItem.IndexId}, type={profileItem.ConfigType}, outbound interface={_config.CoreBasicItem.BindInterface ?? "auto"}");
        var allResult = await CoreConfigContextBuilder.BuildAll(_config, profileItem);
        if (!allResult.Success)
        {
            var errors = allResult.CombinedValidatorResult.Errors;
            throw new InvalidOperationException(errors.Count > 0 ? string.Join(Environment.NewLine, errors) : "Core config validation failed.");
        }

        var mainContext = allResult.MainResult.Context;
        var coreInfo = CoreInfoManager.Instance.GetCoreInfo(mainContext.RunCoreType);
        var coreExe = CoreInfoManager.Instance.GetCoreExecFile(coreInfo, out var coreMsg);
        var configPath = Utils.GetBinConfigPath(Global.CoreConfigFileName);
        LogConnection($"Core config path={configPath}");
        LogConnection(coreExe.IsNotEmpty()
            ? $"Core exe path={coreExe}"
            : $"Core exe missing: {coreMsg}");

        if (coreExe.IsNullOrEmpty())
        {
            throw new InvalidOperationException(coreMsg.IsNotEmpty() ? coreMsg : "Xray/sing-box core executable was not found.");
        }

        await EnsureLocalPortsFreeAsync();
        // Set again by the warm spare when this config gets one.
        _verifyInbound = null;
        _spareVerifyInbound = null;
        _checkInbound = null;
        _checkIndexId = profileItem.IndexId;
        await CoreManager.Instance.LoadCore(mainContext, allResult.PreSocksResult?.Context);

        // The core needs a moment to load its geo data, longer on the first run
        // while antivirus scans the binary: wait for the local port, not a fixed delay.
        var socksPort = AppManager.Instance.GetLocalPort(EInboundProtocol.socks);
        var started = Stopwatch.StartNew();
        var listening = false;
        while (started.Elapsed < TimeSpan.FromSeconds(12))
        {
            await Task.Delay(250, token);
            if (!CoreManager.Instance.IsCoreRunning)
            {
                throw new InvalidOperationException(_lastCoreMessage.IsNotEmpty()
                    ? $"VPN core failed to start: {_lastCoreMessage}"
                    : $"VPN core failed to start. Check core executable and config path: {coreExe}, {configPath}");
            }
            if (await IsLocalPortOpenAsync(Global.Loopback, socksPort))
            {
                listening = true;
                break;
            }
        }
        LogConnection($"Core ready check: listening={listening} after {started.ElapsedMilliseconds} ms");
        if (!listening)
        {
            throw new InvalidOperationException($"VPN core started but local SOCKS port {socksPort} is not listening.");
        }

        if (_config.TunModeItem.EnableTun)
        {
            // TUN captures all traffic at the adapter level; the system proxy
            // must stay off or apps would double-route through the local proxy.
            await SysProxyHandler.UpdateSysProxy(_config, true);
            LogConnection($"TUN mode active, system proxy disabled. socks={Global.Loopback}:{socksPort}");
            return;
        }

        var proxyResult = await SysProxyHandler.UpdateSysProxy(_config, false);
        LogConnection($"System proxy result={proxyResult}, mode={_config.SystemProxyItem.SysProxyType}, socks={Global.Loopback}:{socksPort}, tun={_config.TunModeItem.EnableTun}");
        if (!proxyResult)
        {
            throw new InvalidOperationException("System proxy could not be applied.");
        }
    }

    /// <summary>
    /// A previous core process (crashed app, stuck switch) can keep the local
    /// inbound port bound, which makes the next start fail with
    /// "address already in use". Stop our core, kill orphans, and wait for the
    /// port to be released before starting a new core.
    /// </summary>
    private async Task EnsureLocalPortsFreeAsync()
    {
        var socksPort = AppManager.Instance.GetLocalPort(EInboundProtocol.socks);
        if (!await IsLocalPortOpenAsync(Global.Loopback, socksPort))
        {
            return;
        }

        LogConnection($"Local port {socksPort} is already in use; stopping previous VPN core.");
        await StopCoreAsync();

        for (var attempt = 0; attempt < 10; attempt++)
        {
            await Task.Delay(400);
            if (!await IsLocalPortOpenAsync(Global.Loopback, socksPort))
            {
                return;
            }
            if (attempt == 3)
            {
                KillOrphanCoreProcesses();
            }
        }

        throw new InvalidOperationException(
            $"Local port {socksPort} is still in use. Close other VPN/proxy apps (or another Colitu instance) and try again.");
    }

    private void KillOrphanCoreProcesses()
    {
        var binPath = Utils.GetBinPath("");
        foreach (var name in new[] { "xray", "sing-box", "v2ray", "mihomo" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                try
                {
                    var path = process.MainModule?.FileName ?? "";
                    if (path.IsNotEmpty() && path.StartsWith(binPath, StringComparison.OrdinalIgnoreCase))
                    {
                        LogConnection($"Killing orphan core process {name} (pid {process.Id})");
                        process.Kill(true);
                        process.WaitForExit(2000);
                    }
                }
                catch
                {
                    // Best effort; the port re-check reports if the conflict persists.
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
    }

    /// <param name="primaryOnly">
    /// The connect check: with a warm spare in the config, probe through the <c>colitu-verify</c>
    /// inbound, which reaches the primary outbound only. Otherwise (and for recovery checks) the
    /// <c>colitu-check</c> inbound, the tunnel as a whole; never the routing rules.
    /// </param>
    private async Task<ColituTrafficProbeResult> VerifyConnectionActiveAsync(ColituVpnServer? server, int rounds = 2, CancellationToken token = default, TimeSpan? roundTimeout = null, bool primaryOnly = false)
    {
        if (!CoreManager.Instance.IsCoreRunning)
        {
            throw new InvalidOperationException("VPN core stopped before the connection could be verified.");
        }

        var socksPort = AppManager.Instance.GetLocalPort(EInboundProtocol.socks);
        if (!await IsLocalPortOpenAsync(Global.Loopback, socksPort))
        {
            throw new InvalidOperationException($"VPN local proxy port {socksPort} is not listening.");
        }

        // The probe goes through the local SOCKS port, not the adapter, so both run at once.
        var tunWatch = Stopwatch.StartNew();
        var tunReady = Task.FromResult(true);
        if (_config.TunModeItem.EnableTun)
        {
            LogConnection($"TUN mode enabled. autoRoute={_config.TunModeItem.AutoRoute}, strictRoute={_config.TunModeItem.StrictRoute}, stack={_config.TunModeItem.Stack}");
            tunReady = WaitForTunInterfaceAsync();
        }

        var verify = primaryOnly ? _verifyInbound : null;
        if (verify != null)
        {
            LogConnection($"Checking the primary alone (warm spare in the config), loopback port {verify.Port}");
        }
        var (port, credentials) = verify != null ? (verify.Port, new NetworkCredential(verify.User, verify.Password)) : TunnelCheckTarget();
        // The server refusing our credentials will not change in a second round.
        var probe = await ProbeThroughLocalProxyAsync(port, rounds, token,
            () => _lastCoreMessage?.Contains("authentication failed", StringComparison.OrdinalIgnoreCase) == true, roundTimeout, credentials);
        if (!await tunReady)
        {
            throw new InvalidOperationException("VPN tunnel adapter or route was not activated.");
        }
        if (_config.TunModeItem.EnableTun)
        {
            LogConnection($"TUN adapter up within {tunWatch.ElapsedMilliseconds} ms");
        }
        LogConnection($"Traffic verification result={probe.Success}, detail={probe.Detail}, latency={probe.LatencyMs} ms, serverId={server?.Id}");
        if (!probe.Success)
        {
            LogConnection("Traffic probe failed after the VPN core became ready.");
        }
        return probe;
    }

    /// <summary>Generic connectivity checks, never the Colitu API; the first 2xx answer through the tunnel counts.</summary>
    internal static readonly string[] TrafficProbeUrls =
    [
        "https://cp.cloudflare.com/generate_204",
        "https://www.gstatic.com/generate_204",
        "http://www.msftconnecttest.com/connecttest.txt"
    ];

    /// <summary>A traffic check answer that proves the tunnel carries traffic: 2xx (generate_204 answers 204).</summary>
    internal static bool IsTrafficProbeSuccess(int statusCode) => statusCode is >= 200 and < 300;

    /// <summary>
    /// Fetches small "connectivity check" pages (<see cref="TrafficProbeUrls"/>) through the tunnel,
    /// all at once, and succeeds on the first 2xx answer. Two rounds of at most seven seconds (or <paramref name="roundTimeout"/>).
    /// </summary>
    private static async Task<ColituTrafficProbeResult> ProbeThroughLocalProxyAsync(int port, int rounds = 2, CancellationToken token = default, Func<bool>? giveUp = null, TimeSpan? roundTimeout = null,
        NetworkCredential? credentials = null)
    {
        var timeout = roundTimeout ?? TimeSpan.FromSeconds(7);
        var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy($"{Global.Socks5Protocol}{Global.Loopback}:{port}") { Credentials = credentials },
            UseProxy = true,
            ConnectTimeout = timeout < TimeSpan.FromSeconds(5) ? timeout : TimeSpan.FromSeconds(5)
        };

        using var client = new HttpClient(handler) { Timeout = timeout };
        var probeUrls = TrafficProbeUrls;
        var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();

        for (var round = 1; round <= rounds; round++)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(timeout);
            var pending = probeUrls.Select(url => ProbeOnceAsync(client, url, errors, budget.Token)).ToList();
            while (pending.Count > 0)
            {
                var finished = await Task.WhenAny(pending);
                pending.Remove(finished);
                if (await finished is { } success)
                {
                    budget.Cancel();
                    return success;
                }
            }

            token.ThrowIfCancellationRequested();
            if (round < rounds)
            {
                if (giveUp?.Invoke() == true)
                {
                    break;
                }
                await Task.Delay(800, token);
            }
        }

        var detail = errors.IsEmpty ? "No probe URLs were available." : string.Join(" | ", errors.TakeLast(6));
        return new(false, detail);
    }

    private static async Task<ColituTrafficProbeResult?> ProbeOnceAsync(HttpClient client, string url, System.Collections.Concurrent.ConcurrentQueue<string> errors, CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.TryParseAdd($"ColituVPN/{ColituAuthService.ClientVersion}");
            var watch = Stopwatch.StartNew();
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (IsTrafficProbeSuccess((int)response.StatusCode))
            {
                return new(true, $"{url} returned {(int)response.StatusCode}", (int)watch.ElapsedMilliseconds);
            }
            errors.Enqueue($"{url}: HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex) when (!token.IsCancellationRequested || ex is not OperationCanceledException)
        {
            errors.Enqueue($"{url}: {ex.GetType().Name} {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            errors.Enqueue($"{url}: timed out");
        }
        return null;
    }

    private static bool HasActiveTunInterface()
    {
        try
        {
            // Only our own adapter: any "Tunnel" type adapter (Teredo, 6to4, another VPN such as
            // a Clash/mihomo "Meta Tunnel") used to pass this check before ours was up.
            return NetworkInterface.GetAllNetworkInterfaces().Any(adapter =>
                adapter.OperationalStatus == OperationalStatus.Up &&
                (adapter.Name.Equals("singbox_tun", StringComparison.OrdinalIgnoreCase) ||
                 adapter.Name.Equals("xray_tun", StringComparison.OrdinalIgnoreCase)));
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> WaitForTunInterfaceAsync()
    {
        for (var i = 0; i < 40; i++)
        {
            if (HasActiveTunInterface()) return true;
            await Task.Delay(200);
        }

        return false;
    }

    private static async Task<bool> IsLocalPortOpenAsync(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
            await client.ConnectAsync(host, port, timeout.Token);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private async Task UpdateCoreMessageAsync(bool notify, string message)
    {
        var line = ColituLogPrivacy.SanitizeCoreLine(message, _serverHosts);
        if (line != null)
        {
            _lastCoreMessage = line;
            if (line.Contains("authentication failed", StringComparison.OrdinalIgnoreCase))
            {
                _credentialsRefused = true;
            }
            LogConnection($"core notify={notify}: {line}");
            LastError = notify && Status == ColituVpnStatus.Error ? line : LastError;
        }
        await Task.CompletedTask;
    }

    /// <summary>Stops the core and removes its generated config files, which contain the server credentials.</summary>
    private static async Task StopCoreAsync()
    {
        await CoreManager.Instance.CoreStop();
        foreach (var name in new[] { Global.CoreConfigFileName, Global.CorePreConfigFileName })
        {
            try
            {
                var path = Utils.GetBinConfigPath(name);
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // Best effort; the folder is readable by administrators only.
            }
        }
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
    }

    private static string FriendlyConnectionError(Exception ex)
    {
        var loc = Loc.I;
        if (ex is ColituConnectException connect) return connect.Message;
        if (ex is ColituApiException api) return IsNetworkFailure(api) ? loc["err.network"] : api.Message;
        if (IsNetworkFailure(ex)) return loc["err.network"];
        var message = ex.Message;
        if (message.Contains("core executable", StringComparison.OrdinalIgnoreCase)) return loc["err.core"];
        if (message.Contains("adapter or route", StringComparison.OrdinalIgnoreCase)) return loc["err.tun"];
        if (message.Contains("still in use", StringComparison.OrdinalIgnoreCase)) return loc["err.port"];
        if (message.Contains("Server config", StringComparison.OrdinalIgnoreCase) || message.Contains("transport supported", StringComparison.OrdinalIgnoreCase)) return loc["err.noServers"];
        return loc["err.generic"];
    }

    private static string? NormalizeCountryCode(string? countryCode, string? countryName, string? serverName)
    {
        var code = (countryCode ?? "").Trim().ToUpperInvariant();
        if (string.Equals(code, "UK", StringComparison.OrdinalIgnoreCase))
        {
            code = "GB";
        }
        if (code.Length == 2 && code.All(ch => ch is >= 'A' and <= 'Z')) return code;

        var lookup = FirstNonEmpty(countryName, serverName).Trim().ToLowerInvariant();
        if (lookup.Length == 0) return null;

        var known = new Dictionary<string, string>
        {
            ["argentina"] = "AR",
            ["australia"] = "AU",
            ["austria"] = "AT",
            ["belgium"] = "BE",
            ["brazil"] = "BR",
            ["bulgaria"] = "BG",
            ["canada"] = "CA",
            ["denmark"] = "DK",
            ["finland"] = "FI",
            ["france"] = "FR",
            ["germany"] = "DE",
            ["hong kong"] = "HK",
            ["india"] = "IN",
            ["ireland"] = "IE",
            ["italy"] = "IT",
            ["japan"] = "JP",
            ["netherlands"] = "NL",
            ["norway"] = "NO",
            ["poland"] = "PL",
            ["romania"] = "RO",
            ["singapore"] = "SG",
            ["spain"] = "ES",
            ["sweden"] = "SE",
            ["switzerland"] = "CH",
            ["turkey"] = "TR",
            ["turkiye"] = "TR",
            ["türkiye"] = "TR",
            ["united kingdom"] = "GB",
            ["uk"] = "GB",
            ["united states"] = "US",
            ["usa"] = "US"
        };

        return known.FirstOrDefault(item => lookup.Contains(item.Key, StringComparison.OrdinalIgnoreCase)).Value;
    }

    public static string? NormalizeCountryCodeForApi(string? countryCode, string? countryName, string? serverName)
    {
        return NormalizeCountryCode(countryCode, countryName, serverName);
    }

    private void LogConnection(string message)
    {
        Logging.SaveLog($"ColituVpnService | {message}");
    }

    private void SetStatus(ColituVpnStatus status)
    {
        if (status != Status && status is ColituVpnStatus.Connected or ColituVpnStatus.Disconnected or ColituVpnStatus.Error)
        {
            ResetDirectConnections();
        }
        Status = status;
        if (status == ColituVpnStatus.Connected)
        {
            _verifiedNetwork = ColituNetwork.PhysicalNetworkFingerprint();
        }
        _session = _session with { Status = status, ConnectedAt = ConnectedAt };
        SaveState();
        StatusChanged?.Invoke(status);
    }

    private const int CurrentPreferencesMigration = 2;

    /// <summary>Whether a saved state names its connection mode (older builds always wrote it, but be sure).</summary>
    internal static bool SavedConnectionMode(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.EnumerateObject().FirstOrDefault(item => item.NameEquals("Preferences") || item.Name.Equals("preferences", StringComparison.OrdinalIgnoreCase)) is { Value.ValueKind: JsonValueKind.Object } preferences
                && preferences.Value.EnumerateObject().Any(item => item.Name.Equals("ConnectionMode", StringComparison.OrdinalIgnoreCase)
                    && item.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.Value.GetString()));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Proxy mode, chosen before TUN became the default, and the one-time question not asked yet.</summary>
    public bool ShouldOfferTunMode => !Preferences.IsTunMode && !Preferences.TunModePromptShown && !ColituHardening.ElevatedAsAnotherUser;

    /// <summary>Proxy mode is in effect: DNS, UDP/WebRTC, IPv6 and apps ignoring the system proxy are not covered.</summary>
    public bool ProxyCoverageLimited => !EffectiveTunMode;

    private void LoadState()
    {
        try
        {
            if (!File.Exists(StatePath()))
            {
                // Fresh install: privacy mode (all traffic through the VPN) is the default. A saved state
                // without the property keeps the old behaviour (the record default stays false).
                _session = _session with { PreferencesMigration = CurrentPreferencesMigration, Preferences = ColituVpnPreferences.ForNewInstall() };
                return;
            }
            var json = File.ReadAllText(StatePath());
            _session = JsonSerializer.Deserialize<ColituVpnSession>(json, _jsonOptions) ?? new();
            // Before SaveState below, which writes the memory back (expired entries pruned).
            _adaptive.Load(_session.AdaptiveMemory, DateTimeOffset.UtcNow);
            // SaveState below writes this run's status (Disconnected); recovery needs the old one.
            _previousRunStatus = _session.Status;
            if (_session.PreferencesMigration < 1)
            {
                // 1.6.5 made Kill Switch and Auto-Connect on by default; apply once to states saved by older builds.
                _session = _session with
                {
                    Preferences = _session.Preferences with { KillSwitchEnabled = true, AutoConnectEnabled = true }
                };
            }
            if (_session.PreferencesMigration < 2 && !SavedConnectionMode(json))
            {
                // 2.6.0 made TUN the default for new installs. A state saved without a mode was proxy
                // mode: keep it (the user is asked once, never switched silently).
                _session = _session with { Preferences = _session.Preferences with { ConnectionMode = ColituConnectionModes.Proxy } };
            }
            _session = _session with
            {
                Preferences = _session.Preferences.Normalize(),
                PreferencesMigration = CurrentPreferencesMigration
            };
            SaveState();
        }
        catch
        {
            _session = new ColituVpnSession { PreferencesMigration = CurrentPreferencesMigration };
        }
    }

    private void SaveState()
    {
        _session = _session with
        {
            Status = Status,
            SelectedServerId = SelectedServer?.Id ?? _session.SelectedServerId,
            ConnectedAt = ConnectedAt,
            SelectionMode = _session.SelectionMode,
            Preferences = _session.Preferences.Normalize(),
            AdaptiveMemory = _adaptive.Snapshot(DateTimeOffset.UtcNow)
        };
        // The UI and the watchdog (thread pool) both save; one writer at a time, never a torn file.
        lock (_stateLock)
        {
            var temp = $"{StatePath()}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(_session, _jsonOptions));
                File.Move(temp, StatePath(), overwrite: true);
            }
            catch (Exception ex)
            {
                // A locked or unwritable file must not break connecting or status updates.
                Logging.SaveLog("ColituVpnService.SaveState", ex);
                try { File.Delete(temp); } catch { }
            }
        }
    }

    private static string StatePath() => Utils.GetConfigPath("colitu-vpn-state.json");

    private sealed record ColituTrafficProbeResult(bool Success, string Detail, int? LatencyMs = null);
}

/// <summary>What a direct check outside the tunnel found when the tunnel stopped carrying traffic.</summary>
public enum ColituPathState
{
    /// <summary>Internet and server answer: the transport stalled.</summary>
    Reachable,
    /// <summary>The internet works but the server does not answer.</summary>
    ServerUnreachable,
    /// <summary>Nothing answers: the computer's own connection is down.</summary>
    Offline
}

public enum ColituVpnStatus
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Error
}

/// <summary>The account has no active plan; the UI sends the user to the plan page.</summary>
public sealed class ColituPlanRequiredException() : Exception(Loc.I["err.noPlan"]);

/// <summary>The plan's device limit paused this computer (403 DEVICE_OVER_LIMIT).</summary>
public sealed class ColituDevicePausedException(ColituDeviceOverLimit info) : Exception(Loc.I["paused.short"])
{
    public ColituDeviceOverLimit Info { get; } = info;
}

/// <summary>A connection failure whose message is already localized for the user.</summary>
public sealed class ColituConnectException(string message, Exception? inner = null, bool offline = false) : Exception(message, inner)
{
    /// <summary>The device itself had no internet: no server or transport is to blame.</summary>
    public bool Offline { get; } = offline;
}

public sealed class ColituDashboardData
{
    public ColituAccount? Account { get; init; }
    public ColituServersResponse Servers { get; init; } = new();
    public ColituStatsResponse Stats { get; init; } = new();
    public ColituVpnServer? SelectedServer { get; init; }
    public ColituVpnStatus Status { get; init; }
}

public sealed class ColituServersResponse
{
    public bool ActiveSubscription { get; set; }
    public bool PremiumAllowed { get; set; }
    public bool Unlimited { get; set; }
    public string? Tier { get; set; }
    public ColituFreeQuota? FreeQuota { get; set; }
    public List<ColituVpnServer> Servers { get; set; } = [];
    /// <summary>Multihop (double VPN) routes; empty when the panel offers none.</summary>
    public List<ColituVpnServer> Multihop { get; set; } = [];
    public ColituVpnServer? SelectedServer { get; set; }
    /// <summary>ISO-2 country of this device's IP as the panel saw it; null when unknown (or the VPN was on).</summary>
    public string? ClientCountry { get; set; }
    /// <summary>The panel's opaque key of this device's ISP network; null when unknown.</summary>
    public string? ClientNetwork { get; set; }
    /// <summary>Opaque token of that network for protocol observations; null when unknown.</summary>
    public string? NetworkToken { get; set; }
    /// <summary>Protocols the network hints call blocked on that network (empty: none).</summary>
    public List<string> NetworkHintsBlocked { get; set; } = [];
    /// <summary>Protocols that worked for most devices on that network, best first (Adaptive Connect 3.0).</summary>
    public List<string> NetworkHintsPreferred { get; set; } = [];
    /// <summary>The panel refused the list because the account has no active plan.</summary>
    public bool PlanRequired { get; set; }
}

public sealed class ColituVpnServer
{
    public string? Id { get; set; }
    public string? LocationId { get; set; }
    public string? Name { get; set; }
    public string? DisplayName { get; set; }
    public string? City { get; set; }
    public string? Region { get; set; }
    public string? CountryCode { get; set; }
    public string? Country { get; set; }
    public bool Premium { get; set; }
    public bool Free { get; set; }
    public bool? AllowFree { get; set; }
    public bool? AllowPremium { get; set; }
    public bool Available { get; set; } = true;
    public bool Locked { get; set; }
    public string? RequiredPlan { get; set; }
    public bool Hidden { get; set; }
    public bool RetainedHiddenSelection { get; set; }
    public bool Online { get; set; }
    public int? Ping { get; set; }
    public bool IsPingLoading { get; set; }
    public bool PingFailed { get; set; }
    public bool PingEstimated { get; set; }
    public double? Load { get; set; }
    public string? Host { get; set; }
    public int? Port { get; set; }
    public string? HealthCheckedAt { get; set; }
    public string? TestUrl { get; set; }
    /// <summary>Use-case categories from the panel (streaming, gaming, privacy, speed, torrent, ai).</summary>
    public List<string> Categories { get; set; } = [];
    /// <summary>Service tags from the panel (chatgpt, netflix, youtube_adfree...); only <c>youtube_adfree</c> is shown here, unknown keys are ignored.</summary>
    public List<string> Services { get; set; } = [];
    public bool HasAdFreeYoutube => Services.Contains("youtube_adfree");
    /// <summary>A multihop route (double VPN): <see cref="Entry"/> node, then <see cref="Exit"/> node. The list item's id is the route id.</summary>
    public bool IsMultihop { get; set; }
    public string? RouteSlug { get; set; }
    public ColituRouteEndpoint? Entry { get; set; }
    public ColituRouteEndpoint? Exit { get; set; }
    public string Quality => PingQuality(Ping);
    public string PingDisplay => IsPingLoading
        ? "Checking..."
        : Ping is > 0
            ? $"{Ping.Value} ms"
            : "— ms";
    public int PingSortValue => Ping is > 0 ? Ping.Value : 9999;
    public string PingColor => Ping is > 0
        ? Ping <= 80 ? "#00E5A0" : Ping <= 200 ? "#FFB547" : "#FF6B81"
        : "#5A6896";
    /// <summary>Locations list section header; assigned when the list is built.</summary>
    public string? GroupLabel { get; set; }
    public string PlanLabel => Locked ? "LOCKED" : Load switch
    {
        <= 40 => "LOW LOAD",
        <= 70 => "MEDIUM LOAD",
        > 70 => "HIGH LOAD",
        _ => "ONLINE"
    };
    public string Location => string.Join(", ", new[] { City, Country }.Where(item => !string.IsNullOrWhiteSpace(item)));
    public string FlagCode
    {
        get
        {
            var code = (CountryCode ?? "").Trim().ToUpperInvariant();
            if (string.Equals(code, "UK", StringComparison.OrdinalIgnoreCase))
            {
                code = "GB";
            }
            return IsValidCountryCode(code) ? code.ToLowerInvariant() : "xx";
        }
    }
    public Uri FlagResourceUri => new($"pack://application:,,,/flags/{FlagCode}.svg", UriKind.Absolute);
    /// <summary>The bundled flag of a country code; null for a code that is not two letters.</summary>
    public static Uri? FlagUriFor(string? countryCode)
    {
        var code = (countryCode ?? "").Trim().ToUpperInvariant();
        if (code == "UK") code = "GB";
        return IsValidCountryCode(code) ? new Uri($"pack://application:,,,/flags/{code.ToLowerInvariant()}.svg", UriKind.Absolute) : null;
    }
    public bool HasLocalFlag => FlagCode != "xx";
    public string FlagEmoji => CountryFlag(FlagCode.ToUpperInvariant());
    public bool HasFlagImage => HasLocalFlag;
    public string FlagFallbackText => HasLocalFlag ? FlagEmoji : "--";
    public string? FlagSvgUrl => FlagResourceUri.ToString();
    public string? FlagImageUrl => null;

    public void ApplyPing(int? ping, bool measured = true)
    {
        if (ping is > 0)
        {
            Ping = ping;
            PingFailed = false;
            PingEstimated = !measured;
            return;
        }

        Ping = null;
        PingFailed = !measured;
        PingEstimated = false;
    }

    private static string PingQuality(int? ping)
    {
        if (ping is null or <= 0) return "Unknown";
        if (ping <= 80) return "Excellent";
        if (ping <= 200) return "Good";
        return "High";
    }

    private static string CountryFlag(string? countryCode)
    {
        var code = (countryCode ?? "").Trim().ToUpperInvariant();
        if (!IsValidCountryCode(code)) return "??";

        var chars = code
            .SelectMany(ch => char.ConvertFromUtf32(0x1F1E6 + ch - 'A'))
            .ToArray();
        return new string(chars);
    }

    private static bool IsValidCountryCode(string? countryCode)
    {
        var code = (countryCode ?? "").Trim().ToUpperInvariant();
        return code.Length == 2 && code.All(ch => ch is >= 'A' and <= 'Z');
    }
}

public sealed class ColituVpnConfigResponse
{
    public string? ServerId { get; set; }
    public ColituVpnServer? Server { get; set; }
    public string? ConfigType { get; set; }
    public string? ProtocolType { get; set; }
    /// <summary>Share links for every candidate transport, primary first.</summary>
    public string? RawConfig { get; set; }
    public List<ColituConfigCandidate> Candidates { get; set; } = [];
    public ulong Revision { get; set; }
    public string? ExpiresAt { get; set; }
    public DateTimeOffset? OfflineGraceUntil { get; set; }
    public bool Unlimited { get; set; }
}

public sealed class ColituConnectResponse
{
    public bool Ok { get; set; }
    public string? Status { get; set; }
    public ColituVpnServer? SelectedServer { get; set; }
}

public sealed class ColituStatsResponse
{
    public bool Ok { get; set; }
    public ColituSubscription? Subscription { get; set; }
    public bool Unlimited { get; set; }
    public ColituUsageStats Stats { get; set; } = new();
}

public sealed class ColituUsageStats
{
    public long TotalUsedBytes { get; set; }
    public long TotalConnectedSeconds { get; set; }
    [JsonConverter(typeof(FlexibleIntJsonConverter))]
    public int ServersUsed { get; set; }
    public ColituUsageDay Today { get; set; } = new();
    public List<ColituUsageDay> Days { get; set; } = [];
}

public sealed class ColituUsageDay
{
    public string? Date { get; set; }
    public long UsedBytes { get; set; }
    public long ConnectedSeconds { get; set; }
    [JsonConverter(typeof(FlexibleIntJsonConverter))]
    public int ServersUsed { get; set; }
}

public sealed class FlexibleIntJsonConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Number => reader.TryGetInt32(out var value) ? value : 0,
            JsonTokenType.String => int.TryParse(reader.GetString(), out var value) ? value : 0,
            JsonTokenType.StartArray => CountArrayItems(ref reader),
            JsonTokenType.Null => 0,
            _ => SkipAndReturnZero(ref reader)
        };
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value);
    }

    private static int CountArrayItems(ref Utf8JsonReader reader)
    {
        var count = 0;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return count;
            }

            count += 1;
            if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
            {
                reader.Skip();
            }
        }

        return count;
    }

    private static int SkipAndReturnZero(ref Utf8JsonReader reader)
    {
        if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
        {
            reader.Skip();
        }

        return 0;
    }
}

public sealed class ColituBestServerResponse
{
    public bool Ok { get; set; }
    public ColituVpnServer? Server { get; set; }
    public int Score { get; set; }
}

public sealed class ColituPingEntry
{
    public string? ServerId { get; set; }
    public int? Ping { get; set; }
    public string? Quality { get; set; }
    public bool Online { get; set; }
    public string? MeasuredAt { get; set; }
}

public sealed class ColituPingAllResponse
{
    public bool Ok { get; set; }
    public string? RequestId { get; set; }
    public List<ColituPingEntry> Pings { get; set; } = [];
}

public sealed record ColituVpnPreferences(
    bool KillSwitchEnabled = true,
    bool DnsLeakProtectionEnabled = true,
    bool AutoConnectEnabled = true,
    bool SplitTunnelingEnabled = false,
    List<string>? SplitTunnelApps = null,
    string? PreviousRoutingId = null,
    string Language = "",
    // 2.6.0: new installs start in TUN mode (the whole computer); saved proxy-mode states keep proxy mode.
    string ConnectionMode = ColituConnectionModes.Tun,
    bool CloseToTray = true,
    bool AdBlockEnabled = false,
    // Privacy mode: no direct-routing exceptions (Russian sites and addresses go through the tunnel too).
    // Off here so states saved before the setting existed keep their behaviour; new installs start with ForNewInstall() (on).
    bool PrivacyModeEnabled = false,
    // The one-time notice about Russian sites leaving outside the tunnel has been shown.
    bool RuDirectNoticeShown = false,
    // Split tunnelling: off, bypass (listed apps/sites/addresses outside the VPN) or only (only they use it).
    string SplitTunnelMode = ColituSplitTunnelModes.Off,
    List<string>? SplitTunnelDomains = null,
    List<string>? SplitTunnelNetworks = null,
    // The kill switch keeps the local network (printers, the router page) reachable.
    bool KillSwitchAllowLan = true,
    // A proxy-mode user from before 2.6.0 was asked once whether to switch to TUN mode.
    bool TunModePromptShown = false,
    // Local date (yyyy-MM-dd) the trial-ending banner was dismissed; it comes back the next day.
    string? TrialBannerDismissedOn = null,
    // Warm spare: a second path inside the core takes over within seconds when the first one dies.
    bool WarmSpareEnabled = true,
    // Advanced mode shows split tunnelling, connection mode, kill switch and the other expert settings.
    // On here, so users updating from a version without the setting keep what they saw; new installs
    // start in Simple mode (ForNewInstall).
    bool AdvancedMode = true)
{
    /// <summary>
    /// Preferences of a first run (no saved state yet): privacy mode on, Simple mode. The record
    /// defaults stay as they were for saved states that predate the settings.
    /// </summary>
    public static ColituVpnPreferences ForNewInstall() => new() { PrivacyModeEnabled = true, AdvancedMode = false };

    public bool IsTunMode => string.Equals(ConnectionMode, ColituConnectionModes.Tun, StringComparison.OrdinalIgnoreCase);

    public ColituVpnPreferences Normalize()
    {
        var language = Loc.Normalize(Language);
        var connectionMode = string.Equals(ConnectionMode?.Trim(), ColituConnectionModes.Tun, StringComparison.OrdinalIgnoreCase)
            ? ColituConnectionModes.Tun
            : ColituConnectionModes.Proxy;

        return this with
        {
            // Full paths since 2.6.0; bare names saved by older builds can't be picked in the list and are dropped.
            SplitTunnelApps = ColituSplitTunnel.NormalizeList(SplitTunnelApps, ColituSplitTunnel.NormalizeApp, ColituSplitTunnel.MaxApps),
            SplitTunnelDomains = ColituSplitTunnel.NormalizeList(SplitTunnelDomains, ColituSplitTunnel.NormalizeDomain, ColituSplitTunnel.MaxDomains),
            SplitTunnelNetworks = ColituSplitTunnel.NormalizeList(SplitTunnelNetworks, ColituSplitTunnel.NormalizeNetwork, ColituSplitTunnel.MaxNetworks),
            SplitTunnelMode = ColituSplitTunnelModes.Normalize(SplitTunnelMode),
            Language = language,
            ConnectionMode = connectionMode
        };
    }

}

/// <summary>The warm spare planned for the next core start: which primary profile it backs and with what.</summary>
internal sealed record ColituSparePlan(string PrimaryIndexId, ProfileItem Spare, string Protocol, string? ServerId, string Reason = "", string? NextServerId = null);

/// <summary>Another server's transports for the warm spare (automatic mode).</summary>
internal sealed record ColituSpareServer(string ServerId, List<(string Protocol, ProfileItem Item)> Items, ColituVpnConfigResponse Config, bool Cached);

/// <summary>Parallel connect: only the spare on the next server carried traffic; it becomes the primary.</summary>
internal sealed class ColituSpareServerWins(ColituSpareServer spare, string protocol)
    : Exception($"the warm spare {spare.ServerId}/{protocol} carried traffic, the primary did not")
{
    public ColituSpareServer Spare { get; } = spare;
    public string Protocol { get; } = protocol;
}

/// <summary>The spare server's settings on their way.</summary>
internal sealed record ColituSpareFetch(string ServerId, Task<ColituSpareServer?> Task);

public sealed record ColituVpnSession
{
    public ColituVpnStatus Status { get; init; } = ColituVpnStatus.Disconnected;
    public string? SelectedServerId { get; init; }
    public DateTimeOffset? ConnectedAt { get; init; }
    public string SelectionMode { get; init; } = ColituServerSelectionModes.Manual;
    public ColituVpnPreferences Preferences { get; init; } = new();
    /// <summary>Highest one-time preference migration applied to this saved state.</summary>
    public int PreferencesMigration { get; init; }
    /// <summary>Last non-empty <c>client_country</c> of the server list (the panel sends none through the VPN).</summary>
    public string? ClientCountry { get; init; }
    /// <summary>Last non-empty <c>client_network</c> of the server list; part of the Adaptive Connect network key.</summary>
    public string? ClientNetwork { get; init; }
    /// <summary>Adaptive Connect memory (expiring, per network).</summary>
    public List<ColituAdaptiveEntry>? AdaptiveMemory { get; init; }
    /// <summary>Network hints: the last network token, the client_network it belongs to and when it came.</summary>
    public string? NetworkToken { get; init; }
    public string? NetworkTokenNetwork { get; init; }
    public DateTimeOffset? NetworkTokenAt { get; init; }
    /// <summary>Protocols blocked on <see cref="NetworkHintsNetwork"/> according to the panel.</summary>
    public List<string>? NetworkHintsBlocked { get; init; }
    /// <summary>Protocols that worked for most devices on <see cref="NetworkHintsNetwork"/>, best first (stored with the blocked ones).</summary>
    public List<string>? NetworkHintsPreferred { get; init; }
    public string? NetworkHintsNetwork { get; init; }
    /// <summary>Recovery set: the last fetch attempt (at most one per 6 h) and the account it was made for.</summary>
    public DateTimeOffset? RecoveryAttemptAt { get; init; }
    public string? RecoveryAttemptUser { get; init; }
}

public static class ColituServerSelectionModes
{
    public const string Manual = "manual";
    public const string Best = "best";
}

public static class ColituConnectionModes
{
    public const string Proxy = "proxy";
    public const string Tun = "tun";
}
