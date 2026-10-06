using AwesomeAssertions;
using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Models;
using ServiceLib.Services.CoreConfig;
using Xunit;

namespace ServiceLib.Tests.CoreConfig;

/// <summary>
/// Windows 2.6.0 split tunnelling (ColituVPN/Services/ColituSplitTunnel.cs). The app adds these
/// rules to the Colitu routing profile; these tests pin down what each core makes of them:
/// sing-box in TUN mode (process_path/process_name, domain_suffix, ip_cidr, final direct with
/// direct DNS) and Xray/sing-box in proxy mode (sites and addresses only).
/// </summary>
public class ColituSplitTunnelConfigTests
{
    private const string App = @"C:\Games\game.exe";

    /// <summary>The rules exactly as ColituSplitTunnel.BuildRules writes them (RuleType unset, as in the app).</summary>
    private static List<RulesItem> SplitRules(bool only, bool tun)
    {
        var outbound = only ? Global.ProxyTag : Global.DirectTag;
        var rules = new List<RulesItem>
        {
            new() { Id = "colitu-dns-protection", Enabled = true, OutboundTag = Global.ProxyTag, Port = "53", Network = "tcp,udp" },
        };
        if (tun)
        {
            rules.Add(new() { Id = "colitu-split-apps", Enabled = true, OutboundTag = outbound, Process = [App, "game.exe"] });
        }
        rules.Add(new() { Id = "colitu-split-domains", Enabled = true, OutboundTag = outbound, Domain = ["domain:bank.example"] });
        rules.Add(new() { Id = "colitu-split-networks", Enabled = true, OutboundTag = outbound, Ip = ["198.51.100.0/24"] });
        if (only)
        {
            rules.Add(new() { Id = "colitu-split-rest-direct", Enabled = true, OutboundTag = Global.DirectTag, Port = "0-65535", Network = "tcp,udp" });
        }
        return rules;
    }

    private static CoreConfigContext Context(ECoreType core, bool tun, bool only)
    {
        var config = CoreConfigTestFactory.CreateConfig(core);
        config.RoutingBasicItem.DomainStrategy = tun ? Global.AsIs : Global.IPIfNonMatch;
        if (tun)
        {
            config.TunModeItem = new TunModeItem { EnableTun = true, AutoRoute = true, Stack = "gvisor", Mtu = 9000, IcmpRouting = "rule" };
            config.CoreBasicItem.BindInterface = "Wi-Fi";
            config.Inbound[0].RouteOnly = true;
        }
        config.SimpleDNSItem.DirectDNS = "1.1.1.1";
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var node = core == ECoreType.sing_box
            ? CoreConfigTestFactory.CreateSocksNode(core)
            : CoreConfigTestFactory.CreateVmessNode(core, "n-main", "main");
        return CoreConfigTestFactory.CreateContext(config, node, core) with
        {
            IsTunEnabled = tun,
            RoutingItem = new RoutingItem
            {
                Id = "colitu",
                Remarks = "Colitu",
                RuleSet = JsonUtils.Serialize(SplitRules(only, tun)),
                DomainStrategy = config.RoutingBasicItem.DomainStrategy,
                DomainStrategy4Singbox = string.Empty,
            }
        };
    }

