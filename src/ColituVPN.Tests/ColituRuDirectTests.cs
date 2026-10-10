using System.IO;
using AwesomeAssertions;
using ServiceLib;
using v2rayN.Services;
using Xunit;

namespace v2rayN.Tests;

public class ColituRuDirectTests
{
    [Fact]
    public void RussianDomainsAndIps_GoDirectWhateverTheSettings()
    {
        foreach (var preferences in new[]
                 {
                     new ColituVpnPreferences(),
                     new ColituVpnPreferences(DnsLeakProtectionEnabled: false, AdBlockEnabled: true, ConnectionMode: ColituConnectionModes.Tun),
                 })
        {
            var rules = ColituVpnService.BuildColituRoutingRules(preferences);

            var domain = rules.Single(r => r.Id == "colitu-ru-direct-domain");
            domain.Enabled.Should().BeTrue();
            domain.OutboundTag.Should().Be(Global.DirectTag);
            domain.Domain.Should().Equal("geosite:category-ru");

            var ip = rules.Single(r => r.Id == "colitu-ru-direct-ip");
            ip.Enabled.Should().BeTrue();
            ip.OutboundTag.Should().Be(Global.DirectTag);
            ip.Ip.Should().Equal("geoip:ru");

            // DNS protection and ad blocking stay in front of them.
            rules.FindIndex(r => r.Id == "colitu-dns-protection").Should().BeLessThan(rules.IndexOf(domain));
            rules.FindIndex(r => r.Id == "colitu-ad-block").Should().BeLessThan(rules.IndexOf(domain));
        }
    }

    [Theory]
    [InlineData("RU")]
    [InlineData("ru")]
    public void RussianServer_KeepsRussianSitesInTheTunnel(string country)
    {
        var rules = ColituVpnService.BuildColituRoutingRules(new ColituVpnPreferences(), country);

        rules.Single(r => r.Id == "colitu-ru-direct-domain").Enabled.Should().BeFalse();
        rules.Single(r => r.Id == "colitu-ru-direct-ip").Enabled.Should().BeFalse();
    }

    [Theory]
    [InlineData("DE")]
    [InlineData(null)]
    public void OtherServers_SendRussianSitesDirect(string? country)
    {
        var rules = ColituVpnService.BuildColituRoutingRules(new ColituVpnPreferences(), country);

        rules.Single(r => r.Id == "colitu-ru-direct-domain").Enabled.Should().BeTrue();
        rules.Single(r => r.Id == "colitu-ru-direct-ip").Enabled.Should().BeTrue();
    }

    [Theory]
    [InlineData("DE")]
    [InlineData("RU")]
    [InlineData(null)]
    public void PrivacyMode_SendsEverythingThroughTheTunnel(string? country)
    {
        var rules = ColituVpnService.BuildColituRoutingRules(new ColituVpnPreferences(PrivacyModeEnabled: true), country);

        rules.Single(r => r.Id == "colitu-ru-direct-domain").Enabled.Should().BeFalse();
        rules.Single(r => r.Id == "colitu-ru-direct-ip").Enabled.Should().BeFalse();
        // Nothing else on the internet may leave outside the tunnel either; only the local network,
        // which the server cannot reach.
        rules.Where(r => r.Enabled && r.OutboundTag == Global.DirectTag).Select(r => r.Id).Should().Equal(ColituVpnService.LanDirectRuleId);
    }

    [Theory]
    [InlineData("DE", false, true)]
    [InlineData(null, false, true)]
    [InlineData(" ru ", false, false)]
    [InlineData("DE", true, false)]
    [InlineData("RU", true, false)]
    public void RussianSitesDirect_OnlyWithoutPrivacyModeAndOutsideRussia(string? country, bool privacyMode, bool expected)
    {
        ColituVpnService.RussianSitesDirect(country, privacyMode).Should().Be(expected);
    }

    [Fact]
    public void PrivacyMode_IsOnForNewInstalls_OffForSavedStatesWithoutTheProperty()
    {
        ColituVpnPreferences.ForNewInstall().PrivacyModeEnabled.Should().BeTrue();
        ColituVpnPreferences.ForNewInstall().RuDirectNoticeShown.Should().BeFalse();
        ColituVpnService.RussianSitesDirect("DE", ColituVpnPreferences.ForNewInstall().PrivacyModeEnabled).Should().BeFalse();
    }

    [Fact]
    public void PrivacyMode_IsOffByDefault_AlsoForStatesSavedByOlderBuilds()
    {
        new ColituVpnPreferences().PrivacyModeEnabled.Should().BeFalse();
        new ColituVpnPreferences().RuDirectNoticeShown.Should().BeFalse();

        var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var old = System.Text.Json.JsonSerializer.Deserialize<ColituVpnPreferences>("{\"KillSwitchEnabled\":true,\"AdBlockEnabled\":true}", options)!;
        old.PrivacyModeEnabled.Should().BeFalse();
        old.RuDirectNoticeShown.Should().BeFalse();

        var saved = System.Text.Json.JsonSerializer.Serialize(new ColituVpnPreferences(PrivacyModeEnabled: true, RuDirectNoticeShown: true), options);
        var loaded = System.Text.Json.JsonSerializer.Deserialize<ColituVpnPreferences>(saved, options)!;
        loaded.PrivacyModeEnabled.Should().BeTrue();
        loaded.RuDirectNoticeShown.Should().BeTrue();
    }

    [Fact]
    public void RuleSetFilesShipWithTheApp()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "srss-dosyalari");
        File.Exists(Path.Combine(dir, "geosite-category-ru.srs")).Should().BeTrue();
        File.Exists(Path.Combine(dir, "geoip-ru.srs")).Should().BeTrue();
    }

    [Fact]
    public void LocalNetwork_GoesDirectAfterDnsUnlessTheKillSwitchBlocksIt()
    {
        var rules = ColituVpnService.BuildColituRoutingRules(new ColituVpnPreferences(PrivacyModeEnabled: true), "DE", tun: true);
        var lan = rules.Single(r => r.Id == ColituVpnService.LanDirectRuleId);
        lan.Enabled.Should().BeTrue();
        lan.OutboundTag.Should().Be(Global.DirectTag);
        lan.Ip.Should().Equal("geoip:private");
        rules.FindIndex(r => r.Id == "colitu-dns-protection").Should().BeLessThan(rules.IndexOf(lan));

        ColituVpnService.BuildColituRoutingRules(new ColituVpnPreferences(KillSwitchEnabled: false, KillSwitchAllowLan: false))
            .Single(r => r.Id == ColituVpnService.LanDirectRuleId).Enabled.Should().BeTrue();
        ColituVpnService.BuildColituRoutingRules(new ColituVpnPreferences(KillSwitchEnabled: true, KillSwitchAllowLan: false))
            .Single(r => r.Id == ColituVpnService.LanDirectRuleId).Enabled.Should().BeFalse();
    }
}
