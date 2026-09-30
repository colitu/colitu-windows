using System.Text.Json;
using AwesomeAssertions;
using v2rayN.Services;
using Xunit;

namespace v2rayN.Tests;

public class ColituHysteria2Tests
{
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
}
