using AwesomeAssertions;
using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Manager;
using ServiceLib.Models;
using ServiceLib.Services.CoreConfig;
using Xunit;

namespace ServiceLib.Tests.CoreConfig;

/// <summary>
/// Colitu sends Russian domains (geosite:category-ru) and Russian IPs (geoip:ru) out directly
/// (ColituVPN/Services/ColituVpnService.cs, BuildColituRoutingRules) with IPIfNonMatch routing.
/// These tests pin down that both cores turn those rules into direct routes.
/// </summary>
public class ColituRuDirectConfigTests
{
    private static CoreConfigContext RuDirectContext(ECoreType core, bool ruDirect = true)
    {
        var config = CoreConfigTestFactory.CreateConfig(core);
        config.RoutingBasicItem.DomainStrategy = Global.IPIfNonMatch;
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var node = core == ECoreType.sing_box
            ? CoreConfigTestFactory.CreateSocksNode(core)
            : CoreConfigTestFactory.CreateVmessNode(core, "n-main", "main");
        return CoreConfigTestFactory.CreateContext(config, node, core) with
        {
            RoutingItem = new RoutingItem
            {
                Id = "colitu",
                Remarks = "Colitu",
                RuleSet = JsonUtils.Serialize(new List<RulesItem>
                {
                    new()
                    {
                        Enabled = true,
                        RuleType = ERuleType.Routing,
                        OutboundTag = Global.ProxyTag,
                        Port = "53",
                        Network = "tcp,udp",
                    },
                    new()
                    {
                        Enabled = true,
                        RuleType = ERuleType.Routing,
                        OutboundTag = Global.DirectTag,
                        Ip = ["geoip:private"],
                    },
                    new()
                    {
                        Enabled = ruDirect,
                        RuleType = ERuleType.Routing,
                        OutboundTag = Global.DirectTag,
                        Domain = ["geosite:category-ru"],
                    },
                    new()
                    {
                        Enabled = ruDirect,
                        RuleType = ERuleType.Routing,
                        OutboundTag = Global.DirectTag,
                        Ip = ["geoip:ru"],
                    }
                }),
                DomainStrategy = Global.IPIfNonMatch,
                DomainStrategy4Singbox = string.Empty,
            }
        };
    }

    [Fact]
    public void Xray_SendsRussianDomainsAndIpsDirect()
    {
        var result = new CoreConfigV2rayService(RuDirectContext(ECoreType.Xray)).GenerateClientConfigContent();

        result.Success.Should().BeTrue($"ret msg: {result.Msg}");
        var cfg = JsonUtils.Deserialize<V2rayConfig>(result.Data!.ToString()!)!;

        cfg.routing.domainStrategy.Should().Be(Global.IPIfNonMatch);
        cfg.routing.rules.Should().Contain(r => r.domain != null && r.domain.Contains("geosite:category-ru") && r.outboundTag == Global.DirectTag);
        cfg.routing.rules.Should().Contain(r => r.ip != null && r.ip.Contains("geoip:ru") && r.outboundTag == Global.DirectTag);
        cfg.outbounds.Should().Contain(o => o.tag == Global.DirectTag);
    }

    /// <summary>
    /// Privacy mode (or a server in Russia) leaves the Russian rules in the profile but disabled:
    /// neither core may route anything Russian directly then.
    /// </summary>
    [Fact]
    public void Xray_DisabledRussianRules_SendNothingDirect()
    {
        var result = new CoreConfigV2rayService(RuDirectContext(ECoreType.Xray, ruDirect: false)).GenerateClientConfigContent();

        result.Success.Should().BeTrue($"ret msg: {result.Msg}");
        var cfg = JsonUtils.Deserialize<V2rayConfig>(result.Data!.ToString()!)!;

        cfg.routing.rules.Should().NotContain(r => r.domain != null && r.domain.Contains("geosite:category-ru"));
        cfg.routing.rules.Should().NotContain(r => r.ip != null && r.ip.Contains("geoip:ru"));
        result.Data!.ToString()!.Should().NotContain("category-ru");
    }

