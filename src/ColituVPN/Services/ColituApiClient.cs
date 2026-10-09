using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace v2rayN.Services;

/// <summary>
/// VPN-facing calls to the Colitu panel: server list, device preferences,
/// configuration envelopes and protocol observations. Server-side eligibility
/// is authoritative; the list is consumed in the order the panel returns it.
/// </summary>
public sealed class ColituApiClient
{
    public static ColituApiClient Instance { get; } = new();

    private const string MobileProfileFormat = "xray-mobile-v1";

    private ColituApiClient() { }

    public async Task<ColituServersResponse> GetServersAsync()
    {
        var subscription = ColituAuthService.Instance.CurrentSubscription;
        var response = await ColituAuthService.Instance.GetAuthorizedJsonAsync<ColituServerListDto>("/servers") ?? new();
        var servers = (response.Servers ?? [])
            .Where(server => !string.IsNullOrWhiteSpace(server.Id))
            .Select(MapServer)
            .ToList();
        return new ColituServersResponse
        {
            ActiveSubscription = subscription?.Active ?? servers.Count > 0,
            PremiumAllowed = true,
            Unlimited = subscription?.Unlimited ?? false,
            Tier = subscription?.Tier ?? "premium",
            Servers = servers,
            Multihop = MapMultihop(response.Multihop),
            ClientCountry = response.ClientCountry?.Trim().ToUpperInvariant() is { Length: 2 } country ? country : null,
            ClientNetwork = response.ClientNetwork?.Trim() is { Length: > 0 and <= 64 } network ? network : null,
            NetworkToken = response.NetworkToken?.Trim() is { Length: > 0 and <= 512 } token ? token : null,
            NetworkHintsBlocked = (response.NetworkHints?.Blocked ?? [])
                .Where(protocol => !string.IsNullOrWhiteSpace(protocol))
                .Select(protocol => protocol.Trim().ToLowerInvariant())
                .Distinct()
                .Take(8)
                .ToList()
        };
    }

    /// <summary>The multihop (double VPN) routes only: <c>GET /multihop/servers</c>.</summary>
    public async Task<List<ColituVpnServer>> GetMultihopServersAsync()
    {
        var response = await ColituAuthService.Instance.GetAuthorizedJsonAsync<ColituServerListDto>("/multihop/servers") ?? new();
        return MapMultihop(response.Servers);
    }

    internal static List<ColituVpnServer> MapMultihop(IEnumerable<ColituServerDto>? routes)
    {
        return (routes ?? [])
            .Where(route => !string.IsNullOrWhiteSpace(route.Id) && route.Multihop != false && route.Entry != null && route.Exit != null)
            .Select(MapRoute)
            .ToList();
    }

    public async Task<ColituStatsResponse> GetStatsAsync()
    {
        var usage = await ColituAuthService.Instance.GetAuthorizedJsonAsync<ColituUsageDto>("/me/usage") ?? new();
        var subscription = ColituAuthService.Instance.CurrentSubscription;
        var period = new ColituUsageDay
        {
            Date = usage.PeriodStart,
            UsedBytes = usage.UsedBytes
        };
        return new ColituStatsResponse
        {
            Ok = true,
            Subscription = subscription,
            Unlimited = usage.LimitBytes == null,
            Stats = new ColituUsageStats
            {
                TotalUsedBytes = usage.UsedBytes,
                Today = period,
                Days = [period]
            }
        };
    }

    /// <summary>The panel already orders eligible servers by recommendation.</summary>
    public async Task<ColituBestServerResponse?> GetBestServerAsync()
    {
        var servers = await GetServersAsync();
        var best = servers.Servers.FirstOrDefault(server => server.Available);
        return best == null ? null : new ColituBestServerResponse { Ok = true, Server = best };
    }

    /// <summary>
    /// Stores the chosen location as this device's preferred node; an empty id
    /// lets the panel pick the recommended node ("best server").
    /// </summary>
    public async Task SetPreferredServerAsync(string? nodeId, CancellationToken token = default)
    {
        await ColituAuthService.Instance.PutAuthorizedJsonAsync<JsonElement>("/me/preferences", new
        {
            preferred_region = "",
            preferred_country = "",
            preferred_protocol = "auto",
            preferred_node_id = nodeId ?? ""
        }, token);
    }

