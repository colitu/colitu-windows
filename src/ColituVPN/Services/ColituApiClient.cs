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
            Servers = servers
        };
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
    /// </summary>
    public async Task<ColituVpnConfigResponse?> GetConfigAsync(ColituVpnServer? server, CancellationToken token = default)
    {
        var envelope = await ColituAuthService.Instance.GetAuthorizedJsonAsync<ColituConfigEnvelopeDto>("/config?protocol=auto", token);
        if (envelope?.Profile == null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        if (envelope.OfflineGraceUntil is { } grace && grace <= now)
        {
            throw new InvalidOperationException("Connection settings have expired. Please try again.");
        }

        var nodeId = envelope.Server?.Id ?? server?.Id;
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
            Id = envelope.Server.Id,
            LocationId = envelope.Server.Id,
            Name = envelope.Server.Name,
            DisplayName = envelope.Server.Name,
            CountryCode = envelope.Server.Country,
            Country = CountryName(envelope.Server.Country)
        };

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
    public async Task ReportProtocolObservationsAsync(string? nodeId, IEnumerable<ColituProtocolObservation> observations)
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
            await ColituAuthService.Instance.PostAuthorizedAsync("/client/protocol-observations", new { node_id = nodeId, observations = list });
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

    private static ColituVpnServer MapServer(ColituServerDto dto)
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
                return $"hysteria2://{Uri.EscapeDataString(password!)}@{address}:{port}?sni={Uri.EscapeDataString(sni!)}&insecure=0{fragment}";
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
    [JsonPropertyName("latency_host")] public string? LatencyHost { get; set; }
    [JsonPropertyName("latency_port")] public int? LatencyPort { get; set; }
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
