using System.Text.Json.Nodes;
using AwesomeAssertions;
using v2rayN.Services;
using Xunit;

namespace v2rayN.Tests;

/// <summary>Warm spare: which transport backs the primary, and the core configs it produces.</summary>
public class ColituWarmSpareTests
{
    private static readonly string[] All = ["hysteria2", "vless-reality", "vless-xhttp", "trojan", "shadowsocks"];

    private static string? Pick(string primary, IEnumerable<string> offered, bool singBox, bool sameServer = true, params string[] stalled) =>
        ColituWarmSpare.PickTransport(primary, offered, singBox, protocol => stalled.Contains(protocol), sameServer);

    [Fact]
    public void Hysteria2Primary_GetsATcpSpare_RealityFirst_NeverXhttpOnSingBox()
    {
        Pick("hysteria2", All, singBox: true).Should().Be("vless-reality");
        Pick("hysteria2", ["hysteria2", "vless-xhttp", "trojan"], singBox: true).Should().Be("trojan");
        Pick("hysteria2", ["hysteria2", "vless-xhttp"], singBox: true).Should().BeNull();
    }

    [Fact]
    public void TcpPrimary_OnXray_GetsAnotherTcpTransport_Hysteria2OnlyOnSingBox()
    {
        // Xray never runs Hysteria2 here: the next TCP transport instead.
        Pick("vless-reality", All, singBox: false).Should().Be("vless-xhttp");
        Pick("trojan", All, singBox: false).Should().Be("vless-reality");
        // Apps split (sing-box for everything): the other family.
        Pick("vless-reality", All, singBox: true).Should().Be("hysteria2");
    }

    [Fact]
    public void StalledTransports_AreNeverTheSpare()
    {
        Pick("vless-reality", All, singBox: true, sameServer: true, "hysteria2").Should().Be("trojan");
        Pick("hysteria2", All, singBox: true, sameServer: true, "vless-reality", "trojan", "shadowsocks").Should().BeNull();
    }

    [Fact]
    public void SingleTransport_HasNoSpareOnTheSameServer_ButAnotherServerMayUseTheSameOne()
    {
        Pick("vless-reality", ["vless-reality"], singBox: false).Should().BeNull();
        Pick("vless-reality", ["vless-reality"], singBox: false, sameServer: false).Should().Be("vless-reality");
        Pick("hysteria2", ["hysteria2"], singBox: true, sameServer: false).Should().Be("hysteria2");
    }

    [Fact]
    public void NoSpare_WhenTheSettingIsOff_OrOnAMultihopRoute()
    {
        var on = new ColituVpnPreferences();
        on.WarmSpareEnabled.Should().BeTrue();
        var config = new ColituVpnConfigResponse { Server = new ColituVpnServer { Id = "n1" } };
        ColituVpnService.SpareAllowed(on, new ColituVpnServer { Id = "n1" }, config).Should().BeTrue();
        ColituVpnService.SpareAllowed(on with { WarmSpareEnabled = false }, null, config).Should().BeFalse();
        ColituVpnService.SpareAllowed(on, new ColituVpnServer { Id = "r1", IsMultihop = true }, config).Should().BeFalse();
        ColituVpnService.SpareAllowed(on, null, new ColituVpnConfigResponse { Server = new ColituVpnServer { Id = "r1", IsMultihop = true } }).Should().BeFalse();
    }

    private const string XrayConfig = """
    {
      "outbounds": [
        { "tag": "proxy", "protocol": "vless", "settings": { "vnext": [ { "address": "203.0.113.7", "port": 443 } ] } },
        { "tag": "direct", "protocol": "freedom" },
        { "tag": "block", "protocol": "blackhole" }
      ],
      "routing": {
        "domainStrategy": "AsIs",
        "rules": [
          { "type": "field", "inboundTag": [ "dns-module" ], "outboundTag": "proxy" },
          { "type": "field", "domain": [ "geosite:category-ads-all" ], "outboundTag": "block" },
          { "type": "field", "ip": [ "geoip:private" ], "outboundTag": "direct" },
          { "type": "field", "port": "0-65535", "outboundTag": "proxy" }
        ]
      }
    }
    """;

    private static JsonObject XraySpare() => JsonNode.Parse("""{ "tag": "proxy", "protocol": "trojan", "settings": { "servers": [ { "address": "203.0.113.8", "port": 443 } ] } }""")!.AsObject();

