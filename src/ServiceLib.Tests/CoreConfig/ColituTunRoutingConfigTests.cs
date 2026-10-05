using AwesomeAssertions;
using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Models;
using ServiceLib.Services.CoreConfig;
using Xunit;

namespace ServiceLib.Tests.CoreConfig;

/// <summary>
/// Windows 2.5.5: in TUN mode Colitu routes with AsIs (ColituVpnService.RoutingDomainStrategy),
/// so no connection waits for the core's DoH resolver. These tests pin down that the Russian
/// rules still match (sniffed domain for routing only, the app's address for geoip:ru), that
/// the per-connection process rules are gone once the outbounds are bound to the adapter, and
/// that Hysteria2 without a declared bandwidth uses BBR.
/// </summary>
public class ColituTunRoutingConfigTests
{
    private static CoreConfigContext TunContext(ECoreType core, string? bindInterface = "Wi-Fi", ProfileItem? node = null)
    {
        var config = CoreConfigTestFactory.CreateConfig(core);
        config.RoutingBasicItem.DomainStrategy = Global.AsIs;
        config.TunModeItem = new TunModeItem { EnableTun = true, AutoRoute = true, Stack = "gvisor", Mtu = 9000, IcmpRouting = "rule" };
        config.CoreBasicItem.BindInterface = bindInterface;
        config.Inbound[0].RouteOnly = true;
        config.HysteriaItem = new HysteriaItem { UpMbps = 0, DownMbps = 0 };
        CoreConfigTestFactory.BindAppManagerConfig(config);
        node ??= core == ECoreType.sing_box
            ? CoreConfigTestFactory.CreateSocksNode(core)
            : CoreConfigTestFactory.CreateVmessNode(core, "n-main", "main");
        return CoreConfigTestFactory.CreateContext(config, node, core) with
        {
            IsTunEnabled = true,
            RoutingItem = new RoutingItem
            {
                Id = "colitu",
                Remarks = "Colitu",
                RuleSet = JsonUtils.Serialize(new List<RulesItem>
                {
                    new() { Enabled = true, RuleType = ERuleType.Routing, OutboundTag = Global.DirectTag, Domain = ["geosite:category-ru"] },
                    new() { Enabled = true, RuleType = ERuleType.Routing, OutboundTag = Global.DirectTag, Ip = ["geoip:ru"] },
                }),
                DomainStrategy = Global.AsIs,
                DomainStrategy4Singbox = string.Empty,
            }
        };
    }

    [Fact]
    public void Xray_Tun_RoutesWithoutLookupsAndKeepsTheRussianRules()
    {
        var result = new CoreConfigV2rayService(TunContext(ECoreType.Xray)).GenerateClientConfigContent();

        result.Success.Should().BeTrue($"ret msg: {result.Msg}");
        var cfg = JsonUtils.Deserialize<V2rayConfig>(result.Data!.ToString()!)!;

        cfg.routing.domainStrategy.Should().Be(Global.AsIs);
        cfg.routing.rules.Should().Contain(r => r.domain != null && r.domain.Contains("geosite:category-ru") && r.outboundTag == Global.DirectTag);
        cfg.routing.rules.Should().Contain(r => r.ip != null && r.ip.Contains("geoip:ru") && r.outboundTag == Global.DirectTag);
        // The sniffed domain is for routing only: the connection keeps the app's address for geoip:ru.
        var tun = cfg.inbounds.Single(i => i.tag == "tun");
        tun.sniffing.enabled.Should().BeTrue();
        tun.sniffing.routeOnly.Should().BeTrue();
    }

    [Fact]
    public void Xray_Tun_BoundToTheAdapter_HasNoProcessRules()
    {
        var result = new CoreConfigV2rayService(TunContext(ECoreType.Xray)).GenerateClientConfigContent();

        result.Success.Should().BeTrue($"ret msg: {result.Msg}");
        var cfg = JsonUtils.Deserialize<V2rayConfig>(result.Data!.ToString()!)!;
        cfg.routing.rules.Should().NotContain(r => r.process != null && r.process.Count > 0);
        // DNS from the apps still reaches the core's resolver.
        cfg.routing.rules.Should().Contain(r => r.port == "53" && r.outboundTag == Global.DnsOutboundTag);
    }

    [Fact]
    public void Xray_Tun_WithoutBoundAdapter_KeepsTheProcessRules()
    {
        var result = new CoreConfigV2rayService(TunContext(ECoreType.Xray, bindInterface: null)).GenerateClientConfigContent();

        result.Success.Should().BeTrue($"ret msg: {result.Msg}");
        var cfg = JsonUtils.Deserialize<V2rayConfig>(result.Data!.ToString()!)!;
        cfg.routing.rules.Should().Contain(r => r.process != null && r.process.Count > 0 && r.outboundTag == Global.DirectTag);
    }

    [Fact]
    public void SingBox_Tun_DoesNotResolveBeforeRouting()
    {
        var result = new CoreConfigSingboxService(TunContext(ECoreType.sing_box)).GenerateClientConfigContent();

        result.Success.Should().BeTrue($"ret msg: {result.Msg}");
        var cfg = JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString()!)!;

        cfg.route.rules.Should().NotContain(r => r.action == "resolve");
        cfg.route.rules.Should().Contain(r => r.action == "sniff");
        cfg.route.rules.Should().Contain(r => r.rule_set != null && r.rule_set.Contains("geosite-category-ru") && r.outbound == Global.DirectTag);
        cfg.route.rules.Should().Contain(r => r.rule_set != null && r.rule_set.Contains("geoip-ru") && r.outbound == Global.DirectTag);
    }

    [Fact]
    public void SingBox_Hysteria2_WithoutBandwidth_UsesBbr()
    {
        var node = ServiceLib.Handler.Fmt.Hysteria2Fmt.Resolve("hysteria2://secret@203.0.113.7:8443?sni=vpn.example.com&insecure=0#Colitu", out _)!;
        node.IndexId = "n-hy2";
        node.CoreType = ECoreType.sing_box;

        var result = new CoreConfigSingboxService(TunContext(ECoreType.sing_box, node: node)).GenerateClientConfigContent();

        result.Success.Should().BeTrue($"ret msg: {result.Msg}");
        var cfg = JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString()!)!;
        var hysteria = cfg.outbounds.Single(o => o.type == "hysteria2");
        hysteria.up_mbps.Should().BeNull();
        hysteria.down_mbps.Should().BeNull();
    }
}
