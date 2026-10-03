using System.Globalization;
using System.Text.Json;
using System.Windows;
using AwesomeAssertions;
using v2rayN.Converters;
using v2rayN.Services;
using Xunit;

namespace v2rayN.Tests;

public class ColituClientTests
{
    [Fact]
    public void ActiveEntitlement_WithFutureExpiry_IsActive()
    {
        var subscription = ColituAuthService.MapSubscription(new ColituEntitlementDto
        {
            Status = "active",
            Plan = "Colitu 12",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30).ToString("O", CultureInfo.InvariantCulture),
            DeviceLimit = 3
        });

        subscription.Active.Should().BeTrue();
        subscription.Status.Should().Be("active");
        subscription.DeviceLimit.Should().Be(3);
        subscription.Unlimited.Should().BeTrue();
    }

    [Fact]
    public void ActiveEntitlement_PastItsExpiry_IsTreatedAsExpired()
    {
        var subscription = ColituAuthService.MapSubscription(new ColituEntitlementDto
        {
            Status = "trialing",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O", CultureInfo.InvariantCulture)
        });

        subscription.Active.Should().BeFalse();
        subscription.Status.Should().Be("expired");
    }

    [Fact]
    public void MissingEntitlement_IsInactive()
    {
        var subscription = ColituAuthService.MapSubscription(null);

        subscription.Active.Should().BeFalse();
        subscription.Status.Should().Be("inactive");
    }

    [Theory]
    [InlineData("2.1.0", 210)]
    [InlineData("2.0.0", 200)]
    [InlineData("3.0.5", 305)]
    [InlineData("nonsense", 0)]
    public void VersionCode_MatchesTheReleaseManifestScheme(string version, int expected)
    {
        ColituAuthService.VersionCode(version).Should().Be(expected);
    }

    [Fact]
    public void ClientVersion_IsTheBuildVersion()
    {
        Version.TryParse(ColituAuthService.ClientVersion, out _).Should().BeTrue();
    }

    [Fact]
    public void ShadowsocksPayload_BecomesAnSsLink()
    {
        var payload = Payload("""{"schema_version":1,"protocol":"shadowsocks","endpoint":{"host":"203.0.113.9","port":8388},"credentials":{"method":"aes-128-gcm","password":"p@ss"},"transport":{"type":"tcp"},"security":{"type":"none"}}""");

        var link = ColituShareLinkBuilder.Build(payload, "Colitu nl");

        link.Should().StartWith("ss://").And.Contain("@203.0.113.9:8388").And.EndWith("#Colitu%20nl");
    }

    [Fact]
    public void RealityPayload_BecomesAVlessLinkWithVision()
    {
        var payload = Payload("""{"schema_version":1,"protocol":"vless-reality","endpoint":{"host":"nl.colitu.net","port":443},"credentials":{"uuid":"0b2c"},"transport":{"type":"tcp"},"security":{"type":"reality","server_name":"www.example.com","public_key":"PBK","short_id":"ab","fingerprint":"chrome"}}""");

        var link = ColituShareLinkBuilder.Build(payload, "x");

        link.Should().StartWith("vless://0b2c@nl.colitu.net:443?")
            .And.Contain("flow=xtls-rprx-vision")
            .And.Contain("security=reality")
            .And.Contain("pbk=PBK")
            .And.Contain("sid=ab");
    }

    [Fact]
    public void UnsupportedTransport_IsSkipped()
    {
        var payload = Payload("""{"schema_version":1,"protocol":"trojan","endpoint":{"host":"h","port":443},"credentials":{"password":"x"},"transport":{"type":"hysteria"},"security":{"server_name":"h"}}""");

        ColituShareLinkBuilder.Build(payload, "x").Should().BeNull();
    }

    [Theory]
    [InlineData(46, 23)]
    [InlineData(32, 16)]
    [InlineData(double.NaN, 0)]
    public void PillRadius_IsHalfTheHeight(double height, double radius)
    {
        var result = (CornerRadius)new PillRadiusConverter().Convert(height, typeof(CornerRadius), null!, CultureInfo.InvariantCulture);

        result.TopLeft.Should().Be(radius);
        result.BottomRight.Should().Be(radius);
    }

    [Fact]
    public void NetworkFailures_AreRecognised()
    {
        ColituVpnService.IsNetworkFailure(new System.Net.Http.HttpRequestException("down")).Should().BeTrue();
        ColituVpnService.IsNetworkFailure(new TaskCanceledException()).Should().BeTrue();
        ColituVpnService.IsNetworkFailure(new ColituApiException(System.Net.HttpStatusCode.BadGateway, "bad gateway")).Should().BeTrue();
        ColituVpnService.IsNetworkFailure(new ColituApiException(System.Net.HttpStatusCode.Unauthorized, "no", "AUTH_INVALID_CREDENTIALS")).Should().BeFalse();
        ColituVpnService.IsNetworkFailure(new InvalidOperationException("x")).Should().BeFalse();
    }

    [Fact]
    public void PlanErrors_AreRecognised()
    {
        ColituVpnService.IsPlanError(new ColituApiException(System.Net.HttpStatusCode.Forbidden, "x", "ENTITLEMENT_EXPIRED")).Should().BeTrue();
        ColituVpnService.IsPlanError(new ColituApiException(System.Net.HttpStatusCode.Forbidden, "x", "DEVICE_LIMIT_REACHED")).Should().BeFalse();
    }

    private static JsonElement Payload(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