    /// <summary>
    /// Fetches the device-bound configuration envelope and converts every
    /// candidate transport into a share link that v2rayN's importer understands.
    /// <paramref name="node"/> asks for that node for this request only (automatic mode picks it
    /// itself); without it the panel chooses and never picks a node in <paramref name="exclude"/>.
    /// </summary>
    public async Task<ColituVpnConfigResponse?> GetConfigAsync(ColituVpnServer? server, CancellationToken token = default, string? node = null, IEnumerable<string>? exclude = null)
    {
        // A multihop route has its own config endpoint; the envelope is the same as a node's.
        var multihop = server is { IsMultihop: true, Id.Length: > 0 };
        var path = multihop ? ConfigPathForRoute(server!.Id!) : ConfigPath(node, exclude);
        var envelope = await ColituAuthService.Instance.GetAuthorizedJsonAsync<ColituConfigEnvelopeDto>(path, token);
        if (envelope?.Profile == null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        if (envelope.OfflineGraceUntil is { } grace && grace <= now)
        {
            throw new InvalidOperationException("Connection settings have expired. Please try again.");
        }

        // The route id stays the identity of a multihop choice (list marker, config cache).
        var nodeId = multihop ? server!.Id : envelope.Server?.Id ?? server?.Id;
        var candidates = new List<ColituConfigCandidate>();
        var pinned = await PinServerAddressesAsync(new[] { envelope.Profile }.Concat((envelope.Candidates ?? []).Select(item => item.Profile)), token);
        void Add(string? protocol, ColituConfigProfileDto? profile)
        {
            if (profile == null || !string.Equals(profile.Format, MobileProfileFormat, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            var host = ColituShareLinkBuilder.HostOf(profile.Payload);
            var link = ColituShareLinkBuilder.Build(profile.Payload, $"Colitu {nodeId}", host != null && pinned.TryGetValue(host, out var address) ? address : null);
            if (link == null || candidates.Any(item => item.ShareLink == link))
            {
                return;
            }
            candidates.Add(new ColituConfigCandidate
            {
                Protocol = protocol ?? ColituShareLinkBuilder.ProtocolOf(profile.Payload) ?? "",
                ShareLink = link
            });
        }

        Add(null, envelope.Profile);
        foreach (var candidate in envelope.Candidates ?? [])
        {
            Add(candidate.Protocol, candidate.Profile);
        }

        if (candidates.Count == 0)
        {
            throw new InvalidOperationException("This location does not offer a transport supported by the Windows app.");
        }

        var selected = envelope.Server == null ? server : new ColituVpnServer
        {
            Id = nodeId,
            LocationId = nodeId,
            Name = envelope.Server.Name,
            DisplayName = envelope.Server.Name,
            CountryCode = envelope.Server.Country,
            Country = CountryName(envelope.Server.Country)
        };
        if (selected != null && envelope.Server is { Multihop: true } route)
        {
            ApplyRoute(selected, route.RouteSlug, route.Entry, route.Exit);
        }

        return new ColituVpnConfigResponse
        {
            ServerId = nodeId,
            Server = selected,
            ConfigType = MobileProfileFormat,
            ProtocolType = candidates[0].Protocol,
            RawConfig = string.Join(Environment.NewLine, candidates.Select(item => item.ShareLink)),
            Candidates = candidates,
            Revision = envelope.Revision,
            ExpiresAt = envelope.ExpiresAt?.ToString("O", CultureInfo.InvariantCulture),
            OfflineGraceUntil = envelope.OfflineGraceUntil
        };
    }

    /// <summary>
    /// Resolves each server host name once, ahead of the core. The core would
    /// otherwise ask the system resolver, which another VPN client in fake-IP
    /// mode answers with a placeholder that leads nowhere on the physical adapter.
    /// </summary>
    private static async Task<Dictionary<string, string>> PinServerAddressesAsync(IEnumerable<ColituConfigProfileDto?> profiles, CancellationToken token)
    {
        var pinned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Only hosts a share link would accept are resolved (and logged).
        foreach (var host in profiles.Select(profile => profile == null ? null : ColituShareLinkBuilder.HostOf(profile.Payload)).Where(ColituShareLinkBuilder.IsPlainHost).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (System.Net.IPAddress.TryParse(host, out _))
            {
                continue;
            }
            var address = await ColituNetwork.ResolveServerAsync(host!, token);
            if (address != null)
            {
                pinned[host!] = address.ToString();
                Logging.SaveLog($"ColituApiClient | Server {host} resolved to {address}");
            }
            else
            {
                Logging.SaveLog($"ColituApiClient | Server {host} could not be resolved ahead of the core; the core will resolve it");
            }
        }
        return pinned;
    }

    /// <summary>Best-effort report of which transports worked for a node.</summary>
    /// <param name="networkToken">The panel's opaque token of the network the attempt was made on: the
    /// panel counts the results for that network anonymously (network hints). Null without one.</param>
    public async Task ReportProtocolObservationsAsync(string? nodeId, IEnumerable<ColituProtocolObservation> observations, string? networkToken = null)
    {
        // The panel rejects the whole batch on a repeated protocol, more than
        // eight entries or a latency outside 0..60000 ms; keep the last result.
        var list = observations
            .Where(item => item.Protocol.IsNotEmpty() && item.Protocol != "auto")
            .GroupBy(item => item.Protocol, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .Take(8)
            .Select(item => new
            {
                protocol = item.Protocol.ToLowerInvariant(),
                reachable = item.Reachable,
                latency_ms = item.Reachable && item.LatencyMs is { } latency ? Math.Clamp(latency, 0, 60000) : (int?)null
            })
            .ToList();
        if (nodeId.IsNullOrEmpty() || list.Count == 0)
        {
            return;
        }

        try
        {
            object body = string.IsNullOrEmpty(networkToken)
                ? new { node_id = nodeId, observations = list }
                : new { node_id = nodeId, observations = list, network_token = networkToken };
            await ColituAuthService.Instance.PostAuthorizedAsync("/client/protocol-observations", body);
        }
        catch
        {
            // Reporting never blocks a connection.
        }
    }

    // The panel derives session accounting from node traffic reports, so the
    // client does not report connect/disconnect events or stability logs.
    public Task DisconnectAsync(string? serverId, double connectedSeconds) => Task.CompletedTask;

    public Task SendVpnEventAsync(string eventName, string status, string? serverId, string? message, double connectedSeconds) => Task.CompletedTask;

    public Task SendStabilityLogAsync(string eventName, string state, ColituVpnServer? server, string? reason, string? error) => Task.CompletedTask;

    /// <summary><c>/config?protocol=auto</c>, plus <c>node=</c> or <c>exclude=</c> (at most 10 ids).</summary>
    internal static string ConfigPath(string? node, IEnumerable<string>? exclude)
    {
        var path = "/config?protocol=auto";
        if (!string.IsNullOrWhiteSpace(node))
        {
            return $"{path}&node={Uri.EscapeDataString(node.Trim())}";
        }
        var ids = (exclude ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(ColituAdaptiveConnect.MaxExclude)
            .ToList();
        return ids.Count == 0 ? path : $"{path}&exclude={string.Join(",", ids.Select(Uri.EscapeDataString))}";
    }

    internal static string ConfigPathForRoute(string routeId) => $"/multihop/routes/{Uri.EscapeDataString(routeId)}/config";

    /// <summary>
    /// A rotation or multihop connection runs on VLESS only: the other transports cannot be carried
    /// through the mesh. Returns the configuration with just the VLESS candidates, or null when none is left.
    /// </summary>
    internal static ColituVpnConfigResponse? RestrictToVless(ColituVpnConfigResponse config)
    {
        var vless = config.Candidates.Where(candidate => IsVless(candidate.Protocol)).ToList();
        if (vless.Count == 0)
        {
            return null;
        }
        if (vless.Count == config.Candidates.Count)
        {
            return config;
        }
        return new ColituVpnConfigResponse
        {
            ServerId = config.ServerId,
            Server = config.Server,
            ConfigType = config.ConfigType,
            ProtocolType = vless[0].Protocol,
            RawConfig = string.Join(Environment.NewLine, vless.Select(item => item.ShareLink)),
            Candidates = vless,
            Revision = config.Revision,
            ExpiresAt = config.ExpiresAt,
            OfflineGraceUntil = config.OfflineGraceUntil,
            Unlimited = config.Unlimited
        };
    }

    internal static bool IsVless(string? protocol) =>
        string.Equals(protocol, "vless-reality", StringComparison.OrdinalIgnoreCase)
        || string.Equals(protocol, "vless-xhttp", StringComparison.OrdinalIgnoreCase);

    // ── Rotating exit IP ────────────────────────────────────────────────────

    public async Task<ColituRotationPreference> GetRotationAsync(CancellationToken token = default)
    {
        var response = await ColituAuthService.Instance.GetAuthorizedJsonAsync<ColituRotationEnvelopeDto>("/me/rotation", token);
        return ColituRotation.Map(response?.Rotation);
    }

    /// <summary>Saves the preference; the panel answers 400 INVALID_PREFERENCE for a country set it cannot serve.</summary>
    public async Task<ColituRotationPreference> SetRotationAsync(int intervalSeconds, IEnumerable<string> countries, CancellationToken token = default)
    {
        var response = await ColituAuthService.Instance.PutAuthorizedJsonAsync<ColituRotationEnvelopeDto>("/me/rotation", new
        {
            interval_seconds = intervalSeconds,
            countries = ColituRotation.NormalizeCountries(countries)
        }, token);
        return ColituRotation.Map(response?.Rotation);
    }

    /// <summary>Where the rotation is for the node this device is connected to.</summary>
    public async Task<ColituRotationStatus?> GetRotationStatusAsync(string nodeId, CancellationToken token = default)
    {
        var response = await ColituAuthService.Instance.GetAuthorizedJsonAsync<ColituRotationStatusEnvelopeDto>(
            $"/me/rotation/status?node_id={Uri.EscapeDataString(nodeId)}", token);
        return ColituRotation.MapStatus(response?.Status);
    }

    private static ColituVpnServer MapRoute(ColituServerDto dto)
    {
        var server = MapServer(dto);
        // The route's own country/city describe the exit; its latency host is the entry node.
        ApplyRoute(server, dto.RouteSlug, dto.Entry, dto.Exit);
        server.Categories = [];
        return server;
    }

    private static void ApplyRoute(ColituVpnServer server, string? slug, ColituRouteEndpointDto? entry, ColituRouteEndpointDto? exit)
    {
        server.IsMultihop = true;
        server.RouteSlug = slug;
        server.Entry = ColituRotation.MapEndpoint(entry);
        server.Exit = ColituRotation.MapEndpoint(exit);
        if (server.Exit?.Country is { Length: 2 } exitCountry)
        {
            server.CountryCode = exitCountry;
            server.Country = CountryName(exitCountry);
        }
        if (server.Exit?.City is { Length: > 0 } exitCity)
        {
            server.City = exitCity;
        }
    }

    internal static ColituVpnServer MapServer(ColituServerDto dto)
    {
        var code = (dto.Country ?? "").Trim().ToUpperInvariant();
        var server = new ColituVpnServer
        {
            Id = dto.Id,
            LocationId = dto.Id,
            Name = FirstNonEmpty(dto.Name, CountryName(code), "Colitu Server"),
            DisplayName = FirstNonEmpty(dto.Name, CountryName(code)),
            City = dto.City,
            Region = dto.Region,
            CountryCode = code.Length == 2 ? code : null,
            Country = CountryName(code),
            Available = string.Equals(dto.Status, "online", StringComparison.OrdinalIgnoreCase),
            Online = string.Equals(dto.Status, "online", StringComparison.OrdinalIgnoreCase),
            Premium = false,
            Free = true,
            Locked = false,
            Host = dto.LatencyHost,
            Port = dto.LatencyPort,
            Categories = (dto.Categories ?? []).Select(value => value.Trim().ToLowerInvariant()).Where(value => value.Length > 0).Distinct().ToList(),
            Services = (dto.Services ?? []).Select(value => (value ?? "").Trim().ToLowerInvariant()).Where(value => value.Length > 0).Distinct().ToList(),
            Load = dto.Load?.ToLowerInvariant() switch
            {
                "low" => 25,
                "medium" => 55,
                "high" => 85,
                _ => null
            }
        };
        server.ApplyPing(null, true);
        return server;
    }

    internal static string? CountryName(string? code)
    {
        code = (code ?? "").Trim().ToUpperInvariant();
        if (code == "UK") code = "GB";
        if (code.Length != 2)
        {
            return null;
        }

        try
        {
            return new RegionInfo(code).EnglishName;
        }
        catch (ArgumentException)
        {
            return code;
        }
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
    }
}

public sealed class ColituConfigCandidate
{
    public string Protocol { get; set; } = "";
    public string ShareLink { get; set; } = "";
}

public sealed class ColituProtocolObservation
{
    public string Protocol { get; set; } = "";
    public bool Reachable { get; set; }
    public int? LatencyMs { get; set; }
}

/// <summary>One end of a multihop route, or the exit a rotation currently uses.</summary>
public sealed class ColituRouteEndpoint
{
    public string? NodeId { get; set; }
    public string? Name { get; set; }
    /// <summary>Upper-case ISO country code; null when the panel sent none.</summary>
    public string? Country { get; set; }
    public string? City { get; set; }

    /// <summary>City, else node name, else country code: how the end is named in the UI.</summary>
    public string Label => !string.IsNullOrWhiteSpace(City) ? City! : !string.IsNullOrWhiteSpace(Name) ? Name! : Country ?? "";
}

public sealed class ColituRotationCountry
{
    public string Country { get; set; } = "";
    /// <summary>Part of the set used when the user picks no countries (Russia is not).</summary>
    public bool InDefault { get; set; }
    /// <summary>Exit nodes the panel has in this country.</summary>
    public int Exits { get; set; }
}

/// <summary>The account's rotating-exit-IP preference (<c>/me/rotation</c>).</summary>
public sealed class ColituRotationPreference
{
    /// <summary>0 = off.</summary>
    public int IntervalSeconds { get; set; }
    /// <summary>Chosen countries; empty = the default set.</summary>
    public List<string> Countries { get; set; } = [];
    public List<int> Intervals { get; set; } = [];
    public List<ColituRotationCountry> AvailableCountries { get; set; } = [];
    public List<string> Protocols { get; set; } = [];
    public bool ChangesExitCountry { get; set; }
    public bool Active => IntervalSeconds > 0;
}

/// <summary>Where the rotation of the connected node is (<c>/me/rotation/status</c>).</summary>
public sealed class ColituRotationStatus
{
    public bool Active { get; set; }
    /// <summary>off, not_in_mesh or not_enough_exits while inactive.</summary>
    public string? Reason { get; set; }
    public int IntervalSeconds { get; set; }
    public ColituRouteEndpoint? Entry { get; set; }
    public ColituRouteEndpoint? CurrentExit { get; set; }
    public ColituRouteEndpoint? NextExit { get; set; }
    public DateTimeOffset? WindowStartedAt { get; set; }
    public DateTimeOffset? NextChangeAt { get; set; }
    public List<string> Protocols { get; set; } = [];
}

/// <summary>Validation and timing rules of the rotating exit IP.</summary>
public static class ColituRotation
{
    /// <summary>Off, 5, 10 and 30 minutes.</summary>
    public static readonly int[] IntervalChoices = [0, 300, 600, 1800];

    /// <summary>The status is never asked for more often than this.</summary>
    public const int MinStatusPollSeconds = 60;

    private const int MaxStatusPollSeconds = 35 * 60;

    public static bool IsValidInterval(int seconds) => Array.IndexOf(IntervalChoices, seconds) >= 0;

    /// <summary>Upper-case two-letter codes, no duplicates, sorted; anything else is dropped.</summary>
    public static List<string> NormalizeCountries(IEnumerable<string>? countries)
    {
        return (countries ?? [])
            .Select(code => (code ?? "").Trim().ToUpperInvariant())
            .Select(code => code == "UK" ? "GB" : code)
            .Where(code => code.Length == 2 && code.All(ch => ch is >= 'A' and <= 'Z'))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Countries with exits that the panel includes when the user picks none.</summary>
    public static List<string> DefaultCountries(IEnumerable<ColituRotationCountry>? available)
    {
        return NormalizeCountries((available ?? []).Where(item => item.InDefault && item.Exits > 0).Select(item => item.Country));
    }

    /// <summary>What the checklist shows: the chosen countries, or the default set when none were chosen.</summary>
    public static List<string> SelectedCountries(ColituRotationPreference preference)
    {
        var chosen = NormalizeCountries(preference.Countries);
        return chosen.Count > 0 ? chosen : DefaultCountries(preference.AvailableCountries);
    }

    public enum Validation
    {
        Ok,
        InvalidInterval,
        /// <summary>Fewer than two of the chosen countries have exits.</summary>
        TooFewCountries
    }

    /// <summary>
    /// The panel's rule: an interval of 0/5/10/30 minutes, and either no countries (the default set,
    /// without Russia) or at least two countries that have exits.
    /// </summary>
    public static Validation Validate(int intervalSeconds, IEnumerable<string>? countries, IEnumerable<ColituRotationCountry>? available)
    {
        if (!IsValidInterval(intervalSeconds))
        {
            return Validation.InvalidInterval;
        }
        var chosen = NormalizeCountries(countries);
        if (intervalSeconds == 0 || chosen.Count == 0)
        {
            return Validation.Ok;
        }
        var withExits = (available ?? []).Where(item => item.Exits > 0).Select(item => item.Country.ToUpperInvariant()).ToHashSet();
        return chosen.Count(code => withExits.Contains(code)) >= 2 ? Validation.Ok : Validation.TooFewCountries;
    }

    /// <summary>The list to send: empty (= default set) when the checklist still equals the default set.</summary>
    public static List<string> PayloadCountries(IEnumerable<string>? selected, IEnumerable<ColituRotationCountry>? available)
    {
        var chosen = NormalizeCountries(selected);
        return chosen.SequenceEqual(DefaultCountries(available), StringComparer.Ordinal) ? [] : chosen;
    }

    /// <summary>
    /// When to ask the panel for the status again: at <paramref name="nextChangeAt"/>, but never sooner
    /// than <see cref="MinStatusPollSeconds"/> from now (and not later than the longest interval).
    /// </summary>
    public static TimeSpan NextPollDelay(DateTimeOffset? nextChangeAt, DateTimeOffset now)
    {
        var seconds = nextChangeAt is { } at ? (at - now).TotalSeconds + 1 : MinStatusPollSeconds;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, MinStatusPollSeconds, MaxStatusPollSeconds));
    }

    /// <summary>"4:07" until the next change; "0:00" once it is due.</summary>
    public static string FormatCountdown(TimeSpan remaining)
    {
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }
        return $"{(int)remaining.TotalMinutes}:{remaining.Seconds:00}";
    }

    internal static ColituRouteEndpoint? MapEndpoint(ColituRouteEndpointDto? dto)
    {
        if (dto == null)
        {
            return null;
        }
        var country = (dto.Country ?? "").Trim().ToUpperInvariant();
        return new ColituRouteEndpoint
        {
            NodeId = dto.NodeId,
            Name = dto.Name,
            Country = country.Length == 2 ? country : null,
            City = dto.City
        };
    }

    /// <summary>An endpoint object from the status; anything else (null, a bare string) is read as far as it goes.</summary>
    private static ColituRouteEndpoint? MapEndpoint(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                return MapEndpoint(element.Deserialize<ColituRouteEndpointDto>());
            case JsonValueKind.String:
                return new ColituRouteEndpoint { Name = element.GetString() };
            default:
                return null;
        }
    }

    internal static ColituRotationPreference Map(ColituRotationDto? dto)
    {
        if (dto == null)
        {
            return new ColituRotationPreference();
        }
        return new ColituRotationPreference
        {
            IntervalSeconds = IsValidInterval(dto.IntervalSeconds) ? dto.IntervalSeconds : 0,
            Countries = NormalizeCountries(dto.Countries),
            Intervals = (dto.Intervals ?? []).Where(value => value > 0).Distinct().OrderBy(value => value).ToList(),
            AvailableCountries = (dto.AvailableCountries ?? [])
                .Where(item => !string.IsNullOrWhiteSpace(item.Country))
                .Select(item => new ColituRotationCountry { Country = item.Country!.Trim().ToUpperInvariant(), InDefault = item.InDefault, Exits = item.Exits })
                .ToList(),
            Protocols = dto.Protocols ?? [],
            ChangesExitCountry = dto.ChangesExitCountry
        };
    }

    internal static ColituRotationStatus? MapStatus(ColituRotationStatusDto? dto)
    {
        if (dto == null)
        {
            return null;
        }
        return new ColituRotationStatus
        {
            Active = dto.Active,
            Reason = dto.Reason,
            IntervalSeconds = dto.IntervalSeconds,
            Entry = MapEndpoint(dto.Entry),
            CurrentExit = MapEndpoint(dto.CurrentExit),
            NextExit = MapEndpoint(dto.NextExit),
            WindowStartedAt = dto.WindowStartedAt,
            NextChangeAt = dto.NextChangeAt,
            Protocols = dto.Protocols ?? []
        };
    }
}

/// <summary>
/// Converts a panel <c>xray-mobile-v1</c> payload into a standard share link
/// (vless://, trojan://, ss://) so the retained v2rayN parser and core
/// builder produce the runtime configuration.
/// </summary>
public static class ColituShareLinkBuilder
{
    public static string? ProtocolOf(JsonElement payload)
    {
        return payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("protocol", out var value)
            ? value.GetString()
            : null;
    }

    public static string? HostOf(JsonElement payload)
    {
        return payload.ValueKind == JsonValueKind.Object ? Str(Obj(payload, "endpoint"), "host") : null;
    }

    /// <summary>
    /// An ASCII host name or an IP address without a zone id: what can go into a share link
    /// (and a log line) unescaped.
    /// </summary>
    internal static bool IsPlainHost(string? host)
    {
        return !string.IsNullOrEmpty(host)
            && host.Length <= 253
            && host.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or ':')
            && Uri.CheckHostName(host) is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6;
    }

    /// <param name="addressOverride">A resolved address to dial instead of the host name (TLS/Reality keep the name as SNI).</param>
    public static string? Build(JsonElement payload, string remarks, string? addressOverride = null)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("schema_version", out var schema)
            || schema.ValueKind != JsonValueKind.Number
            || !schema.TryGetInt32(out var schemaVersion)
            || schemaVersion != 1)
        {
            return null;
        }

