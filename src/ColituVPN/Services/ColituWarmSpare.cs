using System.Text.Json;
using System.Text.Json.Nodes;

namespace v2rayN.Services;

/// <summary>
/// Warm spare: a second, already configured path inside the running core. When the primary stops
/// answering, the core itself moves new traffic to the spare within seconds (no reconnect, also
/// while the app is minimized); the app's own stall watch and Adaptive Connect only act when both
/// are dead. Pure logic: picking the spare transport and adding it to a generated core config.
/// <para>
/// Xray (TCP transports): the primary keeps the tag <c>proxy</c>, the spare is <c>warm-spare</c>
/// (Xray balancer and observatory selectors match tag PREFIXES, so "proxy-spare" would be balanced
/// with the primary instead of waiting behind it). Every rule that sent traffic to <c>proxy</c> goes
/// to the balancer <c>proxy-auto</c>: selector [proxy], fallbackTag warm-spare, strategy random
/// (which keeps only the outbounds the observatory saw alive). The plain <c>observatory</c> probes
/// the primary only, every 3 s with its fixed 5 s timeout: a dead primary is noticed within about
/// 8 s. The <c>burstObservatory</c> is not used: it enforces a 10 s minimum interval and needs every
/// kept sample to fail.
/// </para>
/// <para>
/// Both cores also get a loopback SOCKS inbound <c>colitu-verify</c> (random port and credentials)
/// whose first rule goes straight to the primary outbound: the connect check runs through it, so a
/// dead primary fails the check even while the spare keeps the user online.
/// </para>
/// <para>
/// sing-box (Hysteria2, or every transport while apps are split): the primary becomes
/// <c>proxy-main</c>, the spare <c>warm-spare</c> (the same name as in Xray, for the logs), and a
/// <c>urltest</c> group takes the tag <c>proxy</c>, so every route rule, the final outbound and DNS
/// detours follow it unchanged. A high tolerance keeps it on the primary until the primary fails a test.
/// </para>
/// </summary>
public static class ColituWarmSpare
{
    public const string XraySpareTag = "warm-spare";
    public const string XrayBalancerTag = "proxy-auto";
    public const string SingboxPrimaryTag = "proxy-main";
    public const string SingboxSpareTag = XraySpareTag;
    public const string ProxyTag = "proxy";
    /// <summary>Plain HTTP inside the tunnel: no extra TLS handshake per probe.</summary>
    public const string ProbeUrl = "http://www.gstatic.com/generate_204";
    /// <summary>Xray observatory interval (desktop); its probe timeout is a fixed 5 s, so detection stays within about 8 s.</summary>
    public static readonly TimeSpan XrayProbeInterval = TimeSpan.FromSeconds(3);
    public const string VerifyInboundTag = "colitu-verify";
    /// <summary>Second check inbound, to the spare alone (exists only while a spare is attached).</summary>
    public const string SpareVerifyInboundTag = "colitu-verify-spare";
    /// <summary>
    /// Check inbound to the tunnel as a whole (the primary, or the primary/spare group), in every
    /// config: the watcher, resume and restart checks run through it, so no routing rule (split
    /// tunnelling's "everything else direct", a site list, regional direct rules) can send a check
    /// around a dead tunnel and call it working.
    /// </summary>
    public const string CheckInboundTag = "colitu-check";
    /// <summary>Xray sockopt on TCP outbounds with a spare attached: a dead TCP path fails after 10 s, not minutes.</summary>
    public const int TcpUserTimeoutMs = 10000;
    /// <summary>sing-box urltest interval (its test timeout is a fixed 15 s; a failed dial switches at once).</summary>
    public static readonly TimeSpan SingboxProbeInterval = TimeSpan.FromSeconds(5);
    public const int SingboxTolerance = 3000;

    private static readonly string[] TcpOrder = ["vless-reality", "vless-xhttp", "trojan", "shadowsocks"];

    public static bool IsUdp(string? protocol) => protocol == "hysteria2";