    [Fact]
    public void Xray_GetsBalancerObservatoryAndEveryProxyRuleOnTheBalancer()
    {
        var root = JsonNode.Parse(ColituWarmSpare.ApplyXray(XrayConfig, XraySpare())!)!.AsObject();

        var outbounds = root["outbounds"]!.AsArray().Select(item => item!["tag"]!.GetValue<string>()).ToList();
        outbounds.Should().Equal("proxy", "warm-spare", "direct", "block");
        root["outbounds"]![1]!["protocol"]!.GetValue<string>().Should().Be("trojan");

        var rules = root["routing"]!["rules"]!.AsArray().Select(item => item!.AsObject()).ToList();
        rules.Should().NotContain(rule => rule["outboundTag"] != null && rule["outboundTag"]!.GetValue<string>() == "proxy");
        rules.Count(rule => rule["balancerTag"] != null).Should().Be(3, "the DNS rule, the final port rule and the added catch-all");
        rules.Last()["balancerTag"]!.GetValue<string>().Should().Be("proxy-auto");
        rules.Should().Contain(rule => rule["outboundTag"] != null && rule["outboundTag"]!.GetValue<string>() == "block");

        var balancer = root["routing"]!["balancers"]!.AsArray().Single()!;
        balancer["tag"]!.GetValue<string>().Should().Be("proxy-auto");
        balancer["selector"]!.AsArray().Select(item => item!.GetValue<string>()).Should().Equal("proxy");
        balancer["fallbackTag"]!.GetValue<string>().Should().Be("warm-spare");
        balancer["strategy"]!["type"]!.GetValue<string>().Should().Be("random");

        var observatory = root["observatory"]!;
        observatory["subjectSelector"]!.AsArray().Select(item => item!.GetValue<string>()).Should().Equal("proxy");
        observatory["probeUrl"]!.GetValue<string>().Should().Be("http://www.gstatic.com/generate_204");
        observatory["probeInterval"]!.GetValue<string>().Should().Be("3s");
        root["burstObservatory"].Should().BeNull();
    }

    [Fact]
    public void Xray_NoTagStartsWithProxyExceptThePrimary()
    {
        // Balancer selectors match prefixes: "proxy-spare" would be balanced with the primary.
        var root = JsonNode.Parse(ColituWarmSpare.ApplyXray(XrayConfig, XraySpare())!)!;
        root["outbounds"]!.AsArray().Select(item => item!["tag"]!.GetValue<string>())
            .Where(tag => tag.StartsWith("proxy", StringComparison.Ordinal)).Should().Equal("proxy");

        var clash = XrayConfig.Replace("\"tag\": \"direct\"", "\"tag\": \"proxy-2\"");
        ColituWarmSpare.ApplyXray(clash, XraySpare()).Should().BeNull();
    }

    private const string SingboxConfig = """
    {
      "dns": { "servers": [ { "tag": "remote", "type": "https", "server": "1.1.1.1", "detour": "proxy" } ] },
      "outbounds": [
        { "type": "hysteria2", "tag": "proxy", "server": "203.0.113.7", "server_port": 8443 },
        { "type": "direct", "tag": "direct" }
      ],
      "route": { "rules": [ { "action": "sniff" }, { "ip_is_private": true, "outbound": "direct" } ], "final": "proxy" }
    }
    """;

    [Fact]
    public void Singbox_GetsAUrltestGroupNamedProxy_SoRulesFinalAndDnsFollowIt()
    {
        var spare = JsonNode.Parse("""{ "type": "vless", "tag": "proxy", "server": "203.0.113.7", "server_port": 443 }""")!.AsObject();
        var root = JsonNode.Parse(ColituWarmSpare.ApplySingbox(SingboxConfig, spare)!)!;

        var outbounds = root["outbounds"]!.AsArray();
        outbounds.Select(item => item!["tag"]!.GetValue<string>()).Should().Equal("proxy", "proxy-main", "warm-spare", "direct");
        var group = outbounds[0]!;
        group["type"]!.GetValue<string>().Should().Be("urltest");
        group["outbounds"]!.AsArray().Select(item => item!.GetValue<string>()).Should().Equal("proxy-main", "warm-spare");
        group["url"]!.GetValue<string>().Should().Be("http://www.gstatic.com/generate_204");
        group["interval"]!.GetValue<string>().Should().Be("5s");
        group["tolerance"]!.GetValue<int>().Should().BeGreaterThanOrEqualTo(3000);
        group["interrupt_exist_connections"]!.GetValue<bool>().Should().BeFalse();
        outbounds[1]!["type"]!.GetValue<string>().Should().Be("hysteria2");
        outbounds[2]!["type"]!.GetValue<string>().Should().Be("vless");
        // Untouched references now point at the group.
        root["route"]!["final"]!.GetValue<string>().Should().Be("proxy");
        root["dns"]!["servers"]![0]!["detour"]!.GetValue<string>().Should().Be("proxy");
    }