        var protocol = Str(payload, "protocol");
        var endpoint = Obj(payload, "endpoint");
        var credentials = Obj(payload, "credentials");
        var transport = Obj(payload, "transport");
        var security = Obj(payload, "security");
        var host = Str(endpoint, "host");
        var port = endpoint is { } e && e.TryGetProperty("port", out var portValue) && portValue.TryGetInt32(out var parsed) ? parsed : 0;
        // The host goes into a share link unescaped: only a real host name or IP address,
        // never one carrying '?', '#', '@', '/' or a line break that could add parameters.
        if (host.IsNullOrEmpty() || port is < 1 or > 65535 || !IsPlainHost(host))
        {
            return null;
        }
        if (addressOverride != null && Uri.CheckHostName(addressOverride) is not (UriHostNameType.IPv4 or UriHostNameType.IPv6))
        {
            return null;
        }

        var network = Str(transport, "type") ?? "tcp";
        // "hysteria" is the QUIC transport of hysteria2 itself and "xhttp" the one of vless-xhttp;
        // no other protocol rides on them.
        if (network is not ("tcp" or "ws" or "grpc" or "hysteria" or "xhttp")
            || (network == "hysteria") != (protocol == "hysteria2")
            || (network == "xhttp") != (protocol == "vless-xhttp"))
        {
            return null;
        }