    /// <summary>
    /// Whether <paramref name="protocol"/> runs in the core in use. Hysteria2 runs on sing-box only
    /// (the app never gives it to Xray); sing-box has no XHTTP.
    /// </summary>
    public static bool RunsOn(string? protocol, bool singBox) => singBox
        ? protocol is "hysteria2" or "vless-reality" or "trojan" or "shadowsocks"
        : protocol is "vless-reality" or "vless-xhttp" or "trojan" or "shadowsocks";

    /// <summary>
    /// The spare transport for <paramref name="primary"/>: the other family first (Hysteria2 behind
    /// a TCP primary; Reality, XHTTP, Trojan, Shadowsocks behind Hysteria2), then the same family.
    /// On the same server the primary's own transport is never the spare. Only transports offered,
    /// runnable in the core in use and not stalled on this network qualify; null when none does.
    /// A transport the network hints call blocked (<paramref name="hinted"/>) is the spare only when
    /// no other one qualifies.
    /// </summary>
    public static string? PickTransport(string primary, IEnumerable<string> offered, bool singBox, Func<string, bool> stalled, bool sameServer, Func<string, bool>? hinted = null)
    {
        var available = offered.Where(item => !string.IsNullOrEmpty(item)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> order = IsUdp(primary)
            ? [.. TcpOrder, "hysteria2"]
            : ["hysteria2", .. TcpOrder];
        var candidates = order
            .Where(protocol => !sameServer || !string.Equals(protocol, primary, StringComparison.OrdinalIgnoreCase))
            .Where(available.Contains)
            .Where(protocol => RunsOn(protocol, singBox))
            .Where(protocol => !stalled(protocol))
            .ToList();
        return candidates.FirstOrDefault(protocol => hinted?.Invoke(protocol) != true) ?? candidates.FirstOrDefault();
    }

    /// <summary>
    /// Automatic mode, the next ranked server: a transport "proven" on this network (carried traffic
    /// in the last 24 h on any server), the primary's own transport first; only if nothing is proven
    /// the other family (UDP/TCP), then the primary's own transport, then the rest of its family.
    /// Stalled ones and ones the core in use cannot run never; hinted-blocked ones only when nothing
    /// else is left.
    /// </summary>
    public static ColituSpareChoice? ChooseOnNextServer(string primary, IEnumerable<string> offered, bool singBox,
        Func<string, bool> proven, Func<string, bool> stalled, Func<string, bool>? hinted = null)
    {
        var usable = Usable(offered, singBox, stalled, protocol => false);
        IEnumerable<string> order = [primary, .. Order(primary).Where(protocol => !string.Equals(protocol, primary, StringComparison.OrdinalIgnoreCase))];
        var clean = order.Where(usable.Contains).Where(protocol => hinted?.Invoke(protocol) != true).ToList();
        if (clean.FirstOrDefault(proven) is { } provenProtocol)
        {
            return new(provenProtocol, "proven");
        }
        if (clean.FirstOrDefault(protocol => IsUdp(protocol) != IsUdp(primary)) is { } otherFamily)
        {
            return new(otherFamily, "other-family");
        }
        if (clean.FirstOrDefault(protocol => string.Equals(protocol, primary, StringComparison.OrdinalIgnoreCase)) is { } same)
        {
            return new(same, "same-transport");
        }
        if (clean.FirstOrDefault() is { } sameFamily)
        {
            return new(sameFamily, "same-family");
        }
        return order.FirstOrDefault(usable.Contains) is { } hintedOnly ? new(hintedOnly, "hinted-fallback") : null;
    }

    /// <summary>
    /// A chosen server (or no next server): the same server, the other family, skipping stalled
    /// ones, Shadowsocks last; never the primary's own transport. Where the core in use cannot run
    /// the other family (Xray has no Hysteria2 on desktop) the rest of the primary's family follows.
    /// </summary>
    public static ColituSpareChoice? ChooseOnSameServer(string primary, IEnumerable<string> offered, bool singBox,
        Func<string, bool> stalled, Func<string, bool>? hinted = null, Func<string, bool>? excluded = null)
    {
        var usable = Usable(offered, singBox, stalled, protocol => string.Equals(protocol, primary, StringComparison.OrdinalIgnoreCase) || excluded?.Invoke(protocol) == true);
        var order = Order(primary).Where(usable.Contains).ToList();
        var clean = order.Where(protocol => hinted?.Invoke(protocol) != true).ToList();
        if (clean.FirstOrDefault(protocol => IsUdp(protocol) != IsUdp(primary)) is { } otherFamily)
        {
            return new(otherFamily, "other-family");
        }
        if (clean.FirstOrDefault() is { } sameFamily)
        {
            return new(sameFamily, "same-family");
        }
        return order.FirstOrDefault() is { } hintedOnly ? new(hintedOnly, "hinted-fallback") : null;
    }

    /// <summary>The other family first, then the primary's family; Shadowsocks last within TCP.</summary>
    private static IEnumerable<string> Order(string primary) => IsUdp(primary) ? [.. TcpOrder, "hysteria2"] : ["hysteria2", .. TcpOrder];

    private static HashSet<string> Usable(IEnumerable<string> offered, bool singBox, Func<string, bool> stalled, Func<string, bool> excluded) =>
        offered.Where(protocol => !string.IsNullOrEmpty(protocol) && RunsOn(protocol, singBox) && !stalled(protocol) && !excluded(protocol))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The outbound with <paramref name="tag"/> of a generated config (a copy); null when there is none.</summary>
    public static JsonObject? FindOutbound(string config, string tag = ProxyTag)
    {
        try
        {
            return JsonNode.Parse(config)?["outbounds"] is JsonArray outbounds
                ? outbounds.OfType<JsonObject>().FirstOrDefault(item => Tag(item) == tag)?.DeepClone() as JsonObject
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Adds <paramref name="spare"/> (an Xray outbound) behind the primary <c>proxy</c> of an Xray
    /// config: balancer, observatory, every former <c>proxy</c> rule on the balancer, and a final
    /// catch-all on it (Xray sends unmatched traffic to the first outbound). Null when the config
    /// does not have the expected shape (it is then used without a spare).
    /// </summary>
    public static string? ApplyXray(string config, JsonObject spare, TimeSpan? interval = null, ColituVerifyInbound? verify = null, ColituVerifyInbound? spareVerify = null)
    {
        if (Parse(config) is not { } root || root["outbounds"] is not JsonArray outbounds
            || root["observatory"] != null || root["burstObservatory"] != null)
        {
            return null;
        }
        var objects = outbounds.OfType<JsonObject>().ToList();
        var primary = objects.FirstOrDefault(item => Tag(item) == ProxyTag);
        // Another tag starting with "proxy" would be picked by the balancer's prefix selector too.
        if (primary == null || objects.Any(item => Tag(item) is { } tag && tag != ProxyTag && (tag.StartsWith(ProxyTag, StringComparison.Ordinal) || tag == XraySpareTag)))
        {
            return null;
        }
        var spareOutbound = (JsonObject)spare.DeepClone();
        spareOutbound["tag"] = XraySpareTag;
        outbounds.Insert(outbounds.IndexOf(primary) + 1, spareOutbound);

        var routing = root["routing"] as JsonObject ?? new JsonObject();
        root["routing"] = routing;
        var rules = routing["rules"] as JsonArray ?? new JsonArray();
        routing["rules"] = rules;
        foreach (var rule in rules.OfType<JsonObject>())
        {
            if (rule["outboundTag"]?.GetValueKind() == JsonValueKind.String && rule["outboundTag"]!.GetValue<string>() == ProxyTag)
            {
                rule.Remove("outboundTag");
                rule["balancerTag"] = XrayBalancerTag;
            }
        }
        if (ReferenceEquals(outbounds[0], primary))
        {
            rules.Add(new JsonObject { ["type"] = "field", ["network"] = "tcp,udp", ["balancerTag"] = XrayBalancerTag });
        }
        var balancers = routing["balancers"] as JsonArray ?? new JsonArray();
        routing["balancers"] = balancers;
        balancers.Add(new JsonObject
        {
            ["tag"] = XrayBalancerTag,
            ["selector"] = new JsonArray(ProxyTag),
            ["fallbackTag"] = XraySpareTag,
            ["strategy"] = new JsonObject { ["type"] = "random" }
        });
        root["observatory"] = new JsonObject
        {
            // The primary only: the spare is used blind when the primary is down.
            ["subjectSelector"] = new JsonArray(ProxyTag),
            ["probeUrl"] = ProbeUrl,
            ["probeInterval"] = Seconds(interval ?? XrayProbeInterval),
            ["enableConcurrency"] = true
        };
        // A dead TCP path fails after 10 s instead of hanging for minutes (Xray runs TCP transports only here).
        foreach (var outbound in new[] { primary, spareOutbound })
        {
            var stream = outbound["streamSettings"] as JsonObject ?? new JsonObject();
            outbound["streamSettings"] = stream;
            var sockopt = stream["sockopt"] as JsonObject ?? new JsonObject();
            stream["sockopt"] = sockopt;
            sockopt["tcpUserTimeout"] = TcpUserTimeoutMs;
        }
        // The DNS module's own queries: an explicit rule to the balancer at the top (after the checks).
        var dnsTag = root["dns"]?["tag"]?.GetValueKind() == JsonValueKind.String ? root["dns"]!["tag"]!.GetValue<string>() : null;
        if (!string.IsNullOrEmpty(dnsTag))
        {
            foreach (var rule in rules.OfType<JsonObject>().Where(rule => rule["inboundTag"] is JsonArray tags && tags.Count == 1
                && tags[0]?.GetValueKind() == JsonValueKind.String && tags[0]!.GetValue<string>() == dnsTag && rule["balancerTag"] != null).ToList())
            {
                rules.Remove(rule);
            }
            rules.Insert(0, new JsonObject { ["type"] = "field", ["inboundTag"] = new JsonArray(dnsTag), ["balancerTag"] = XrayBalancerTag });
        }
        AddXrayVerify(root, rules, spareVerify, SpareVerifyInboundTag, XraySpareTag);
        // Before every other rule, to the primary itself (never the balancer).
        AddXrayVerify(root, rules, verify, VerifyInboundTag, ProxyTag);
        return root.ToJsonString(Indented);
    }

    /// <summary>
    /// Adds the <see cref="CheckInboundTag"/> inbound, its rule first: to <c>proxy</c> (sing-box: the
    /// primary, or the urltest group over primary and spare; Xray: the primary, or the balancer when a
    /// spare is attached). Null when the config has no <c>proxy</c> outbound.
    /// </summary>
    public static string? AddCheckInbound(string config, bool singBox, ColituVerifyInbound check)
    {
        if (Parse(config) is not { } root || root["outbounds"] is not JsonArray outbounds
            || !outbounds.OfType<JsonObject>().Any(item => Tag(item) == ProxyTag))
        {
            return null;
        }
        if (singBox)
        {
            AddSingboxVerify(root, check, CheckInboundTag, ProxyTag);
            return root.ToJsonString(Indented);
        }
        var routing = root["routing"] as JsonObject ?? new JsonObject();
        root["routing"] = routing;
        var rules = routing["rules"] as JsonArray ?? new JsonArray();
        routing["rules"] = rules;
        var balanced = routing["balancers"] is JsonArray balancers && balancers.OfType<JsonObject>().Any(item => Tag(item) == XrayBalancerTag);
        AddXrayVerify(root, rules, check, CheckInboundTag, balanced ? null : ProxyTag, balanced ? XrayBalancerTag : null);
        return root.ToJsonString(Indented);
    }

    private static void AddXrayVerify(JsonObject root, JsonArray rules, ColituVerifyInbound? verify, string tag, string? outbound, string? balancer = null)
    {
        if (verify == null)
        {
            return;
        }
        var inbounds = root["inbounds"] as JsonArray ?? new JsonArray();
        root["inbounds"] = inbounds;
        inbounds.Add(new JsonObject
        {
            ["tag"] = tag,
            ["listen"] = "127.0.0.1",
            ["port"] = verify.Port,
            ["protocol"] = "socks",
            ["settings"] = new JsonObject
            {
                ["auth"] = "password",
                ["accounts"] = new JsonArray(new JsonObject { ["user"] = verify.User, ["pass"] = verify.Password }),
                ["udp"] = false
            }
        });
        var rule = new JsonObject { ["type"] = "field", ["inboundTag"] = new JsonArray(tag) };
        rule[balancer != null ? "balancerTag" : "outboundTag"] = balancer ?? outbound;
        rules.Insert(0, rule);
    }

    /// <summary>
    /// Adds <paramref name="spare"/> (a sing-box outbound) behind the primary of a sing-box config:
    /// primary renamed to proxy-main, spare proxy-spare, a urltest group named proxy over both.
    /// Null when the config does not have the expected shape.
    /// </summary>
    public static string? ApplySingbox(string config, JsonObject spare, TimeSpan? interval = null, ColituVerifyInbound? verify = null, ColituVerifyInbound? spareVerify = null)
    {
        if (Parse(config) is not { } root || root["outbounds"] is not JsonArray outbounds)
        {
            return null;
        }
        var objects = outbounds.OfType<JsonObject>().ToList();
        var primary = objects.FirstOrDefault(item => Tag(item) == ProxyTag);
        if (primary == null || primary["type"]?.GetValue<string>() is "selector" or "urltest"
            || objects.Any(item => Tag(item) is SingboxPrimaryTag or SingboxSpareTag))
        {
            return null;
        }
        var index = outbounds.IndexOf(primary);
        primary["tag"] = SingboxPrimaryTag;
        var spareOutbound = (JsonObject)spare.DeepClone();
        spareOutbound["tag"] = SingboxSpareTag;
        outbounds.Insert(index, new JsonObject
        {
            ["type"] = "urltest",
            ["tag"] = ProxyTag,
            ["outbounds"] = new JsonArray(SingboxPrimaryTag, SingboxSpareTag),
            ["url"] = ProbeUrl,
            ["interval"] = Seconds(interval ?? SingboxProbeInterval),
            ["tolerance"] = SingboxTolerance,
            ["interrupt_exist_connections"] = false
        });
        outbounds.Insert(outbounds.IndexOf(primary) + 1, spareOutbound);
        // TCP outbounds notice a dead path sooner (sing-box 1.13+ dial fields; Hysteria2 is QUIC).
        foreach (var outbound in new[] { primary, spareOutbound }.Where(item => item["type"]?.GetValue<string>() is "vless" or "trojan" or "shadowsocks"))
        {
            outbound["tcp_keep_alive"] = "10s";
            outbound["tcp_keep_alive_interval"] = "5s";
        }
        AddSingboxVerify(root, spareVerify, SpareVerifyInboundTag, SingboxSpareTag);
        // Before every other rule, to the primary itself (never the urltest group).
        AddSingboxVerify(root, verify, VerifyInboundTag, SingboxPrimaryTag);
        return root.ToJsonString(Indented);
    }

    /// <summary>
    /// Every reference in an Xray config points at something that exists: each rule's outboundTag
    /// or balancerTag, each balancer's selector (prefix) and fallbackTag, the observatory's
    /// subjects; the verify inbound's rule matches that inbound only and goes to an outbound.
    /// Empty when the config is sound.
    /// </summary>
    public static List<string> XrayProblems(string config)
    {
        var problems = new List<string>();
        if (Parse(config) is not { } root || root["outbounds"] is not JsonArray outbounds)
        {
            return ["not an Xray config"];
        }
        var tags = outbounds.OfType<JsonObject>().Select(Tag).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var balancers = (root["routing"]?["balancers"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        var balancerTags = balancers.Select(Tag).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var balancer in balancers)
        {
            foreach (var prefix in Strings(balancer["selector"]))
            {
                if (!tags.Any(tag => tag.StartsWith(prefix, StringComparison.Ordinal)))
                {
                    problems.Add($"balancer {Tag(balancer)}: selector {prefix} matches no outbound");
                }
            }
            if (Str(balancer["fallbackTag"]) is { } fallback && !tags.Contains(fallback))
            {
                problems.Add($"balancer {Tag(balancer)}: fallbackTag {fallback} missing");
            }
        }
        foreach (var subject in Strings(root["observatory"]?["subjectSelector"]).Concat(Strings(root["burstObservatory"]?["subjectSelector"])))
        {
            if (!tags.Any(tag => tag.StartsWith(subject, StringComparison.Ordinal)))
            {
                problems.Add($"observatory subject {subject} matches no outbound");
            }
        }
        var rules = (root["routing"]?["rules"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        foreach (var (rule, index) in rules.Select((rule, index) => (rule, index)))
        {
            var outbound = Str(rule["outboundTag"]);
            var balancer = Str(rule["balancerTag"]);
            if (outbound == null && balancer == null)
            {
                problems.Add($"rule {index}: no target");
            }
            if (outbound != null && !tags.Contains(outbound))
            {
                problems.Add($"rule {index}: outboundTag {outbound} missing");
            }
            if (balancer != null && !balancerTags.Contains(balancer))
            {
                problems.Add($"rule {index}: balancerTag {balancer} missing");
            }
            var inbounds = Strings(rule["inboundTag"]).ToList();
            if ((inbounds.Contains(VerifyInboundTag) || inbounds.Contains(SpareVerifyInboundTag)) && (inbounds.Count != 1 || outbound == null || balancer != null))
            {
                problems.Add($"rule {index}: a verify rule must match only its inbound and go to an outbound");
            }
            if (inbounds.SequenceEqual([VerifyInboundTag]) && outbound != ProxyTag || inbounds.SequenceEqual([SpareVerifyInboundTag]) && outbound != XraySpareTag)
            {
                problems.Add($"rule {index}: a verify rule goes to the wrong outbound ({outbound})");
            }
        }
        var inboundTags = (root["inbounds"] as JsonArray)?.OfType<JsonObject>().Select(Tag).OfType<string>().ToHashSet(StringComparer.Ordinal) ?? [];
        var leading = rules.Take(2).SelectMany(rule => Strings(rule["inboundTag"])).ToHashSet(StringComparer.Ordinal);
        foreach (var tag in new[] { VerifyInboundTag, SpareVerifyInboundTag }.Where(inboundTags.Contains))
        {
            if (!leading.Contains(tag))
            {
                problems.Add($"the {tag} rule is not among the first rules");
            }
        }
        return problems;
    }

    /// <summary>
    /// The same for a sing-box config: route rules' outbounds, route.final, DNS servers' detours,
    /// group members; the verify inbound's rule matches that inbound only and goes to a real outbound.
    /// </summary>
    public static List<string> SingboxProblems(string config)
    {
        var problems = new List<string>();
        if (Parse(config) is not { } root || root["outbounds"] is not JsonArray outbounds)
        {
            return ["not a sing-box config"];
        }
        var objects = outbounds.OfType<JsonObject>().ToList();
        var endpoints = (root["endpoints"] as JsonArray)?.OfType<JsonObject>() ?? [];
        var tags = objects.Concat(endpoints).Select(Tag).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var groups = objects.Where(item => Str(item["type"]) is "selector" or "urltest").Select(Tag).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var group in objects.Where(item => Str(item["type"]) is "selector" or "urltest"))
        {
            foreach (var member in Strings(group["outbounds"]))
            {
                if (!tags.Contains(member))
                {
                    problems.Add($"group {Tag(group)}: member {member} missing");
                }
            }
        }
        foreach (var outbound in objects)
        {
            if (Str(outbound["detour"]) is { } detour && !tags.Contains(detour))
            {
                problems.Add($"outbound {Tag(outbound)}: detour {detour} missing");
            }
        }
        if (Str(root["route"]?["final"]) is { } final && !tags.Contains(final))
        {
            problems.Add($"route.final {final} missing");
        }
        var rules = (root["route"]?["rules"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        foreach (var (rule, index) in rules.Select((rule, index) => (rule, index)))
        {
            var target = Str(rule["outbound"]);
            if (target != null && !tags.Contains(target))
            {
                problems.Add($"route rule {index}: outbound {target} missing");
            }
            var inbounds = Strings(rule["inbound"]).ToList();
            if ((inbounds.Contains(VerifyInboundTag) || inbounds.Contains(SpareVerifyInboundTag)) && (inbounds.Count != 1 || target == null || groups.Contains(target) || rule.Count != 2))
            {
                problems.Add($"route rule {index}: a verify rule must match only its inbound and go to a real outbound");
            }
            if (inbounds.SequenceEqual([VerifyInboundTag]) && target != SingboxPrimaryTag || inbounds.SequenceEqual([SpareVerifyInboundTag]) && target != SingboxSpareTag)
            {
                problems.Add($"route rule {index}: a verify rule goes to the wrong outbound ({target})");
            }
        }
        foreach (var server in (root["dns"]?["servers"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            if (Str(server["detour"]) is { } detour && !tags.Contains(detour))
            {
                problems.Add($"dns server {Tag(server)}: detour {detour} missing");
            }
        }
        var inboundTags = (root["inbounds"] as JsonArray)?.OfType<JsonObject>().Select(Tag).OfType<string>().ToHashSet(StringComparer.Ordinal) ?? [];
        var leading = rules.Take(2).SelectMany(rule => Strings(rule["inbound"])).ToHashSet(StringComparer.Ordinal);
        foreach (var tag in new[] { VerifyInboundTag, SpareVerifyInboundTag }.Where(inboundTags.Contains))
        {
            if (!leading.Contains(tag))
            {
                problems.Add($"the {tag} rule is not among the first rules");
            }
        }
        return problems;
    }

    private static string? Str(JsonNode? node) => node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;

    private static IEnumerable<string> Strings(JsonNode? node) => node switch
    {
        JsonArray array => array.Select(Str).OfType<string>(),
        _ when Str(node) is { } single => [single],
        _ => []
    };

    /// <summary>A fresh loopback check inbound: a free port and random credentials.</summary>
    public static ColituVerifyInbound NewVerifyInbound()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return new ColituVerifyInbound(port,
            Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant(),
            Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant());
    }

    private static void AddSingboxVerify(JsonObject root, ColituVerifyInbound? verify, string tag, string outbound)
    {
        if (verify == null)
        {
            return;
        }
        var inbounds = root["inbounds"] as JsonArray ?? new JsonArray();
        root["inbounds"] = inbounds;
        inbounds.Add(new JsonObject
        {
            ["type"] = "socks",
            ["tag"] = tag,
            ["listen"] = "127.0.0.1",
            ["listen_port"] = verify.Port,
            ["users"] = new JsonArray(new JsonObject { ["username"] = verify.User, ["password"] = verify.Password })
        });
        var route = root["route"] as JsonObject ?? new JsonObject();
        root["route"] = route;
        var rules = route["rules"] as JsonArray ?? new JsonArray();
        route["rules"] = rules;
        rules.Insert(0, new JsonObject { ["inbound"] = new JsonArray(tag), ["outbound"] = outbound });
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static JsonObject? Parse(string config)
    {
        try
        {
            return JsonNode.Parse(config) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Tag(JsonObject outbound) =>
        outbound["tag"]?.GetValueKind() == JsonValueKind.String ? outbound["tag"]!.GetValue<string>() : null;

    private static string Seconds(TimeSpan value) => $"{Math.Max(1, (int)Math.Round(value.TotalSeconds))}s";
}

/// <summary>The loopback SOCKS inbound the connect check uses to reach the primary outbound only.</summary>
public sealed record ColituVerifyInbound(int Port, string User, string Password);

/// <summary>The spare transport picked and why (proven, other-family, same-transport, same-family, hinted-fallback).</summary>
public sealed record ColituSpareChoice(string Protocol, string Reason);
