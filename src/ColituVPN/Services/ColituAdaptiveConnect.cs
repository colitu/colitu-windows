namespace v2rayN.Services;

/// <summary>
/// Adaptive Connect: what "best server" connects to, and what this device learnt about the
/// network it is on. A server can answer the ping and still carry no VPN traffic (Russian LTE),
/// so the ping is only a hint; a connection that carried traffic decides. Pure logic, no I/O.
/// </summary>
public static class ColituAdaptiveConnect
{
    /// <summary>A ping older than this, or taken on another network, is not used for the order.</summary>
    public static readonly TimeSpan PingFreshFor = TimeSpan.FromMinutes(10);

    /// <summary>At most this many servers per connect in automatic mode.</summary>
    public const int MaxServersPerConnect = 3;

    /// <summary>The panel takes at most this many node ids in <c>exclude=</c>.</summary>
    public const int MaxExclude = 10;

    /// <summary>
    /// Network hints: the panel says <paramref name="protocol"/> fails for most devices on this ISP
    /// network. It goes last, unless it carried traffic on this network in the last 24 h (this
    /// device's own experience beats the hint).
    /// </summary>
    public static bool HintSaysBlocked(IReadOnlyCollection<string>? blocked, string? protocol, ColituAdaptiveMemory memory, string networkKey, DateTimeOffset now) =>
        !string.IsNullOrEmpty(protocol)
        && blocked?.Contains(protocol, StringComparer.OrdinalIgnoreCase) == true
        && !memory.WorkedOnNetwork(networkKey, protocol, now);

    /// <summary>
    /// The client network after a server list: the panel's value when it sent one (a list fetched
    /// through the VPN comes back empty and never overwrites), otherwise what was known.
    /// </summary>
    public static string? ResolveClientNetwork(string? fromServerList, string? known) =>
        string.IsNullOrWhiteSpace(fromServerList) ? known : fromServerList;

    /// <summary>
    /// "&lt;link&gt;|&lt;client_network&gt;", e.g. "wifi|TR-AS9121". Memory from another key is not
    /// applied (it stays stored under its own key until it expires).
    /// </summary>
    public static string NetworkKey(string? link, string? clientNetwork) =>
        $"{(string.IsNullOrWhiteSpace(link) ? "other" : link.Trim().ToLowerInvariant())}|{clientNetwork?.Trim() ?? ""}";

    /// <summary>
    /// Server countries automatic mode never picks: a Russian exit carries the same blocks the
    /// user wants to get away from, wherever the user is. A manual choice still connects there.
    /// </summary>
    public static readonly IReadOnlySet<string> AutoExcludedCountries =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "RU" };

    public static bool AutoExcluded(ColituVpnServer server) =>
        AutoExcludedCountries.Contains(server.CountryCode?.Trim() ?? "");

    /// <summary>
    /// Servers of the list in the order automatic mode tries them (multihop routes, unavailable
    /// and locked servers and those in <see cref="AutoExcludedCountries"/> left out). The "best server" entry shows rank[0], and connect uses it:
    /// <list type="number">
    /// <item>penalized on this network: last (the oldest penalty first, so retries rotate);</item>
    /// <item>a fresh ping that failed: after all others except the penalized ones;</item>
    /// <item>the user's own country (when known): after the foreign ones;</item>
    /// <item>the last server that worked on this network: first;</item>
    /// <item>fresh successful pings, fastest first;</item>
    /// <item>servers without a fresh ping keep the panel's order, after the pinged ones.</item>
    /// </list>
    /// </summary>
    public static List<ColituVpnServer> Rank(
        IEnumerable<ColituVpnServer> servers,
        IReadOnlyDictionary<string, ColituPingSample> pings,
        ColituAdaptiveMemory memory,
        string networkKey,
        string? clientCountry,
        DateTimeOffset now)
    {
        var country = clientCountry?.Trim();
        var lastGood = memory.LastGoodServer(networkKey, now);
        return servers
            .Where(server => !string.IsNullOrWhiteSpace(server.Id) && !server.IsMultihop && server.Available && !server.Locked && !AutoExcluded(server))
            .GroupBy(server => server.Id!, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select((server, index) =>
            {
                var ping = pings.TryGetValue(server.Id!, out var sample) && sample.IsFresh(networkKey, now) ? sample : null;
                var penalizedAt = memory.PenalizedAt(networkKey, server.Id, now);
                return new
                {
                    Server = server,
                    Index = index,
                    Penalized = penalizedAt != null,
                    PenalizedAt = penalizedAt ?? DateTimeOffset.MinValue,
                    PingFailed = ping is { Ms: null },
                    OwnCountry = !string.IsNullOrEmpty(country) && string.Equals(server.CountryCode?.Trim(), country, StringComparison.OrdinalIgnoreCase),
                    LastGood = lastGood != null && string.Equals(server.Id, lastGood, StringComparison.OrdinalIgnoreCase),
                    PingMs = ping?.Ms
                };
            })
            .OrderBy(item => item.Penalized)
            .ThenBy(item => item.PenalizedAt)
            .ThenBy(item => item.PingFailed)
            .ThenBy(item => item.OwnCountry)
            .ThenBy(item => !item.LastGood)
            .ThenBy(item => item.PingMs == null)
            .ThenBy(item => item.PingMs ?? 0)
            .ThenBy(item => item.Index)
            .Select(item => item.Server)
            .ToList();
    }
}

