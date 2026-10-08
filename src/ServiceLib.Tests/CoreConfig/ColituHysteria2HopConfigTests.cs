using System.Text.Json.Nodes;
using AwesomeAssertions;
using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Models;
using ServiceLib.Services.CoreConfig;
using Xunit;

namespace ServiceLib.Tests.CoreConfig;

/// <summary>
/// The panel adds mport=20000-40000 to hysteria2:// links of nodes that support port hopping
/// (Russian mobile networks throttle a long-lived UDP flow on one port). Both cores must hop:
/// sing-box with server_ports + hop_interval, Xray 26 with finalmask.quicParams.udpHop (the older
/// hysteriaSettings.udphop shape is silently ignored by Xray 26). Without mport nothing changes.
/// </summary>
public class ColituHysteria2HopConfigTests
{
    private const string HopLink = "hysteria2://secret@203.0.113.7:8443?sni=vpn.example.com&insecure=0&mport=20000-40000#Colitu";
    private const string PlainLink = "hysteria2://secret@203.0.113.7:8443?sni=vpn.example.com&insecure=0#Colitu";

    private static CoreConfigContext Context(ECoreType core, string link)
    {
        var config = CoreConfigTestFactory.CreateConfig(core);
        // As ColituVpnService sets them: no declared bandwidth (BBR), 30 s between hops.
        config.HysteriaItem = new HysteriaItem { UpMbps = 0, DownMbps = 0, HopInterval = 30 };
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var node = ServiceLib.Handler.Fmt.Hysteria2Fmt.Resolve(link, out _)!;
        node.IndexId = "n-hy2";
        node.CoreType = core;
        return CoreConfigTestFactory.CreateContext(config, node, core);
    }

    [Fact]
    public void ShareLink_WithMport_KeepsThePortRange()
    {
        var node = ServiceLib.Handler.Fmt.Hysteria2Fmt.Resolve(HopLink, out _)!;

        node.Port.Should().Be(8443);
        node.GetProtocolExtra().Ports.Should().Be("20000-40000");
    }

    [Fact]
    public void SingBox_WithMport_HopsOverTheRangeEvery30Seconds()
    {
        var result = new CoreConfigSingboxService(Context(ECoreType.sing_box, HopLink)).GenerateClientConfigContent();

        result.Success.Should().BeTrue($"ret msg: {result.Msg}");
        var cfg = JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString()!)!;
        var hysteria = cfg.outbounds.Single(o => o.type == "hysteria2");
        hysteria.server_ports.Should().Equal("20000:40000");
        hysteria.hop_interval.Should().Be("30s");
        // server_port and server_ports conflict in sing-box.
        hysteria.server_port.Should().BeNull();
    }

    [Fact]
    public void SingBox_WithoutMport_KeepsTheSinglePort()
    {
        var result = new CoreConfigSingboxService(Context(ECoreType.sing_box, PlainLink)).GenerateClientConfigContent();

        result.Success.Should().BeTrue($"ret msg: {result.Msg}");
        var cfg = JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString()!)!;
        var hysteria = cfg.outbounds.Single(o => o.type == "hysteria2");
        hysteria.server_port.Should().Be(8443);
        hysteria.server_ports.Should().BeNull();
        hysteria.hop_interval.Should().BeNull();
    }

    private static JsonNode HysteriaStream(string link)
    {
        var result = new CoreConfigV2rayService(Context(ECoreType.Xray, link)).GenerateClientConfigContent();
        result.Success.Should().BeTrue($"ret msg: {result.Msg}");
        var root = JsonNode.Parse(result.Data!.ToString()!)!;
        var outbound = root["outbounds"]!.AsArray().Single(o => o?["protocol"]?.GetValue<string>() == "hysteria")!;
        return outbound["streamSettings"]!;
    }

    [Fact]
    public void Xray_WithMport_HopsThroughFinalmaskQuicParams()
    {
        var stream = HysteriaStream(HopLink);

        var udpHop = stream["finalmask"]?["quicParams"]?["udpHop"];
        udpHop.Should().NotBeNull();
        udpHop!["ports"]!.GetValue<string>().Should().Be("20000-40000");
        udpHop["interval"]!.GetValue<string>().Should().Be("30");
        // The pre-26 shape is ignored by Xray 26 and must not be relied on.
        stream["hysteriaSettings"]?["udphop"].Should().BeNull();
    }

    [Fact]
    public void Xray_WithoutMport_HasNoHop()
    {
        var stream = HysteriaStream(PlainLink);

        stream["finalmask"]?["quicParams"]?["udpHop"].Should().BeNull();
    }
}