        var dial = addressOverride ?? host!;
        var address = dial.Contains(':') ? $"[{dial}]" : dial;
        var fragment = "#" + Uri.EscapeDataString(remarks);
        switch (protocol)
        {
            case "vless-reality":
            {
                var uuid = Str(credentials, "uuid");
                var sni = Str(security, "server_name");
                var publicKey = Str(security, "public_key");
                var shortId = Str(security, "short_id");
                if (uuid.IsNullOrEmpty() || sni.IsNullOrEmpty() || publicKey.IsNullOrEmpty() || shortId == null)
                {
                    return null;
                }
                var query = new List<string>
                {
                    "encryption=none",
                    "security=reality",
                    $"sni={Uri.EscapeDataString(sni!)}",
                    $"fp={Uri.EscapeDataString(Str(security, "fingerprint") ?? "chrome")}",
                    $"pbk={Uri.EscapeDataString(publicKey!)}",
                    $"sid={Uri.EscapeDataString(shortId)}",
                    $"type={network}"
                };
                if (network == "tcp")
                {
                    query.Insert(1, "flow=xtls-rprx-vision");
                }
                return $"vless://{Uri.EscapeDataString(uuid!)}@{address}:{port}?{string.Join("&", query)}{fragment}";
            }
            case "vless-xhttp":
            {
                // VLESS over XHTTP with Reality; no Vision flow, XHTTP carries no raw TCP stream.
                var uuid = Str(credentials, "uuid");
                var sni = Str(security, "server_name");
                var publicKey = Str(security, "public_key");
                var shortId = Str(security, "short_id");
                var path = Str(transport, "path");
                var mode = Str(transport, "mode") ?? "auto";
                if (uuid.IsNullOrEmpty() || sni.IsNullOrEmpty() || publicKey.IsNullOrEmpty() || shortId == null
                    || path.IsNullOrEmpty() || !path!.StartsWith('/') || Str(security, "type") != "reality")
                {
                    return null;
                }
                var query = new List<string>
                {
                    "encryption=none",
                    "security=reality",
                    $"sni={Uri.EscapeDataString(sni!)}",
                    $"fp={Uri.EscapeDataString(Str(security, "fingerprint") ?? "chrome")}",
                    $"pbk={Uri.EscapeDataString(publicKey!)}",
                    $"sid={Uri.EscapeDataString(shortId)}",
                    "type=xhttp",
                    $"path={Uri.EscapeDataString(path)}",
                    $"mode={Uri.EscapeDataString(mode)}"
                };
                return $"vless://{Uri.EscapeDataString(uuid!)}@{address}:{port}?{string.Join("&", query)}{fragment}";
            }
            case "trojan":
            {
                var password = Str(credentials, "password");
                var sni = Str(security, "server_name");
                if (password.IsNullOrEmpty() || sni.IsNullOrEmpty())
                {
                    return null;
                }
                return $"trojan://{Uri.EscapeDataString(password!)}@{address}:{port}?security=tls&sni={Uri.EscapeDataString(sni!)}&type={network}{fragment}";
            }
            case "hysteria2":
            {
                // QUIC-based; runs on the sing-box core. Password and SNI come from the panel, the certificate is public.
                var password = Str(credentials, "password");
                var sni = Str(security, "server_name");
                if (password.IsNullOrEmpty() || sni.IsNullOrEmpty() || network != "hysteria")
                {
                    return null;
                }
                // Port hopping: the panel sends transport.hop_ports ("20000-40000") for nodes that
                // redirect that UDP range to Hysteria; as mport= the core hops ports every 30 s.
                var hop = HopRange(Str(transport, "hop_ports"));
                var mport = hop == null ? "" : $"&mport={hop}";
                return $"hysteria2://{Uri.EscapeDataString(password!)}@{address}:{port}?sni={Uri.EscapeDataString(sni!)}&insecure=0{mport}{fragment}";
            }
            case "shadowsocks":
            {
                var method = Str(credentials, "method");
                var password = Str(credentials, "password");
                if (method.IsNullOrEmpty() || password.IsNullOrEmpty())
                {
                    return null;
                }
                var userInfo = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{method}:{password}"))
                    .TrimEnd('=').Replace('+', '-').Replace('/', '_');
                return $"ss://{userInfo}@{address}:{port}{fragment}";
            }
            default:
                return null;
        }
    }

    private static JsonElement? Obj(JsonElement? parent, string name)
    {
        return parent is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : null;
    }

    /// <summary>A well-formed "from-to" UDP port range (1-65535, from below to), else null.</summary>
    internal static string? HopRange(string? value)
    {
        var parts = value?.Trim().Split('-');
        if (parts is not { Length: 2 }
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var from)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var to)
            || from < 1 || to > 65535 || from >= to)
        {
            return null;
        }
        return $"{from}-{to}";
    }

    private static string? Str(JsonElement? parent, string name)
    {
        return parent is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}

