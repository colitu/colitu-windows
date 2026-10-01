using AwesomeAssertions;
using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Models;
using ServiceLib.Services.CoreConfig;
using Xunit;

namespace ServiceLib.Tests.CoreConfig;

/// <summary>
/// Colitu's ad blocking (v2rayN/Services/ColituVpnService.cs) sets the remote DNS to its
/// AdGuard Home DoH servers, switches routing to IPIfNonMatch and adds a 0.0.0.0 -> block rule.
/// These tests pin down that both cores turn those settings into a working config.
/// </summary>
public class ColituAdBlockConfigTests
{
    private const string Doh = "https://dns-a.example.test:3443/dns-query,https://dns-b.example.test:3443/dns-query";

    private static CoreConfigContext AdBlockContext(ECoreType core)
    {
        var config = CoreConfigTestFactory.CreateConfig(core);
        config.SimpleDNSItem.RemoteDNS = Doh;
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
                        OutboundTag = Global.BlockTag,
                        Ip = ["0.0.0.0/32", "::/128"],
                    }
                }),
                DomainStrategy = Global.IPIfNonMatch,
                DomainStrategy4Singbox = string.Empty,
            }
        };
    }

    [Fact]
    public void Xray_ResolvesThroughColituDnsAndBlocksNullAnswers()
    {
        var result = new CoreConfigV2rayService(AdBlockContext(ECoreType.Xray)).GenerateClientConfigContent();

        result.Success.Should().BeTrue($"ret msg: {result.Msg}");
        var text = result.Data!.ToString()!;
        var cfg = JsonUtils.Deserialize<V2rayConfig>(text)!;

        cfg.routing.domainStrategy.Should().Be(Global.IPIfNonMatch);
        cfg.routing.rules.Should().Contain(r => r.ip != null && r.ip.Contains("0.0.0.0/32") && r.outboundTag == Global.BlockTag);
        cfg.outbounds.Should().Contain(o => o.tag == Global.BlockTag);
        text.Should().Contain("dns-a.example.test:3443/dns-query").And.Contain("dns-b.example.test:3443/dns-query");
    }

    [Fact]
    public void SingBox_ResolvesBeforeTheNullAnswerRule()
    {
        var result = new CoreConfigSingboxService(AdBlockContext(ECoreType.sing_box)).GenerateClientConfigContent();

        result.Success.Should().BeTrue($"ret msg: {result.Msg}");
        var text = result.Data!.ToString()!;
        var cfg = JsonUtils.Deserialize<SingboxConfig>(text)!;

        var rules = cfg.route.rules;
        var resolve = rules.FindIndex(r => r.action == "resolve");
        resolve.Should().BeGreaterThanOrEqualTo(0, "the domain must be resolved before the IP rule can match");
        // v2rayN repeats the IP rules after the resolve step; that copy is the one that sees 0.0.0.0.
        var block = rules.FindIndex(resolve + 1, r => r.ip_cidr != null && r.ip_cidr.Contains("0.0.0.0/32")
            && (r.outbound == Global.BlockTag || r.action == "reject"));
        block.Should().BeGreaterThan(resolve);
        text.Should().Contain("dns-a.example.test");
    }
}
