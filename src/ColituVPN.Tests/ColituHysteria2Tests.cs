using System.Text.Json;
using AwesomeAssertions;
using v2rayN.Services;
using Xunit;

namespace v2rayN.Tests;

public class ColituHysteria2Tests
{
    [Fact]
    public void Hysteria2Payload_WithAHopRange_CarriesItAsMport()
    {
        var payload = JsonDocument.Parse("""{"schema_version":1,"protocol":"hysteria2","endpoint":{"host":"vpn.example.com","port":8443},"credentials":{"password":"p"},"transport":{"type":"hysteria","hop_ports":"20000-40000","hop_interval":30},"security":{"type":"tls","server_name":"vpn.example.com"}}""").RootElement.Clone();

        var link = ColituShareLinkBuilder.Build(payload, "Colitu ee", "203.0.113.7");

        link.Should().Be("hysteria2://p@203.0.113.7:8443?sni=vpn.example.com&insecure=0&mport=20000-40000#Colitu%20ee");
    }

    [Theory]
    [InlineData("20000-40000", "20000-40000")]
    [InlineData(" 20000-40000 ", "20000-40000")]
    [InlineData("40000-20000", null)]
    [InlineData("0-100", null)]
    [InlineData("20000-70000", null)]
    [InlineData("20000", null)]
    [InlineData("20000-40000,50000", null)]
    [InlineData("a-b", null)]
    [InlineData(null, null)]
    public void HopRange_AcceptsOnlyAPlainPortRange(string? value, string? expected)
    {
        ColituShareLinkBuilder.HopRange(value).Should().Be(expected);
    }

    [Fact]
    public void Hysteria2Payload_BecomesAHysteria2Link()
    {
        var payload = JsonDocument.Parse("""{"schema_version":1,"protocol":"hysteria2","endpoint":{"host":"vpn.example.com","port":8443},"credentials":{"password":"p@ss word"},"transport":{"type":"hysteria"},"security":{"type":"tls","server_name":"vpn.example.com"}}""").RootElement.Clone();

        var link = ColituShareLinkBuilder.Build(payload, "Colitu ee", "203.0.113.7");

        link.Should().Be("hysteria2://p%40ss%20word@203.0.113.7:8443?sni=vpn.example.com&insecure=0#Colitu%20ee");
    }

    [Fact]
    public void Hysteria2Payload_WithoutServerName_IsRejected()
    {
        var payload = JsonDocument.Parse("""{"schema_version":1,"protocol":"hysteria2","endpoint":{"host":"vpn.example.com","port":8443},"credentials":{"password":"p"},"transport":{"type":"hysteria"},"security":{"type":"tls"}}""").RootElement.Clone();

        ColituShareLinkBuilder.Build(payload, "x").Should().BeNull();
    }

    [Fact]
    public void Hysteria2Link_IsParsedByTheCoreLibrary()
    {
        var item = ServiceLib.Handler.Fmt.Hysteria2Fmt.Resolve("hysteria2://secret@203.0.113.7:8443?sni=vpn.example.com&insecure=0#Colitu", out _);

        item.Should().NotBeNull();
        item!.ConfigType.Should().Be(ServiceLib.Enums.EConfigType.Hysteria2);
        item.Address.Should().Be("203.0.113.7");
        item.Port.Should().Be(8443);
        item.Password.Should().Be("secret");
        item.Sni.Should().Be("vpn.example.com");
    }

    [Fact]
    public void Hysteria2Link_WithMport_HopsPorts_WithoutMport_DoesNot()
    {
        var hopping = ServiceLib.Handler.Fmt.Hysteria2Fmt.Resolve("hysteria2://secret@203.0.113.7:8443?sni=vpn.example.com&insecure=0&mport=20000-40000#Colitu", out _)!;
        var plain = ServiceLib.Handler.Fmt.Hysteria2Fmt.Resolve("hysteria2://secret@203.0.113.7:8443?sni=vpn.example.com&insecure=0#Colitu", out _)!;

        ColituVpnService.UsesPortHopping(hopping).Should().BeTrue();
        ColituVpnService.UsesPortHopping(plain).Should().BeFalse();
    }

    [Fact]
    public void KillSwitch_WithPortHopping_LetsTheCoresReachTheWholeRange()
    {
        // The service pins single ports; hops go to any port of 20000-40000.
        var servers = new[] { ("203.0.113.7", 8443, "hysteria2"), ("203.0.113.7", 443, "vless-reality") };

        ColituVpnService.BuildKillSwitchArm(new ColituVpnPreferences(), true, servers, false, [], false, portHopping: true)
            .CoreAccess.Should().Be(Colitu.KillSwitch.KsProtocol.CoreAccessFull);
        ColituVpnService.BuildKillSwitchArm(new ColituVpnPreferences(), true, servers, false, [], false)
            .CoreAccess.Should().Be(Colitu.KillSwitch.KsProtocol.CoreAccessEndpoints);
    }

    [Fact]
    public void MidSessionStall_SwitchesTransportAtMostOncePerMinute()
    {
        var now = DateTimeOffset.UtcNow;

        ColituVpnService.ShouldSwitchTransport(null, now).Should().BeTrue();
        ColituVpnService.ShouldSwitchTransport(now.AddSeconds(-30), now).Should().BeFalse();
        ColituVpnService.ShouldSwitchTransport(now.AddSeconds(-60), now).Should().BeTrue();
    }

    [Fact]
    public void DemotedHysteria2_GoesBehindTheOtherTransports()
    {
        var order = new[] { ("hysteria2", -1, true), ("vless-reality", 80, false), ("trojan", 40, false) }
            .OrderBy(t => ColituVpnService.TransportRank(t.Item1, t.Item2, t.Item3))
            .Select(t => t.Item1);

        order.Should().Equal("vless-reality", "trojan", "hysteria2");
    }
}
