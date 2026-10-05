using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ServiceLib.Handler.Builder;
using ServiceLib.Handler.SysProxy;
using ServiceLib.Helper;

namespace v2rayN.Services;

public sealed class ColituVpnService
{
    public static ColituVpnService Instance { get; } = new();

    private const string ColituSubId = "colitu-api";
    private const string ColituRoutingRemarks = "Colitu VPN Protection";
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
    private readonly ColituKillSwitch _killSwitch = new();
    private int _watchdogTicks;
    private volatile bool _autoReconnecting;
    private int _autoReconnectBusy;
    private bool _proxyModeWarned;
    /// <summary>Status saved by the previous run, before this run overwrote the state file.</summary>
    private ColituVpnStatus _previousRunStatus;
    private readonly object _stateLock = new();
    private bool _userDisconnected;
    private CancellationTokenSource? _connectCts;
    private bool _otherVpnWarned;
    /// <summary>Transports that stalled recently, per server ("serverId|protocol"), kept last until this time.</summary>
    // Written by the watchdog (thread pool) and read while the UI connects.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _stalledTransports = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Health checks in a row that carried no traffic through the tunnel.</summary>
    private int _failedChecks;
    /// <summary>DNS checks in a row the tunnel's resolver did not answer.</summary>
    private int _failedDnsChecks;
    /// <summary>The tunnel's resolver answered once this session, so a silence means it hangs (not that the check can't work).</summary>
    private bool _dnsCheckWorks;
    private Timer? _networkChangeDebounce;
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
        DeleteLegacyConfigCache();
        StartWatchdog();
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

    /// <summary>"Best server": the panel chooses the recommended node.</summary>
    public bool IsAutoSelection => SelectedServer == null
        && (_session.SelectedServerId.IsNullOrEmpty() || string.Equals(_session.SelectionMode, ColituServerSelectionModes.Best, StringComparison.OrdinalIgnoreCase));

    public string? SavedServerId => IsAutoSelection ? null : SelectedServer?.Id ?? _session.SelectedServerId;

    public async Task<ColituServersResponse> GetServersAsync()
    {
        try
        {
            var servers = await _api.GetServersAsync();
            _lastServers = servers.Servers;
            if (!IsAutoSelection && SelectedServer == null && _session.SelectedServerId.IsNotEmpty())
            {
                SelectedServer = servers.Servers.FirstOrDefault(s => string.Equals(s.Id, _session.SelectedServerId, StringComparison.OrdinalIgnoreCase));
            }
            return servers;
        }
        catch (ColituApiException ex) when (IsPlanError(ex))
        {
            return new ColituServersResponse { PlanRequired = true };
        }
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
        _failedChecks = 0;
        _failedDnsChecks = 0;
        _dnsCheckWorks = false;
        _offlineNoticeShown = false;
        _offlineVerdicts = 0;
        _waitingForNetwork = false;
        _watchdogTicks = 0;
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
        SetStatus(ColituVpnStatus.Connecting);
        LastError = null;
        _lastCoreMessage = null;
        _credentialsRefused = false;
        SaveState();

        try
        {
            LogConnection($"Connecting to {(server == null ? "best server" : $"server id={server.Id}, name={server.Name}")}");
            // Wi-Fi just reconnected (or the PC woke up): give the adapter a moment to get its
            // address and route instead of failing every transport in a row.
            for (var waited = 0; waited < 20 && !(ColituNetwork.HasPhysicalNetwork() && ColituNetwork.HasDefaultRoute()); waited++)
            {
                await Task.Delay(1000, token);
            }
            if (EffectiveTunMode && !_otherVpnWarned && ColituNetwork.CompetingVpnAdapter() is { } other)
            {
                _otherVpnWarned = true;
                LogConnection($"Another VPN adapter holds a default route: {other}");
                Notice?.Invoke("warn.otherVpn");
            }
            var config = await FetchConfigAsync(server, token, quick);
            token.ThrowIfCancellationRequested();
            if (config.ServerId.IsNotEmpty() && server?.Id is { } requested && !string.Equals(config.ServerId, requested, StringComparison.OrdinalIgnoreCase))
            {
                // The panel falls back to another healthy node when the preferred one is unavailable.
                LogConnection($"Panel selected fallback node {config.ServerId} instead of {requested}");
            }

            var connected = FindServer(config.ServerId) ?? config.Server ?? server;
            var profiles = await ImportConfigAsync(config, connected);
            await PrepareConnectionModeAsync(config.Server?.CountryCode ?? connected?.CountryCode);
            if (EffectiveTunMode && Preferences.KillSwitchEnabled && !_killSwitch.IsEngaged)
            {
                // Before the TUN adapter comes up: from its first second Windows must not ask the
                // network adapter's DNS server. The core and this app stay allowed out.
                killSwitchEngagedHere = EngageKillSwitch("TUN connecting");
                if (!killSwitchEngagedHere)
                {
                    // No WFP filters: let sing-box's strict route keep DNS inside the tunnel instead.
                    _config.TunModeItem.StrictRoute = true;
                }
            }
            try
            {
                await StartFirstWorkingProfileAsync(profiles, config, connected, token);
            }
            catch (Exception ex) when (_credentialsRefused && ex is not OperationCanceledException)
            {
                // The panel creates this device's credentials for a server the first time it is
                // chosen; the server applies them some seconds later and refuses us until then.
                LogConnection("The server refused the credentials (new on this server); retrying in 15 s");
                await StopCoreAsync();
                await Task.Delay(TimeSpan.FromSeconds(15), token);
                _credentialsRefused = false;
                await StartFirstWorkingProfileAsync(profiles, config, connected, token);
            }
            ConnectedServer = connected;
            ConnectedAt = DateTimeOffset.Now;
            ResetHealthChecks();
            ApplyKillSwitchForConnectedTunnel();
            SetStatus(ColituVpnStatus.Connected);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            LogConnection("Connection attempt cancelled");
            if (killSwitchEngagedHere) ReleaseKillSwitch("connection attempt cancelled");
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
            LastError = FriendlyConnectionError(ex);
            LogConnection($"Connection failed: {ex}");
            // Engaged by this attempt (not by a lost tunnel): a failed manual connect must not
            // leave the computer offline.
            if (killSwitchEngagedHere) ReleaseKillSwitch("connection failed");
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
            throw new ColituConnectException(LastError, ex);
        }
    }