/// <summary>One ping of a server: <see cref="Ms"/> null when the probe timed out or was refused.</summary>
public sealed record ColituPingSample(int? Ms, DateTimeOffset MeasuredAt, string NetworkKey)
{
    public bool IsFresh(string networkKey, DateTimeOffset now) =>
        string.Equals(NetworkKey, networkKey, StringComparison.Ordinal)
        && now - MeasuredAt < ColituAdaptiveConnect.PingFreshFor
        && MeasuredAt <= now + TimeSpan.FromMinutes(1);
}

/// <summary>One remembered fact, as saved in the state file.</summary>
public sealed record ColituAdaptiveEntry
{
    /// <summary><see cref="ColituAdaptiveMemory.GoodServer"/>, <c>GoodTransport</c>, <c>Stalled</c> or <c>Penalized</c>.</summary>
    public string Kind { get; init; } = "";
    public string Network { get; init; } = "";
    /// <summary>Empty for a network-wide fact.</summary>
    public string Server { get; init; } = "";
    public string Transport { get; init; } = "";
    public DateTimeOffset At { get; init; }
}

/// <summary>
/// What worked and what failed, per network (see <see cref="ColituAdaptiveConnect.NetworkKey"/>).
/// Every fact expires: last-good server and transport after 24 h, a stalled transport after
/// 6 h, a penalized server after 30 min. Thread-safe: the watchdog marks while the UI reads.
/// </summary>
public sealed class ColituAdaptiveMemory
{
    public const string GoodServer = "good-server";
    public const string GoodTransport = "good-transport";
    public const string Stalled = "stalled";
    /// <summary>A stall found during a failing round or mid-session on TCP: 10 minutes, 6 h once another transport carries traffic here.</summary>
    public const string StalledProvisional = "stalled-provisional";
    /// <summary>A mid-session stall of a transport that already worked on this network: only 90 s (the network blocked it for a while, it is not broken).</summary>
    public const string StalledShort = "stalled-short";
    public const string Penalized = "penalized";

    public static readonly TimeSpan GoodServerFor = TimeSpan.FromHours(24);
    public static readonly TimeSpan GoodTransportFor = TimeSpan.FromHours(24);
    public static readonly TimeSpan StalledFor = TimeSpan.FromHours(6);
    public static readonly TimeSpan StalledProvisionalFor = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan PenalizedFor = TimeSpan.FromMinutes(30);
    /// <summary>Penalty of a mid-session stall for a transport that is proven on this network (Android PROVEN_STALL_PENALTY).</summary>
    public static readonly TimeSpan ProvenStallPenalty = TimeSpan.FromSeconds(90);
    public const int MaxEntries = 200;