// ── Panel wire formats (snake_case) ─────────────────────────────────────────

internal sealed class ColituServerListDto
{
    [JsonPropertyName("servers")] public List<ColituServerDto>? Servers { get; set; }
    /// <summary>Multihop routes; absent from an older panel.</summary>
    [JsonPropertyName("multihop")] public List<ColituServerDto>? Multihop { get; set; }
    /// <summary>Country of the request IP; empty when unknown or through a Colitu exit, absent from an older panel.</summary>
    [JsonPropertyName("client_country")] public string? ClientCountry { get; set; }
    /// <summary>Opaque key of the user's ISP network (only used in the network key).</summary>
    [JsonPropertyName("client_network")] public string? ClientNetwork { get; set; }
    /// <summary>Opaque token of that network (valid 48 h), sent back with protocol observations.</summary>
    [JsonPropertyName("network_token")] public string? NetworkToken { get; set; }
    /// <summary>Protocols that fail for most devices on that network; absent when none or too little data.</summary>
    [JsonPropertyName("network_hints")] public ColituNetworkHintsDto? NetworkHints { get; set; }
}

internal sealed class ColituNetworkHintsDto
{
    [JsonPropertyName("blocked")] public List<string>? Blocked { get; set; }
    /// <summary>"network" (this ISP network) or "country" (too little data for the network).</summary>
    [JsonPropertyName("scope")] public string? Scope { get; set; }
    [JsonPropertyName("updated_at")] public string? UpdatedAt { get; set; }
}

