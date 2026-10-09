using System.Text.Json.Nodes;
using AwesomeAssertions;
using v2rayN.Services;
using Xunit;

namespace v2rayN.Tests;

/// <summary>Network hints (protocols blocked on the ISP network) and the integrity of every generated core config.</summary>
public class ColituNetworkHintsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private const string Network = "wifi|RU-AS0001";
    private static readonly string[] Blocked = ["hysteria2"];

    [Fact]
    public void HintedBlockedProtocol_GoesLast_UnlessItWorkedOnThisNetworkInTheLastDay()
    {
        var memory = new ColituAdaptiveMemory();
        ColituAdaptiveConnect.HintSaysBlocked(Blocked, "hysteria2", memory, Network, Now).Should().BeTrue();
        ColituAdaptiveConnect.HintSaysBlocked(Blocked, "vless-reality", memory, Network, Now).Should().BeFalse();
        ColituAdaptiveConnect.HintSaysBlocked(null, "hysteria2", memory, Network, Now).Should().BeFalse();

        // Local experience beats the hint: it worked here (any server) within 24 h.
        memory.RememberGoodTransport(Network, "node-b", "hysteria2", Now.AddHours(-3));
        ColituAdaptiveConnect.HintSaysBlocked(Blocked, "hysteria2", memory, Network, Now).Should().BeFalse();
        ColituAdaptiveConnect.HintSaysBlocked(Blocked, "hysteria2", memory, "cellular|RU-AS0002", Now).Should().BeTrue();
        ColituAdaptiveConnect.HintSaysBlocked(Blocked, "hysteria2", memory, Network, Now.AddHours(22)).Should().BeTrue();
    }

    [Fact]
    public void TransportOrder_PutsTheHintedProtocolBehindEveryOther()
    {
        var order = new[] { ("hysteria2", -1, false, true), ("vless-reality", 80, false, false), ("trojan", 40, true, false), ("shadowsocks", -1, false, false) }
            .OrderBy(t => ColituVpnService.TransportRank(t.Item1, t.Item2, t.Item3, lastGood: false, hintedBlocked: t.Item4))
            .Select(t => t.Item1)
            .ToList();

        order.Last().Should().Be("hysteria2");
        order.First().Should().Be("vless-reality");
    }

    [Fact]
    public void WarmSpare_NeverPicksAHintedProtocol_UnlessNothingElseIsOffered()
    {
        static bool Hinted(string protocol) => protocol == "hysteria2";
        ColituWarmSpare.PickTransport("vless-reality", ["vless-reality", "hysteria2", "trojan"], singBox: true, _ => false, sameServer: true, Hinted)
            .Should().Be("trojan");
        ColituWarmSpare.PickTransport("vless-reality", ["vless-reality", "hysteria2"], singBox: true, _ => false, sameServer: true, Hinted)
            .Should().Be("hysteria2");
    }

    // ── Tag integrity of every config variant ───────────────────────────────
    private const string Xray = """
    {
      "inbounds": [ { "tag": "socks", "port": 10808, "listen": "127.0.0.1", "protocol": "mixed", "settings": { "udp": true, "auth": "noauth" } } ],
      "outbounds": [
        { "tag": "proxy", "protocol": "vless", "settings": { "vnext": [ { "address": "203.0.113.7", "port": 443, "users": [ { "id": "11111111-2222-3333-4444-555555555555", "encryption": "none" } ] } ] } },
        { "tag": "direct", "protocol": "freedom" },
        { "tag": "block", "protocol": "blackhole" },
        { "tag": "dns", "protocol": "dns" }
      ],
      "dns": { "tag": "dns-module", "servers": [ "https://dns.example.net/dns-query", { "address": "77.88.8.8", "tag": "direct-dns-1", "domains": [ "domain:ru" ] } ] },
      "routing": { "domainStrategy": "AsIs", "rules": [
        { "type": "field", "inboundTag": [ "dns-module" ], "outboundTag": "proxy" },
        { "type": "field", "inboundTag": [ "direct-dns-1" ], "outboundTag": "direct" },
        { "type": "field", "port": "53", "outboundTag": "dns" },
        { "type": "field", "domain": [ "geosite:category-ads-all" ], "outboundTag": "block" },
        { "type": "field", "ip": [ "geoip:private" ], "outboundTag": "direct" },
        { "type": "field", "port": "0-65535", "outboundTag": "proxy" } ] }
    }
    """;

    private const string Singbox = """
    {
      "dns": { "servers": [
        { "tag": "remote_dns", "type": "https", "server": "dns.example.net", "domain_resolver": "local_local", "detour": "proxy" },
        { "tag": "direct_dns", "type": "udp", "server": "77.88.8.8" },
        { "tag": "local_local", "type": "udp", "server": "8.8.8.8" } ], "final": "remote_dns" },
      "inbounds": [ { "type": "mixed", "tag": "socks", "listen": "127.0.0.1", "listen_port": 10808 } ],
      "outbounds": [
        { "type": "hysteria2", "tag": "proxy", "server": "203.0.113.7", "server_port": 8443, "password": "p", "tls": { "enabled": true, "server_name": "www.example.com" } },
        { "type": "direct", "tag": "direct" }
      ],
      "route": { "rules": [ { "action": "sniff" }, { "protocol": "dns", "action": "hijack-dns" }, { "ip_is_private": true, "outbound": "direct" } ], "final": "proxy", "default_domain_resolver": "local_local" }
    }
    """;

    private static JsonObject XraySpare() => JsonNode.Parse("""{ "tag": "proxy", "protocol": "trojan", "settings": { "servers": [ { "address": "203.0.113.8", "port": 443, "password": "p" } ] } }""")!.AsObject();
    private static JsonObject SingboxSpare() => JsonNode.Parse("""{ "type": "vless", "tag": "proxy", "server": "203.0.113.7", "server_port": 443, "uuid": "11111111-2222-3333-4444-555555555555" }""")!.AsObject();
    private static readonly ColituVerifyInbound Verify = new(41234, "u", "p");

    [Fact]
    public void EveryXrayVariant_HasNoDanglingReference()
    {
        // Spare off (and off after on: every config is generated from scratch, nothing is detached).
        ColituWarmSpare.XrayProblems(Xray).Should().BeEmpty();
        var on = ColituWarmSpare.ApplyXray(Xray, XraySpare(), verify: Verify)!;
        ColituWarmSpare.XrayProblems(on).Should().BeEmpty();
        ColituWarmSpare.XrayProblems(ColituWarmSpare.ApplyXray(Xray, XraySpare())!).Should().BeEmpty();

        // The DNS module still reaches a working path (the balancer), port 53 its dns outbound.
        var rules = JsonNode.Parse(on)!["routing"]!["rules"]!.AsArray();
        rules.Single(rule => rule!["inboundTag"]?.ToJsonString().Contains("dns-module") == true)!["balancerTag"]!.GetValue<string>().Should().Be("proxy-auto");
        rules.Single(rule => rule!["port"]?.GetValue<string>() == "53")!["outboundTag"]!.GetValue<string>().Should().Be("dns");
    }

    [Fact]
    public void EverySingboxVariant_HasNoDanglingReference()
    {
        ColituWarmSpare.SingboxProblems(Singbox).Should().BeEmpty();
        var on = ColituWarmSpare.ApplySingbox(Singbox, SingboxSpare(), verify: Verify)!;
        ColituWarmSpare.SingboxProblems(on).Should().BeEmpty();
        // The remote DNS server's detour is the group (both paths), never a missing tag.
        JsonNode.Parse(on)!["dns"]!["servers"]![0]!["detour"]!.GetValue<string>().Should().Be("proxy");
    }

    [Fact]
    public void IntegrityCheck_CatchesDanglingTagsAndALooseVerifyRule()
    {
        ColituWarmSpare.XrayProblems(Xray.Replace("\"outboundTag\": \"block\"", "\"outboundTag\": \"gone\"")).Should().ContainSingle();
        var on = JsonNode.Parse(ColituWarmSpare.ApplyXray(Xray, XraySpare(), verify: Verify)!)!;
        on["routing"]!["rules"]![0]!["inboundTag"]!.AsArray().Add("socks");
        ColituWarmSpare.XrayProblems(on.ToJsonString()).Should().NotBeEmpty();

        ColituWarmSpare.SingboxProblems(Singbox.Replace("\"detour\": \"proxy\"", "\"detour\": \"proxy-spare\"")).Should().ContainSingle();
        var sb = JsonNode.Parse(ColituWarmSpare.ApplySingbox(Singbox, SingboxSpare(), verify: Verify)!)!;
        sb["route"]!["rules"]![0]!["outbound"] = "proxy";
        ColituWarmSpare.SingboxProblems(sb.ToJsonString()).Should().NotBeEmpty("the verify rule must not reach the urltest group");
    }
}