    private readonly object _gate = new();
    private readonly List<ColituAdaptiveEntry> _entries = [];

    public static TimeSpan Lifetime(string kind) => kind switch
    {
        GoodServer => GoodServerFor,
        GoodTransport => GoodTransportFor,
        Stalled => StalledFor,
        StalledProvisional => StalledProvisionalFor,
        StalledShort => ProvenStallPenalty,
        Penalized => PenalizedFor,
        _ => TimeSpan.Zero
    };

    /// <summary>
    /// How long a MID-SESSION stall puts a transport last: a transport that already worked on this
    /// network (<paramref name="proven"/>) only for <see cref="ProvenStallPenalty"/> (or less, when
    /// <paramref name="full"/> is shorter), otherwise <paramref name="full"/>. Connect-time marks don't use it.
    /// </summary>
    public static TimeSpan MidSessionPenalty(bool proven, TimeSpan full) =>
        proven && ProvenStallPenalty < full ? ProvenStallPenalty : full;

    private static bool IsStallKind(string kind) => kind is Stalled or StalledProvisional or StalledShort;

    /// <summary>Replaces the memory with saved entries, dropping expired and unknown ones.</summary>
    public void Load(IEnumerable<ColituAdaptiveEntry>? entries, DateTimeOffset now)
    {
        lock (_gate)
        {
            _entries.Clear();
            _entries.AddRange((entries ?? []).Where(entry => entry != null && entry.Network != null && entry.Server != null && entry.Transport != null));
            PruneLocked(now);
        }
    }

    /// <summary>The entries to save, expired ones pruned.</summary>
    public List<ColituAdaptiveEntry> Snapshot(DateTimeOffset now)
    {
        lock (_gate)
        {
            PruneLocked(now);
            return [.. _entries];
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    // ── Server ───────────────────────────────────────────────────────────────
    /// <summary>Traffic flowed through <paramref name="server"/>: it goes first on this network, its penalty ends.</summary>
    public void RememberGoodServer(string network, string? server, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(server)) return;
        lock (_gate)
        {
            _entries.RemoveAll(entry => entry.Kind == GoodServer && Same(entry.Network, network));
            _entries.RemoveAll(entry => entry.Kind == Penalized && Same(entry.Network, network) && Same(entry.Server, server));
            AddLocked(new() { Kind = GoodServer, Network = network, Server = server, At = now }, now);
        }
    }

    public string? LastGoodServer(string network, DateTimeOffset now)
    {
        lock (_gate)
        {
            return Find(GoodServer, network, null, null, now)?.Server;
        }
    }

    /// <summary>Every transport of <paramref name="server"/> failed on this network: it goes last for 30 minutes.</summary>
    public void Penalize(string network, string? server, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(server)) return;
        lock (_gate)
        {
            _entries.RemoveAll(entry => entry.Kind == Penalized && Same(entry.Network, network) && Same(entry.Server, server));
            // The last-good mark would put it first again.
            _entries.RemoveAll(entry => entry.Kind == GoodServer && Same(entry.Network, network) && Same(entry.Server, server));
            AddLocked(new() { Kind = Penalized, Network = network, Server = server, At = now }, now);
        }
    }