/// <summary>One end of a multihop route (or the exit a rotation currently uses).</summary>
internal sealed class ColituRouteEndpointDto
{
    [JsonPropertyName("node_id")] public string? NodeId { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("country")] public string? Country { get; set; }
    [JsonPropertyName("city")] public string? City { get; set; }
}

internal sealed class ColituServerDto
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("country")] public string? Country { get; set; }
    [JsonPropertyName("city")] public string? City { get; set; }
    [JsonPropertyName("region")] public string? Region { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("load")] public string? Load { get; set; }
    [JsonPropertyName("protocols")] public List<string>? Protocols { get; set; }
    [JsonPropertyName("categories")] public List<string>? Categories { get; set; }
    [JsonPropertyName("services")] public List<string>? Services { get; set; }
    [JsonPropertyName("latency_host")] public string? LatencyHost { get; set; }
    [JsonPropertyName("latency_port")] public int? LatencyPort { get; set; }
    // Multihop routes only.
    [JsonPropertyName("multihop")] public bool? Multihop { get; set; }
    [JsonPropertyName("route_slug")] public string? RouteSlug { get; set; }
    [JsonPropertyName("entry")] public ColituRouteEndpointDto? Entry { get; set; }
    [JsonPropertyName("exit")] public ColituRouteEndpointDto? Exit { get; set; }
    [JsonPropertyName("latency_note")] public string? LatencyNote { get; set; }
}