    [Fact]
    public void SingBox_DisabledRussianRules_SendNothingDirect()
    {
        var result = new CoreConfigSingboxService(RuDirectContext(ECoreType.sing_box, ruDirect: false)).GenerateClientConfigContent();

        result.Success.Should().BeTrue($"ret msg: {result.Msg}");
        var cfg = JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString()!)!;

        (cfg.route.rule_set ?? []).Should().NotContain(r => r.tag == "geosite-category-ru" || r.tag == "geoip-ru");
        cfg.route.rules.Should().NotContain(r => r.rule_set != null && (r.rule_set.Contains("geosite-category-ru") || r.rule_set.Contains("geoip-ru")));
        result.Data!.ToString()!.Should().NotContain("category-ru");
    }

    [Fact]
    public void Xray_LooksForGeoFilesNextToItsExecutable()
    {
        // The installer puts geoip.dat/geosite.dat in bin\xray beside xray.exe. Windows 2.5.1 pointed
        // XRAY_LOCATION_ASSET at bin\, so every Xray transport failed with "failed to open geosite.dat".
        var xray = CoreInfoManager.Instance.GetCoreInfo(ECoreType.Xray)!;
        var exeDir = Path.GetDirectoryName(Utils.GetBinPath("xray.exe", ECoreType.Xray.ToString()));

        Path.GetFullPath(xray.Environment[Global.XrayLocalAsset]!).Should().Be(Path.GetFullPath(exeDir!));
    }

    [Fact]
    public void SingBox_UsesTheRuleSetsAndResolvesBeforeTheIpRule()
    {
        var result = new CoreConfigSingboxService(RuDirectContext(ECoreType.sing_box)).GenerateClientConfigContent();

        result.Success.Should().BeTrue($"ret msg: {result.Msg}");
        var cfg = JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString()!)!;

        cfg.route.rule_set.Should().Contain(r => r.tag == "geosite-category-ru");
        cfg.route.rule_set.Should().Contain(r => r.tag == "geoip-ru");

        var rules = cfg.route.rules;
        rules.Should().Contain(r => r.rule_set != null && r.rule_set.Contains("geosite-category-ru") && r.outbound == Global.DirectTag);
        var resolve = rules.FindIndex(r => r.action == "resolve");
        resolve.Should().BeGreaterThanOrEqualTo(0, "a domain must be resolved before the Russian IP rule can match");
        rules.FindIndex(resolve + 1, r => r.rule_set != null && r.rule_set.Contains("geoip-ru") && r.outbound == Global.DirectTag)
            .Should().BeGreaterThan(resolve);
    }

    /// <summary>
    /// The local network goes out directly (colitu-lan-direct): in TUN mode 2.8.0 sent it to the
    /// server, so another PC, a printer or a NAS on the LAN could not be reached. DNS (port 53)
    /// is decided first, so a resolver on the LAN still gets no names.
    /// </summary>
    [Fact]
    public void BothCores_SendTheLocalNetworkDirectAfterTheDnsRule()
    {
        var singbox = new CoreConfigSingboxService(RuDirectContext(ECoreType.sing_box)).GenerateClientConfigContent();
        singbox.Success.Should().BeTrue($"ret msg: {singbox.Msg}");
        var rules = JsonUtils.Deserialize<SingboxConfig>(singbox.Data!.ToString()!)!.route.rules;
        var lan = rules.FindIndex(r => r.ip_is_private == true && r.outbound == Global.DirectTag);
        lan.Should().BeGreaterThanOrEqualTo(0);
        rules.FindIndex(r => r.port != null && r.port.Contains(53) && r.outbound == Global.ProxyTag).Should().BeLessThan(lan);

        var xray = new CoreConfigV2rayService(RuDirectContext(ECoreType.Xray)).GenerateClientConfigContent();
        xray.Success.Should().BeTrue($"ret msg: {xray.Msg}");
        JsonUtils.Deserialize<V2rayConfig>(xray.Data!.ToString()!)!.routing.rules
            .Should().Contain(r => r.ip != null && r.ip.Contains("geoip:private") && r.outboundTag == Global.DirectTag);
    }
}
