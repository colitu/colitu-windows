using AwesomeAssertions;
using ServiceLib;
using ServiceLib.Enums;
using v2rayN.Services;
using Xunit;

namespace v2rayN.Tests;

/// <summary>Windows 2.6.0: split tunnelling — validation and the routing rules it produces.</summary>
public class ColituSplitTunnelTests
{
    [Theory]
    [InlineData("example.com", "example.com")]
    [InlineData("  Example.COM. ", "example.com")]
    [InlineData("*.example.com", "example.com")]
    [InlineData(".example.com", "example.com")]
    [InlineData("пример.рф", "xn--e1afmkfd.xn--p1ai")]
    [InlineData("a-b.co.uk", "a-b.co.uk")]
    public void Domains_AreNormalized(string input, string expected)
    {
        ColituSplitTunnel.NormalizeDomain(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("localhost")]
    [InlineData("https://example.com")]
    [InlineData("example.com/path")]
    [InlineData("example.com:443")]
    [InlineData("-bad.example.com")]
    [InlineData("bad-.example.com")]
    [InlineData("ex ample.com")]
    [InlineData("1.2.3.4")]
    [InlineData("geosite:category-ru")]
    [InlineData("regexp:.*")]
    public void BadDomains_AreRejected(string input)
    {
        ColituSplitTunnel.NormalizeDomain(input).Should().BeNull();
    }

    [Theory]
    [InlineData("198.51.100.7", "198.51.100.7/32")]
    [InlineData("198.51.100.0/24", "198.51.100.0/24")]
    [InlineData("2001:db8::/32", "2001:db8::/32")]
    [InlineData("0.0.0.0/0", null)]
    [InlineData("::/0", null)]
    [InlineData("geoip:ru", null)]
    [InlineData("300.1.1.1", null)]
    public void Networks_AreValidated(string input, string? expected)
    {
        ColituSplitTunnel.NormalizeNetwork(input).Should().Be(expected);
    }

    [Fact]
    public void Preferences_DropInvalidEntries_AndDuplicates()
    {
        var preferences = new ColituVpnPreferences(
            SplitTunnelMode: "BYPASS",
            SplitTunnelApps: [@"C:\Apps\game.exe", @"C:\Apps\GAME.exe", "game", @"\\server\x.exe"],
            SplitTunnelDomains: ["Example.com", "example.com", "bad domain"],
            SplitTunnelNetworks: ["198.51.100.1/24", "198.51.100.0/24", "nope"]).Normalize();

        preferences.SplitTunnelMode.Should().Be(ColituSplitTunnelModes.Bypass);
        preferences.SplitTunnelApps.Should().Equal(@"C:\Apps\game.exe");
        preferences.SplitTunnelDomains.Should().Equal("example.com");
        preferences.SplitTunnelNetworks.Should().Equal("198.51.100.0/24");
    }

    private static ColituVpnPreferences Split(string mode) => new(
        SplitTunnelMode: mode,
        SplitTunnelApps: [@"C:\Games\game.exe"],
        SplitTunnelDomains: ["bank.example"],
        SplitTunnelNetworks: ["198.51.100.0/24"],
        PrivacyModeEnabled: true);

    [Fact]
    public void Off_AddsNoRules()
    {
        var rules = ColituVpnService.BuildColituRoutingRules(Split(ColituSplitTunnelModes.Off), "DE", tun: true);
        rules.Should().NotContain(rule => rule.Id != null && rule.Id.StartsWith("colitu-split", StringComparison.Ordinal));
    }

    [Fact]
    public void Bypass_InTunMode_SendsAppsSitesAndAddressesDirect()
    {
        var rules = ColituVpnService.BuildColituRoutingRules(Split(ColituSplitTunnelModes.Bypass), "DE", tun: true);

        var apps = rules.Single(rule => rule.Id == ColituSplitTunnel.RuleAppsId);
        apps.OutboundTag.Should().Be(Global.DirectTag);
        apps.Process.Should().Equal(@"C:\Games\game.exe", "game.exe");
        rules.Single(rule => rule.Id == ColituSplitTunnel.RuleDomainsId).Domain.Should().Equal("domain:bank.example");
        rules.Single(rule => rule.Id == ColituSplitTunnel.RuleNetworksId).Ip.Should().Equal("198.51.100.0/24");
        rules.Should().NotContain(rule => rule.Id == ColituSplitTunnel.RuleRestId);
        // Ad blocking and DNS protection come first; the user's choice before the regional rule.
        rules.FindIndex(rule => rule.Id == "colitu-ad-block").Should().BeLessThan(rules.IndexOf(apps));
        rules.IndexOf(apps).Should().BeLessThan(rules.FindIndex(rule => rule.Id == "colitu-ru-direct-domain"));
    }

    [Fact]
    public void ProxyMode_HasNoAppRules()
    {
        var rules = ColituVpnService.BuildColituRoutingRules(Split(ColituSplitTunnelModes.Bypass), "DE", tun: false);
        rules.Should().NotContain(rule => rule.Id == ColituSplitTunnel.RuleAppsId);
        rules.Should().Contain(rule => rule.Id == ColituSplitTunnel.RuleDomainsId);
    }

    [Fact]
    public void Only_SendsTheSelectedToTheProxy_AndTheRestDirectLast()
    {
        var rules = ColituVpnService.BuildColituRoutingRules(Split(ColituSplitTunnelModes.Only), "DE", tun: true);

        rules.Where(rule => rule.Id is ColituSplitTunnel.RuleAppsId or ColituSplitTunnel.RuleDomainsId or ColituSplitTunnel.RuleNetworksId)
            .Should().OnlyContain(rule => rule.OutboundTag == Global.ProxyTag).And.HaveCount(3);
        var last = rules[^1];
        last.Id.Should().Be(ColituSplitTunnel.RuleRestId);
        last.OutboundTag.Should().Be(Global.DirectTag);
        last.Port.Should().Be("0-65535");
    }

    [Fact]
    public void AppsOnly_InProxyMode_IsNotActive()
    {
        var preferences = new ColituVpnPreferences(SplitTunnelMode: ColituSplitTunnelModes.Only, SplitTunnelApps: [@"C:\Apps\a.exe"]);
        ColituSplitTunnel.IsActive(preferences, tun: false).Should().BeFalse();
        ColituVpnService.BuildColituRoutingRules(preferences, "DE", tun: false).Should().NotContain(rule => rule.Id == ColituSplitTunnel.RuleRestId);
    }

    [Fact]
    public void AppsInTunMode_RunEveryTransportOnSingBox()
    {
        var preferences = Split(ColituSplitTunnelModes.Bypass);
        ColituSplitTunnel.NeedsSingBox(preferences, tun: true).Should().BeTrue();
        ColituSplitTunnel.NeedsSingBox(preferences, tun: false).Should().BeFalse();
        ColituVpnService.CoreTypes(true).Should().Contain(item => item.ConfigType == EConfigType.VLESS && item.CoreType == ECoreType.sing_box);
        ColituVpnService.CoreTypes(false).Should().ContainSingle().Which.ConfigType.Should().Be(EConfigType.Hysteria2);
    }
}