internal sealed class ColituUsageDto
{
    [JsonPropertyName("upload_bytes")] public long UploadBytes { get; set; }
    [JsonPropertyName("download_bytes")] public long DownloadBytes { get; set; }
    [JsonPropertyName("used_bytes")] public long UsedBytes { get; set; }
    [JsonPropertyName("limit_bytes")] public long? LimitBytes { get; set; }
    [JsonPropertyName("remaining_bytes")] public long? RemainingBytes { get; set; }
    [JsonPropertyName("period_start")] public string? PeriodStart { get; set; }
    [JsonPropertyName("period_end")] public string? PeriodEnd { get; set; }
}

internal sealed class ColituConfigEnvelopeDto
{
    [JsonPropertyName("revision")] public ulong Revision { get; set; }
    [JsonPropertyName("expires_at")] public DateTimeOffset? ExpiresAt { get; set; }
    [JsonPropertyName("offline_grace_until")] public DateTimeOffset? OfflineGraceUntil { get; set; }
    [JsonPropertyName("server")] public ColituConfigServerDto? Server { get; set; }
    [JsonPropertyName("profile")] public ColituConfigProfileDto? Profile { get; set; }
    [JsonPropertyName("candidates")] public List<ColituConfigCandidateDto>? Candidates { get; set; }
}

