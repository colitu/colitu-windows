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

    [Fact]
    public void RuleSetFilesShipWithTheApp()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "srss-dosyalari");
        File.Exists(Path.Combine(dir, "geosite-category-ru.srs")).Should().BeTrue();
        File.Exists(Path.Combine(dir, "geoip-ru.srs")).Should().BeTrue();
    }
}