    /// <summary>
    /// Asks the panel for fresh connection settings. When the panel is slow or cannot be
    /// reached (offline, blocked, down) the last settings received for the same choice are
    /// used until their offline grace period ends ("best server": any cached server).
    /// An automatic reconnect (<paramref name="quick"/>) skips the preference call (the choice
    /// did not change) and waits at most 3 s for the panel, a user's connect 10 s.
    /// </summary>
    private async Task<ColituVpnConfigResponse> FetchConfigAsync(ColituVpnServer? server, CancellationToken token, bool quick = false)
    {
        // Bound to the account: settings cached for one account never connect another one.
        var cacheKey = $"{ColituAuthService.Instance.CurrentUser?.Id ?? "-"}|{server?.Id ?? "auto"}";
        var fallback = LoadCachedConfig(cacheKey) ?? (server == null ? LoadAnyCachedConfig() : null);
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
            if (!quick)
            {
                await _api.SetPreferredServerAsync(server?.Id, budget.Token);
            }
            var preferenceMs = watch.ElapsedMilliseconds;
            var config = await _api.GetConfigAsync(server, budget.Token)
                ?? throw new ColituConnectException(Loc.I["err.noServers"]);
            LogConnection($"Panel answered in {watch.ElapsedMilliseconds} ms (preference {preferenceMs} ms, config {watch.ElapsedMilliseconds - preferenceMs} ms)");
            SaveCachedConfig(cacheKey, config);
            return config;
        }
        catch (Exception ex) when (!token.IsCancellationRequested && fallback != null && (ex is OperationCanceledException || IsNetworkFailure(ex)))
        {
            LogConnection($"Panel did not answer in {watch.ElapsedMilliseconds} ms ({ex.GetType().Name}); using cached connection settings (server {fallback.ServerId}, revision {fallback.Revision})");
            return fallback;
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
        ReleaseKillSwitch("disconnected by the user");
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
            ReleaseKillSwitch("kill switch turned off");
        }
        else if (Status == ColituVpnStatus.Connected)
        {
            ApplyKillSwitchForConnectedTunnel();
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
            }).Wait(TimeSpan.FromSeconds(5));
            ReleaseKillSwitch("Windows session ending");
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
        if (Status != ColituVpnStatus.Disconnected || _killSwitch.IsEngaged)
        {
            await DisconnectAsync();
        }
        DeleteConfigCache();
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
        SelectedServer = null;
        ConnectedServer = null;
    }

    // ── Health watch: keep the tunnel working ──────────────────────────────
    private void StartWatchdog()
    {
        _watchdog = new Timer(_ => _ = WatchdogTickAsync(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
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
        // The timer fires every 2 s while a probe can take up to 14 s: one tick at a time,
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
        if (!CoreManager.Instance.IsCoreRunning)
        {
            await RecoverTunnelAsync("VPN core stopped unexpectedly", coreStopped: true);
            return;
        }

        // Every 10 s make sure traffic still flows (server gone, network changed, transport stalled).
        if (++_watchdogTicks % 5 != 0)
        {
            return;
        }
        var port = AppManager.Instance.GetLocalPort(EInboundProtocol.socks);
        var probe = await ProbeThroughLocalProxyAsync(port, 1);
        if (!CanHeal())
        {
            return;
        }
        if (probe.Success)
        {
            _failedChecks = 0;
            _offlineVerdicts = 0;
            _offlineNoticeShown = false;
            _waitingForNetwork = false;
            // Every 30 s, and on every check while the resolver is silent.
            if (_failedDnsChecks > 0 || _watchdogTicks % 15 == 0)
            {
                await CheckTunnelDnsAsync();
            }
            return;
        }
        // A slow moment (a busy server, a burst of loss) is not a dead tunnel: only a second
        // failed check about 10 s later acts.
        if (++_failedChecks < 2)
        {
            LogConnection($"Tunnel check failed ({probe.Detail}); checking again");
            return;
        }
        _failedChecks = 0;
        await RecoverTunnelAsync($"tunnel carries no traffic: {probe.Detail}");
    }

    /// <summary>
    /// The tunnel stopped carrying traffic. First look outside the tunnel which part broke:
    /// with no internet at all nothing can be fixed (keep the tunnel, tell the user, wait); when
    /// only the server is unreachable a restart can't help (reconnect, the panel may move to
    /// another node); otherwise restart the core on the same server and transport (a second or
    /// two), and only when that does not help, reconnect from scratch (next transport).
    /// </summary>
    private async Task RecoverTunnelAsync(string reason, bool coreStopped = false)
    {
        if (!ColituNetwork.HasPhysicalNetwork())
        {
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
                return;
            }
            // Three "offline" verdicts in a row (about a minute) while an adapter is connected: the
            // check itself may be what is blocked, so the restart goes ahead anyway.
            if (path == ColituPathState.Offline && ++_offlineVerdicts < 3)
            {
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
        if (await TryRestartCoreAsync(reason) || _userDisconnected)
        {
            return;
        }
        if (_activeTransport != null)
        {
            _stalledTransports[StallKey(ConnectedServer?.Id, _activeTransport)] = DateTimeOffset.UtcNow.AddMinutes(10);
            LogConnection($"Transport {_activeTransport} stalled; it goes last for the next 10 minutes");
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
    private async Task<bool> TryRestartCoreAsync(string reason)
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
            var probe = await VerifyConnectionActiveAsync(ConnectedServer, 2, token);
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
            if (!CanHeal() || physical == null || bound.IsNullOrEmpty() || string.Equals(physical, bound, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            LogConnection($"Network changed ({bound} -> {physical}); moving the tunnel to the new adapter");
            if (!await TryRestartCoreAsync("network changed") && !_userDisconnected)
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
        if (Preferences.KillSwitchEnabled && EngageKillSwitch("tunnel lost"))
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
            var probe = await ProbeThroughLocalProxyAsync(AppManager.Instance.GetLocalPort(EInboundProtocol.socks));
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
                    ReleaseKillSwitch("no active plan");
                    Notice?.Invoke("err.noPlan");
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
            // the background so the internet returns as soon as the VPN can.
            while (!_userDisconnected && _killSwitch.IsEngaged && Status == ColituVpnStatus.Error)
            {
                await Task.Delay(TimeSpan.FromSeconds(20));
                if (_userDisconnected || !_killSwitch.IsEngaged || Status != ColituVpnStatus.Error || _connectionLock.CurrentCount == 0)
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
                    ReleaseKillSwitch("no active plan");
                    Notice?.Invoke("err.noPlan");
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
    /// <summary>True while the kill switch is blocking traffic outside the VPN.</summary>
    public bool KillSwitchEngaged => _killSwitch.IsEngaged;

    /// <summary>
    /// TUN mode sends everything through the adapter, so the switch stays on for
    /// the whole session and closes any gap if the tunnel drops. In proxy mode
    /// apps may legitimately bypass the proxy, so the switch only closes when the
    /// tunnel is lost.
    /// </summary>
    private void ApplyKillSwitchForConnectedTunnel()
    {
        var preferences = Preferences;
        if (preferences.KillSwitchEnabled && EffectiveTunMode)
        {
            EngageKillSwitch("TUN session");
        }
        else
        {
            ReleaseKillSwitch("tunnel is up");
        }
    }

    private bool EngageKillSwitch(string reason)
    {
        if (_killSwitch.IsEngaged)
        {
            return true;
        }
        try
        {
            var programs = new[]
            {
                Utils.GetBinPath("xray.exe", "xray"),
                Utils.GetBinPath("sing-box.exe", "sing_box"),
                Environment.ProcessPath ?? ""
            };
            _killSwitch.Engage(programs);
            LogConnection($"Kill switch engaged ({reason})");
            return true;
        }
        catch (Exception ex)
        {
            LogConnection($"Kill switch could not be engaged: {ex.Message}");
            Notice?.Invoke("err.killSwitch");
            return false;
        }
    }

    private void ReleaseKillSwitch(string reason)
    {
        if (!_killSwitch.IsEngaged)
        {
            return;
        }
        _killSwitch.Release();
        LogConnection($"Kill switch released ({reason})");
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
    private ColituVpnConfigResponse? LoadAnyCachedConfig()
    {
        var prefix = $"{ColituAuthService.Instance.CurrentUser?.Id ?? "-"}|";
        return ReadConfigCache()
            .Where(item => item.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(item => LoadCachedConfig(item.Key))
            .FirstOrDefault(config => config != null);
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
        // Hysteria2 is not an Xray protocol; it runs on the bundled sing-box core.
        _config.CoreTypeItem = [new CoreTypeItem { ConfigType = EConfigType.Hysteria2, CoreType = ECoreType.sing_box }];
        // No declared bandwidth: Hysteria2 then uses BBR, as on Android. v2rayN's 100/100 Mbps
        // default selects Brutal, which sends at 100 Mbps whatever the line can carry; on slower
        // links that means heavy loss and stalls.
        _config.HysteriaItem ??= new HysteriaItem();
        _config.HysteriaItem.UpMbps = 0;
        _config.HysteriaItem.DownMbps = 0;
        _coreReady = true;
    }

    /// <summary>
    /// Imports every transport offered by the panel for the selected location
    /// (primary first) and returns the matching v2rayN profiles in that order.
    /// </summary>
    private async Task<List<(ColituConfigCandidate Candidate, ProfileItem Profile)>> ImportConfigAsync(ColituVpnConfigResponse config, ColituVpnServer? server)
    {
        var candidates = config.Candidates;
        LogConnection($"Importing config for serverId={config.ServerId ?? server?.Id}, revision={config.Revision}, candidates={string.Join(",", candidates.Select(item => item.Protocol))}");
        if (candidates.Count == 0 || string.IsNullOrWhiteSpace(config.RawConfig))
        {
            throw new InvalidOperationException("Server config is not ready.");
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

        var latency = await MeasureTransportLatencyAsync(ordered);
        ordered = ordered
            .OrderBy(item => TransportRank(item.Candidate.Protocol, latency.GetValueOrDefault(item.Candidate.Protocol, -1), StalledRecently(config.ServerId ?? server?.Id, item.Candidate.Protocol)))
            .ThenBy(item => latency.GetValueOrDefault(item.Candidate.Protocol, int.MaxValue))
            .ToList();
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
    /// recently moved to the back of the line for a while.
    /// </summary>
    internal static int TransportRank(string protocol, int latencyMs, bool stalledRecently)
    {
        var rank = TransportPriority(protocol);
        if (latencyMs < 0 && protocol != "hysteria2")
        {
            rank += 10;
        }
        if (stalledRecently)
        {
            rank += 20;
        }
        return rank;
    }

    private bool StalledRecently(string? serverId, string protocol) =>
        _stalledTransports.TryGetValue(StallKey(serverId, protocol), out var until) && until > DateTimeOffset.UtcNow;

    /// <summary>A transport blocked on one server says nothing about the same transport on another.</summary>
    private static string StallKey(string? serverId, string protocol) => $"{serverId ?? "-"}|{protocol}";

    /// <summary>
    /// Starts the core with each candidate transport until traffic flows through
    /// one of them, then reports the observations back to the panel.
    /// </summary>
    private async Task StartFirstWorkingProfileAsync(List<(ColituConfigCandidate Candidate, ProfileItem Profile)> profiles, ColituVpnConfigResponse config, ColituVpnServer? server, CancellationToken token)
    {
        var observations = new List<ColituProtocolObservation>();
        try
        {
            for (var index = 0; index < profiles.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var (candidate, profile) = profiles[index];
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
                    await ReloadCoreAsync(token);
                    // Hysteria2's first QUIC handshake right after the TUN adapter appears sometimes
                    // needs longer than one round; it gets a second one unless the server refused us.
                    var rounds = isLast || candidate.Protocol == "hysteria2" ? 2 : 1;
                    var probe = await VerifyConnectionActiveAsync(server, rounds, token);
                    observations.Add(new ColituProtocolObservation { Protocol = candidate.Protocol, Reachable = probe.Success, LatencyMs = probe.LatencyMs });
                    if (probe.Success)
                    {
                        _activeTransport = candidate.Protocol;
                        return;
                    }
                    // Without internet every transport fails: say so instead of blaming (and marking)
                    // each one in turn. 2026-10-05: a connect while Wi-Fi was off marked all of them,
                    // and the next server then started with Shadowsocks.
                    if (!_credentialsRefused && !await InternetReachableDirectAsync())
                    {
                        observations.Clear();
                        LogConnection("No internet outside the tunnel either; not trying the other transports");
                        throw new ColituConnectException(Loc.I["warn.noInternet"]);
                    }
                    // A transport that carried nothing goes last on the next attempts too (UDP blocked on
                    // this network), so a reconnect does not wait for it again. Refused credentials are
                    // a server still applying new ones, not a property of the transport.
                    if (!_credentialsRefused)
                    {
                        _stalledTransports[StallKey(config.ServerId ?? server?.Id, candidate.Protocol)] = DateTimeOffset.UtcNow.AddMinutes(10);
                    }
                    if (isLast)
                    {
                        // No transport carried traffic: report it instead of claiming a connection.
                        throw new ColituConnectException(Loc.I["err.unreachable"]);
                    }
                }
                catch (Exception ex) when (!isLast && ex is not (OperationCanceledException or ColituConnectException))
                {
                    observations.Add(new ColituProtocolObservation { Protocol = candidate.Protocol, Reachable = false });
                    LogConnection($"Transport {candidate.Protocol} failed: {ex.Message}");
                }

                await StopCoreAsync();
            }
        }
        finally
        {
            _ = _api.ReportProtocolObservationsAsync(config.ServerId ?? server?.Id, observations);
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

        await ConfigHandler.SaveConfig(_config);
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
        var rules = BuildColituRoutingRules(preferences, serverCountry);

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
        LogConnection(RussianSitesDirect(serverCountry)
            ? "Routing profile applied: DNS protection, Russian sites direct"
            : "Routing profile applied: DNS protection, Russian sites through the Russian server");
    }

    /// <summary>
    /// Russian sites skip the tunnel unless the server itself is in Russia: someone abroad who
    /// picks the Moscow server wants exactly those sites to see a Russian address.
    /// </summary>
    internal static bool RussianSitesDirect(string? serverCountry) =>
        !string.Equals(serverCountry?.Trim(), "RU", StringComparison.OrdinalIgnoreCase);

    internal static List<RulesItem> BuildColituRoutingRules(ColituVpnPreferences preferences, string? serverCountry = null)
    {
        var ruDirect = RussianSitesDirect(serverCountry);
        preferences = preferences.Normalize();
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

    private async Task<ColituTrafficProbeResult> VerifyConnectionActiveAsync(ColituVpnServer? server, int rounds = 2, CancellationToken token = default)
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

        // The server refusing our credentials will not change in a second round.
        var probe = await ProbeThroughLocalProxyAsync(socksPort, rounds, token,
            () => _lastCoreMessage?.Contains("authentication failed", StringComparison.OrdinalIgnoreCase) == true);
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

    /// <summary>
    /// Fetches small "connectivity check" pages through the tunnel, all at once,
    /// and succeeds on the first answer. Two rounds of at most seven seconds.
    /// </summary>
    private static async Task<ColituTrafficProbeResult> ProbeThroughLocalProxyAsync(int port, int rounds = 2, CancellationToken token = default, Func<bool>? giveUp = null)
    {
        var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy($"{Global.Socks5Protocol}{Global.Loopback}:{port}"),
            UseProxy = true,
            ConnectTimeout = TimeSpan.FromSeconds(5)
        };

        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(7) };
        var speedProbeUrl = AppManager.Instance.Config.SpeedTestItem.SpeedPingTestUrl.NullIfEmpty()
            ?? Global.SpeedPingTestUrls.First();
        var probeUrls = new[] { speedProbeUrl, "https://www.gstatic.com/generate_204", "https://cp.cloudflare.com/generate_204", "https://www.apple.com/library/test/success.html", "http://www.msftconnecttest.com/connecttest.txt" }
            .Where(item => item.IsNotEmpty())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();

        for (var round = 1; round <= rounds; round++)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(TimeSpan.FromSeconds(7));
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
            if ((int)response.StatusCode < 500)
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
        _session = _session with { Status = status, ConnectedAt = ConnectedAt };
        SaveState();
        StatusChanged?.Invoke(status);
    }

    private const int CurrentPreferencesMigration = 1;

    private void LoadState()
    {
        try
        {
            if (!File.Exists(StatePath()))
            {
                _session = _session with { PreferencesMigration = CurrentPreferencesMigration };
                return;
            }
            _session = JsonSerializer.Deserialize<ColituVpnSession>(File.ReadAllText(StatePath()), _jsonOptions) ?? new();
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
            Preferences = _session.Preferences.Normalize()
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

/// <summary>A connection failure whose message is already localized for the user.</summary>
public sealed class ColituConnectException(string message, Exception? inner = null) : Exception(message, inner);

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
    public ColituVpnServer? SelectedServer { get; set; }
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
    string ConnectionMode = ColituConnectionModes.Proxy,
    bool CloseToTray = true,
    bool AdBlockEnabled = false)
{
    public bool IsTunMode => string.Equals(ConnectionMode, ColituConnectionModes.Tun, StringComparison.OrdinalIgnoreCase);

    public ColituVpnPreferences Normalize()
    {
        var language = Loc.Normalize(Language);
        var connectionMode = string.Equals(ConnectionMode?.Trim(), ColituConnectionModes.Tun, StringComparison.OrdinalIgnoreCase)
            ? ColituConnectionModes.Tun
            : ColituConnectionModes.Proxy;

        return this with
        {
            SplitTunnelApps = SplitTunnelApps?
                .Select(NormalizeProcessName)
                .Where(app => app.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? [],
            Language = language,
            ConnectionMode = connectionMode
        };
    }

    public string SplitTunnelAppsText => string.Join(", ", Normalize().SplitTunnelApps);

    public static List<string> ParseApps(string? value)
    {
        return (value ?? "")
            .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeProcessName)
            .Where(app => app.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizeProcessName(string value)
    {
        var app = value.Trim().Trim('"');
        if (app.Length == 0 || app.Contains('/') || app.Contains('\\') || app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return app;
        }

        return $"{app}.exe";
    }
}

public sealed record ColituVpnSession
{
    public ColituVpnStatus Status { get; init; } = ColituVpnStatus.Disconnected;
    public string? SelectedServerId { get; init; }
    public DateTimeOffset? ConnectedAt { get; init; }
    public string SelectionMode { get; init; } = ColituServerSelectionModes.Manual;
    public ColituVpnPreferences Preferences { get; init; } = new();
    /// <summary>Highest one-time preference migration applied to this saved state.</summary>
    public int PreferencesMigration { get; init; }
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