    private static readonly ColituVerifyInbound Verify = new(41234, "u1", "p1");

    [Fact]
    public void Xray_VerifyInbound_GoesStraightToThePrimary_BeforeEveryOtherRule()
    {
        var root = JsonNode.Parse(ColituWarmSpare.ApplyXray(XrayConfig, XraySpare(), verify: Verify)!)!;

        var inbound = root["inbounds"]!.AsArray().Single(item => item!["tag"]!.GetValue<string>() == "colitu-verify")!;
        inbound["listen"]!.GetValue<string>().Should().Be("127.0.0.1");
        inbound["port"]!.GetValue<int>().Should().Be(41234);
        inbound["protocol"]!.GetValue<string>().Should().Be("socks");
        inbound["settings"]!["auth"]!.GetValue<string>().Should().Be("password");
        inbound["settings"]!["accounts"]![0]!["user"]!.GetValue<string>().Should().Be("u1");

        var rules = root["routing"]!["rules"]!.AsArray().Select(item => item!.AsObject()).ToList();
        rules[0]["inboundTag"]!.AsArray().Select(item => item!.GetValue<string>()).Should().Equal("colitu-verify");
        rules[0]["outboundTag"]!.GetValue<string>().Should().Be("proxy");
        rules[0]["balancerTag"].Should().BeNull();
        rules.Skip(1).Should().NotContain(rule => rule["inboundTag"] != null && rule["inboundTag"]!.ToJsonString().Contains("colitu-verify"));
    }

    [Fact]
    public void Singbox_VerifyInbound_GoesStraightToThePrimary_NeverTheUrltestGroup()
    {
        var spare = JsonNode.Parse("""{ "type": "vless", "tag": "proxy", "server": "203.0.113.7", "server_port": 443 }""")!.AsObject();
        var root = JsonNode.Parse(ColituWarmSpare.ApplySingbox(SingboxConfig, spare, verify: Verify)!)!;

        var inbound = root["inbounds"]!.AsArray().Single(item => item!["tag"]!.GetValue<string>() == "colitu-verify")!;
        inbound["type"]!.GetValue<string>().Should().Be("socks");
        inbound["listen"]!.GetValue<string>().Should().Be("127.0.0.1");
        inbound["listen_port"]!.GetValue<int>().Should().Be(41234);
        inbound["users"]![0]!["password"]!.GetValue<string>().Should().Be("p1");

        var first = root["route"]!["rules"]![0]!;
        first["inbound"]!.AsArray().Select(item => item!.GetValue<string>()).Should().Equal("colitu-verify");
        first["outbound"]!.GetValue<string>().Should().Be("proxy-main");
    }

    [Fact]
    public void VerifyInbound_HasAFreeLoopbackPortAndRandomCredentials()
    {
        var a = ColituWarmSpare.NewVerifyInbound();
        var b = ColituWarmSpare.NewVerifyInbound();
        a.Port.Should().BeInRange(1, 65535);
        a.Password.Should().HaveLength(32).And.NotBe(b.Password);
        a.User.Should().NotBe(b.User);
    }

    [Fact]
    public void FindOutbound_ReturnsACopyOfTheProxyOutbound()
    {
        var outbound = ColituWarmSpare.FindOutbound(SingboxConfig)!;
        outbound["type"]!.GetValue<string>().Should().Be("hysteria2");
        ColituWarmSpare.FindOutbound("not json").Should().BeNull();
        ColituWarmSpare.FindOutbound("""{"outbounds":[{"tag":"direct"}]}""").Should().BeNull();
    }
}
