using System.Text.Json;
using AwesomeAssertions;
using v2rayN.Services;
using Xunit;

namespace v2rayN.Tests;

public class ColituXhttpTests
{
    private const string XhttpPayload = """{"schema_version":1,"protocol":"vless-xhttp","endpoint":{"host":"tr.example.test","port":2053},"credentials":{"uuid":"0b2c"},"transport":{"type":"xhttp","path":"/fixture-path","mode":"auto"},"security":{"type":"reality","server_name":"www.cloudflare.com","public_key":"PBK","short_id":"ab","fingerprint":"chrome"}}""";

    private static JsonElement Payload(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void XhttpPayload_BecomesAVlessXhttpLinkWithoutVision()
    {
        var link = ColituShareLinkBuilder.Build(Payload(XhttpPayload), "x");

        link.Should().StartWith("vless://0b2c@tr.example.test:2053?")
            .And.Contain("type=xhttp")
            .And.Contain("path=%2Ffixture-path")
            .And.Contain("mode=auto")
            .And.Contain("security=reality")
            .And.Contain("pbk=PBK")
            .And.NotContain("flow=");
    }

    [Fact]
    public void XhttpLink_IsParsedByTheCoreLibrary()
    {
        var link = ColituShareLinkBuilder.Build(Payload(XhttpPayload), "Colitu");
        var item = ServiceLib.Handler.Fmt.VLESSFmt.Resolve(link!, out _);

        item.Should().NotBeNull();
        item!.ConfigType.Should().Be(ServiceLib.Enums.EConfigType.VLESS);
        item.Network.Should().Be("xhttp");
        item.GetTransportExtra().Path.Should().Be("/fixture-path");
        item.GetTransportExtra().XhttpMode.Should().Be("auto");
        item.StreamSecurity.Should().Be("reality");
        item.PublicKey.Should().Be("PBK");
    }

    [Fact]
    public void XhttpPayload_WithoutPath_IsRejected()
    {
        var payload = Payload(XhttpPayload.Replace("\"path\":\"/fixture-path\",", ""));

        ColituShareLinkBuilder.Build(payload, "x").Should().BeNull();
    }

    [Fact]
    public void XhttpTransport_OnlyCarriesVlessXhttp()
    {
        var payload = Payload(XhttpPayload.Replace("\"protocol\":\"vless-xhttp\"", "\"protocol\":\"vless-reality\""));

        ColituShareLinkBuilder.Build(payload, "x").Should().BeNull();
    }

    [Theory]
    [InlineData("hysteria2")]
    [InlineData("vless-reality")]
    [InlineData("vless-xhttp")]
    [InlineData("trojan")]
    [InlineData("shadowsocks")]
    public void Screens_ShowColituNamesInsteadOfProtocolNames(string protocol)
    {
        ColituTransportNames.Of(protocol, Loc.I).Should().Be(Loc.I[$"transport.{protocol}"]);
        foreach (var name in Loc.ValuesOf($"transport.{protocol}"))
        {
            name.Should().NotBeNullOrWhiteSpace();
            foreach (var technical in new[] { "Hysteria", "VLESS", "Reality", "XHTTP", "Trojan", "Shadowsocks" })
            {
                name.Should().NotContainEquivalentOf(technical, protocol);
            }
        }
    }
}