    private static SingboxConfig SingBox(bool tun, bool only)
    {
        var result = new CoreConfigSingboxService(Context(ECoreType.sing_box, tun, only)).GenerateClientConfigContent();
        result.Success.Should().BeTrue($"ret msg: {result.Msg}");
        return JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString()!)!;
    }

    private static V2rayConfig Xray(bool only)
    {
        var result = new CoreConfigV2rayService(Context(ECoreType.Xray, false, only)).GenerateClientConfigContent();
        result.Success.Should().BeTrue($"ret msg: {result.Msg}");
        return JsonUtils.Deserialize<V2rayConfig>(result.Data!.ToString()!)!;
    }

    [Fact]
    public void SingBox_Tun_Bypass_SendsAppsSitesAndAddressesDirect()
    {
        var cfg = SingBox(tun: true, only: false);
        var rules = cfg.route.rules;

        rules.Should().Contain(r => r.process_path != null && r.process_path.Contains(App) && r.outbound == Global.DirectTag);
        rules.Should().Contain(r => r.process_name != null && r.process_name.Contains("game.exe") && r.outbound == Global.DirectTag);
        rules.Should().Contain(r => r.domain_suffix != null && r.domain_suffix.Contains("bank.example") && r.outbound == Global.DirectTag);
        rules.Should().Contain(r => r.ip_cidr != null && r.ip_cidr.Contains("198.51.100.0/24") && r.outbound == Global.DirectTag);
        cfg.route.final.Should().Be(Global.ProxyTag);
        // Sites outside the VPN are resolved by the direct resolver, not through the tunnel.
        cfg.dns.rules.Should().Contain(r => r.domain_suffix != null && r.domain_suffix.Contains("bank.example") && r.server == Global.SingboxDirectDNSTag);
        cfg.dns.final.Should().Be(Global.SingboxRemoteDNSTag);
    }

    [Fact]
    public void SingBox_Tun_Only_SendsTheSelectedToTheProxy_AndEverythingElseDirect()
    {
        var cfg = SingBox(tun: true, only: true);
        var rules = cfg.route.rules;

        rules.Should().Contain(r => r.process_path != null && r.process_path.Contains(App) && r.outbound == Global.ProxyTag);
        rules.Should().Contain(r => r.domain_suffix != null && r.domain_suffix.Contains("bank.example") && r.outbound == Global.ProxyTag);
        var rest = rules.FindLastIndex(r => r.port_range != null && r.port_range.Contains("0:65535") && r.outbound == Global.DirectTag);
        rest.Should().BeGreaterThan(rules.FindIndex(r => r.process_path != null && r.process_path.Contains(App)));
        // With "everything else direct" the final resolver is the direct one.
        cfg.dns.final.Should().Be(Global.SingboxDirectDNSTag);
        cfg.dns.rules.Should().Contain(r => r.domain_suffix != null && r.domain_suffix.Contains("bank.example") && r.server == Global.SingboxRemoteDNSTag);
    }

    [Fact]
    public void SingBox_Proxy_HasNoProcessRules()
    {
        var cfg = SingBox(tun: false, only: false);
        cfg.route.rules.Should().NotContain(r => (r.process_name != null && r.process_name.Count > 0 && r.process_name.Contains("game.exe"))
            || (r.process_path != null && r.process_path.Count > 0));
        cfg.route.rules.Should().Contain(r => r.domain_suffix != null && r.domain_suffix.Contains("bank.example") && r.outbound == Global.DirectTag);
    }

    [Fact]
    public void Xray_Proxy_Bypass_RoutesSitesAndAddressesDirect()
    {
        var cfg = Xray(only: false);
        cfg.routing.rules.Should().Contain(r => r.domain != null && r.domain.Contains("domain:bank.example") && r.outboundTag == Global.DirectTag);
        cfg.routing.rules.Should().Contain(r => r.ip != null && r.ip.Contains("198.51.100.0/24") && r.outboundTag == Global.DirectTag);
        cfg.routing.rules.Should().NotContain(r => r.process != null && r.process.Contains(App));
    }

    [Fact]
    public void Xray_Proxy_Only_EndsWithACatchAllDirectRule()
    {
        var cfg = Xray(only: true);
        cfg.routing.rules.Should().Contain(r => r.domain != null && r.domain.Contains("domain:bank.example") && r.outboundTag == Global.ProxyTag);
        var rest = cfg.routing.rules.FindIndex(r => r.port == "0-65535" && r.outboundTag == Global.DirectTag);
        rest.Should().BeGreaterThan(cfg.routing.rules.FindIndex(r => r.domain != null && r.domain.Contains("domain:bank.example")));
        // Only the core's own DNS rules (inbound "dns") follow it.
        cfg.routing.rules.Skip(rest + 1).Should().OnlyContain(r => r.inboundTag != null && r.inboundTag.Count > 0);
    }
}