    public DateTimeOffset? PenalizedAt(string network, string? server, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(server)) return null;
        lock (_gate)
        {
            return Find(Penalized, network, server, null, now)?.At;
        }
    }

    public bool IsPenalized(string network, string? server, DateTimeOffset now) => PenalizedAt(network, server, now) != null;

    // ── Transport ────────────────────────────────────────────────────────────
    /// <summary>Traffic flowed over <paramref name="transport"/> on <paramref name="server"/>: tried first next time; its stall mark ends.</summary>
    public void RememberGoodTransport(string network, string? server, string? transport, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(transport)) return;
        lock (_gate)
        {
            _entries.RemoveAll(entry => entry.Kind == GoodTransport && Same(entry.Network, network) && Same(entry.Server, server));
            _entries.RemoveAll(entry => IsStallKind(entry.Kind) && Same(entry.Network, network) && Same(entry.Transport, transport)
                && (Same(entry.Server, server) || entry.Server.Length == 0));
            // Another transport carries traffic here: the provisional marks of this network were real (6 h).
            for (var i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Kind == StalledProvisional && Same(_entries[i].Network, network) && Alive(_entries[i], now))
                {
                    _entries[i] = _entries[i] with { Kind = Stalled, At = now };
                }
            }
            AddLocked(new() { Kind = GoodTransport, Network = network, Server = server, Transport = transport, At = now }, now);
        }
    }

    /// <summary>
    /// "Proven": the transport carried traffic on this network in the last 24 h, on any server
    /// (per-server last-good records count).
    /// </summary>
    public List<string> ProvenOnNetwork(string network, DateTimeOffset now)
    {
        lock (_gate)
        {
            return _entries.Where(entry => entry.Kind == GoodTransport && Same(entry.Network, network) && Alive(entry, now))
                .Select(entry => entry.Transport).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    /// <summary>The transport carried traffic on this network in the last 24 h, on any server.</summary>
    public bool WorkedOnNetwork(string network, string? transport, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(transport)) return false;
        lock (_gate)
        {
            return _entries.Any(entry => entry.Kind == GoodTransport && Same(entry.Network, network) && Same(entry.Transport, transport) && Alive(entry, now));
        }
    }

    public string? LastGoodTransport(string network, string? server, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(server)) return null;
        lock (_gate)
        {
            return Find(GoodTransport, network, server, null, now)?.Transport;
        }
    }

    /// <summary>
    /// <paramref name="transport"/> carried nothing on <paramref name="server"/> (null: on this
    /// network as a whole). Stalled on a second server of the same network, it is marked for the
    /// whole network: the network blocks it, not the server.
    /// </summary>
    public void MarkStalled(string network, string? server, string? transport, DateTimeOffset now, bool provisional = false)
    {
        if (string.IsNullOrWhiteSpace(transport)) return;
        var serverKey = server?.Trim() ?? "";
        if (provisional)
        {
            lock (_gate)
            {
                // A 6 h mark stays; otherwise a 10-minute one (renewed).
                if (!_entries.Any(entry => entry.Kind == Stalled && Same(entry.Network, network) && Same(entry.Transport, transport) && Same(entry.Server, serverKey) && Alive(entry, now)))
                {
                    _entries.RemoveAll(entry => entry.Kind == StalledProvisional && Same(entry.Network, network) && Same(entry.Transport, transport) && Same(entry.Server, serverKey));
                    _entries.RemoveAll(entry => entry.Kind == GoodTransport && Same(entry.Network, network) && Same(entry.Transport, transport) && Same(entry.Server, serverKey));
                    AddLocked(new() { Kind = StalledProvisional, Network = network, Server = serverKey, Transport = transport, At = now }, now);
                }
            }
            return;
        }
        lock (_gate)
        {
            var elsewhere = serverKey.Length > 0 && _entries.Any(entry => entry.Kind == Stalled && Same(entry.Network, network)
                && Same(entry.Transport, transport) && entry.Server.Length > 0 && !Same(entry.Server, serverKey) && Alive(entry, now));
            _entries.RemoveAll(entry => entry.Kind == Stalled && Same(entry.Network, network) && Same(entry.Transport, transport) && Same(entry.Server, serverKey));
            _entries.RemoveAll(entry => entry.Kind == GoodTransport && Same(entry.Network, network) && Same(entry.Transport, transport)
                && (serverKey.Length == 0 || Same(entry.Server, serverKey)));
            AddLocked(new() { Kind = Stalled, Network = network, Server = serverKey, Transport = transport, At = now }, now);
            if (elsewhere)
            {
                _entries.RemoveAll(entry => entry.Kind == Stalled && Same(entry.Network, network) && Same(entry.Transport, transport) && entry.Server.Length == 0);
                AddLocked(new() { Kind = Stalled, Network = network, Server = "", Transport = transport, At = now }, now);
            }
        }
    }

    /// <summary>
    /// A stall found MID-SESSION (the tunnel carried nothing). A transport that already worked on this
    /// network gets the short <see cref="ProvenStallPenalty"/> mark (<see cref="MidSessionPenalty"/>),
    /// otherwise the usual mark: 10 minutes (<paramref name="provisional"/>) or 6 h. Returns true when the
    /// short mark was written (log "proven here: short penalty"). A live 6 h mark is never shortened.
    /// </summary>
    public bool MarkMidSessionStall(string network, string? server, string? transport, DateTimeOffset now, bool provisional)
    {
        if (string.IsNullOrWhiteSpace(transport)) return false;
        var full = provisional ? StalledProvisionalFor : StalledFor;
        var proven = WorkedOnNetwork(network, transport, now);
        if (MidSessionPenalty(proven, full) != ProvenStallPenalty)
        {
            MarkStalled(network, server, transport, now, provisional);
            return false;
        }
        var serverKey = server?.Trim() ?? "";
        lock (_gate)
        {
            if (!_entries.Any(entry => entry.Kind == Stalled && Same(entry.Network, network) && Same(entry.Transport, transport) && Same(entry.Server, serverKey) && Alive(entry, now)))
            {
                _entries.RemoveAll(entry => (entry.Kind == StalledProvisional || entry.Kind == StalledShort) && Same(entry.Network, network) && Same(entry.Transport, transport) && Same(entry.Server, serverKey));
                _entries.RemoveAll(entry => entry.Kind == GoodTransport && Same(entry.Network, network) && Same(entry.Transport, transport) && Same(entry.Server, serverKey));
                AddLocked(new() { Kind = StalledShort, Network = network, Server = serverKey, Transport = transport, At = now }, now);
            }
        }
        return true;
    }

    /// <summary>Stalled on this server, or on this network as a whole.</summary>
    public bool IsStalled(string network, string? server, string? transport, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(transport)) return false;
        lock (_gate)
        {
            return _entries.Any(entry => IsStallKind(entry.Kind) && Same(entry.Network, network) && Same(entry.Transport, transport)
                && (entry.Server.Length == 0 || (!string.IsNullOrWhiteSpace(server) && Same(entry.Server, server))) && Alive(entry, now));
        }
    }

    // ── Store ────────────────────────────────────────────────────────────────
    private ColituAdaptiveEntry? Find(string kind, string network, string? server, string? transport, DateTimeOffset now) =>
        _entries.LastOrDefault(entry => entry.Kind == kind && Same(entry.Network, network)
            && (server == null || Same(entry.Server, server))
            && (transport == null || Same(entry.Transport, transport))
            && Alive(entry, now));

    private void AddLocked(ColituAdaptiveEntry entry, DateTimeOffset now)
    {
        _entries.Add(entry);
        PruneLocked(now);
    }

    private void PruneLocked(DateTimeOffset now)
    {
        _entries.RemoveAll(entry => !Alive(entry, now));
        if (_entries.Count > MaxEntries)
        {
            // The oldest facts go first.
            var keep = _entries.OrderByDescending(entry => entry.At).Take(MaxEntries).Reverse().ToList();
            _entries.Clear();
            _entries.AddRange(keep);
        }
    }

    /// <summary>Not expired, and not from the future (a clock set back would keep it for ever).</summary>
    private static bool Alive(ColituAdaptiveEntry entry, DateTimeOffset now)
    {
        var lifetime = Lifetime(entry.Kind);
        return lifetime > TimeSpan.Zero && now - entry.At < lifetime && entry.At <= now + TimeSpan.FromMinutes(5);
    }

    private static bool Same(string? a, string? b) => string.Equals(a?.Trim() ?? "", b?.Trim() ?? "", StringComparison.OrdinalIgnoreCase);
}