internal sealed class ColituConfigServerDto
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("country")] public string? Country { get; set; }
    [JsonPropertyName("region")] public string? Region { get; set; }
    [JsonPropertyName("multihop")] public bool? Multihop { get; set; }
    [JsonPropertyName("route_slug")] public string? RouteSlug { get; set; }
    [JsonPropertyName("entry")] public ColituRouteEndpointDto? Entry { get; set; }
    [JsonPropertyName("exit")] public ColituRouteEndpointDto? Exit { get; set; }
}

internal sealed class ColituConfigProfileDto
{
    [JsonPropertyName("format")] public string? Format { get; set; }
    [JsonPropertyName("payload")] public JsonElement Payload { get; set; }
}

internal sealed class ColituConfigCandidateDto
{
    [JsonPropertyName("protocol")] public string? Protocol { get; set; }
    [JsonPropertyName("profile")] public ColituConfigProfileDto? Profile { get; set; }
}

internal sealed class ColituRotationEnvelopeDto
{
    [JsonPropertyName("rotation")] public ColituRotationDto? Rotation { get; set; }
}

internal sealed class ColituRotationDto
{
    [JsonPropertyName("interval_seconds")] public int IntervalSeconds { get; set; }
    [JsonPropertyName("countries")] public List<string>? Countries { get; set; }
    [JsonPropertyName("intervals")] public List<int>? Intervals { get; set; }
    [JsonPropertyName("available_countries")] public List<ColituRotationCountryDto>? AvailableCountries { get; set; }
    [JsonPropertyName("protocols")] public List<string>? Protocols { get; set; }
    [JsonPropertyName("changes_exit_country")] public bool ChangesExitCountry { get; set; }
}

internal sealed class ColituRotationCountryDto
{
    [JsonPropertyName("country")] public string? Country { get; set; }
    [JsonPropertyName("in_default")] public bool InDefault { get; set; }
    [JsonPropertyName("exits")] public int Exits { get; set; }
}

internal sealed class ColituRotationStatusEnvelopeDto
{
    [JsonPropertyName("status")] public ColituRotationStatusDto? Status { get; set; }
}

internal sealed class ColituRotationStatusDto
{
    [JsonPropertyName("active")] public bool Active { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("interval_seconds")] public int IntervalSeconds { get; set; }
    // The ends are objects today; JsonElement keeps an unexpected shape from failing the whole status.
    [JsonPropertyName("entry")] public JsonElement Entry { get; set; }
    [JsonPropertyName("current_exit")] public JsonElement CurrentExit { get; set; }
    [JsonPropertyName("next_exit")] public JsonElement NextExit { get; set; }
    [JsonPropertyName("window_started_at")] public DateTimeOffset? WindowStartedAt { get; set; }
    [JsonPropertyName("next_change_at")] public DateTimeOffset? NextChangeAt { get; set; }
    [JsonPropertyName("protocols")] public List<string>? Protocols { get; set; }
}
