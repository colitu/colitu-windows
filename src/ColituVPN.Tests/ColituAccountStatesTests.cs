using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using v2rayN.Services;
using Xunit;

namespace v2rayN.Tests;

/// <summary>Windows 2.6.0: 2FA code step, paused devices, trial-end banner, TUN default.</summary>
public class ColituAccountStatesTests
{
    // ── 2FA ───────────────────────────────────────────────────────────────
    [Fact]
    public void MfaChallenge_IsReadFromThe403()
    {
        var challenge = ColituMfa.ParseChallenge("""{"error":{"code":"MFA_REQUIRED"},"mfa_token":"tok-1","mfa_expires_in":300}""");
        challenge.Should().NotBeNull();
        challenge!.Token.Should().Be("tok-1");
        challenge.ExpiresIn.Should().Be(300);
    }

    [Theory]
    [InlineData("""{"error":{"code":"MFA_REQUIRED_UPDATE_APP"}}""")]
    [InlineData("""{"error":{"code":"MFA_REQUIRED"}}""")]
    [InlineData("""{"error":{"code":"MFA_REQUIRED"},"mfa_token":""}""")]
    [InlineData("""{"error":{"code":"FORBIDDEN"},"mfa_token":"x"}""")]
    [InlineData("<html>403</html>")]
    public void OtherForbiddenAnswers_AreNoChallenge(string body)
    {
        ColituMfa.ParseChallenge(body).Should().BeNull();
    }

    [Theory]
    [InlineData("123456", false, "123456")]
    [InlineData("123 456", false, "123456")]
    [InlineData("123-456", false, "123456")]
    [InlineData(" 123456 ", false, "123456")]
    [InlineData("12345", false, null)]
    [InlineData("1234567", false, null)]
    [InlineData("12a456", false, null)]
    [InlineData("abcd-efgh-ijkl", true, "abcd-efgh-ijkl")]
    [InlineData("ABCD EFGH", true, "ABCDEFGH")]
    [InlineData("abc", true, null)]
    [InlineData("abcd;drop", true, null)]
    public void MfaCodes_AreNormalized(string input, bool recovery, string? expected)
    {
        ColituMfa.NormalizeCode(input, recovery).Should().Be(expected);
    }

    [Fact]
    public void PastedCodes_KeepTheirSixDigits()
    {
        ColituMfa.DigitsOfPaste("Your code: 123 456.").Should().Be("123456");
        ColituMfa.AttemptsLeft("""{"error":{"code":"MFA_INVALID_CODE"},"attempts_left":2}""").Should().Be(2);
        ColituMfa.AttemptsLeft("""{"error":{"code":"MFA_INVALID_CODE"}}""").Should().BeNull();
    }

    [Theory]
    [InlineData("MFA_INVALID_CODE", "mfa.err.invalid")]
    [InlineData("MFA_TOKEN_EXPIRED", "mfa.err.expired")]
    [InlineData("MFA_REQUIRED_UPDATE_APP", "mfa.err.update")]
    [InlineData("RATE_LIMITED", "err.rateLimited")]
    public void MfaErrors_HaveTheirOwnMessages(string code, string key)
    {
        ColituAuthService.FriendlyMessage(code, null, HttpStatusCode.Unauthorized).Should().Be(Loc.I[key]);
    }

    // ── Paused device ─────────────────────────────────────────────────────
    [Fact]
    public void DeviceOverLimit_IsParsedTolerantly()
    {
        var info = ColituDeviceOverLimit.Parse("""
            {"error":{"code":"DEVICE_OVER_LIMIT","message":"paused"},"device_limit":"1",
             "active_devices":[{"id":"d1","name":"Phone\u0007","platform":"android","last_seen_at":"2026-10-06T10:00:00Z"}, 5, {"id":"d2"}]}
            """);
        info.Should().NotBeNull();
        info!.DeviceLimit.Should().Be(1);
        info.ActiveDevices.Should().HaveCount(2);
        info.ActiveDevices[0].Name.Should().Be("Phone");
        ColituDeviceOverLimit.Parse("not json").Should().BeNull();
    }

    [Fact]
    public void PausedText_NamesTheLimitAndTheActiveDevice()
    {
        var previous = Loc.I.Language;
        try
        {
            Loc.I.SetLanguage("en");
            var info = new ColituDeviceOverLimit
            {
                DeviceLimit = 1,
                ActiveDevices = [new ColituActiveDevice { Name = "Phone", LastSeenAt = "2026-10-06T10:00:00Z" }]
            };
            info.Describe(Loc.I).Should().Be("Your plan allows 1 device. Active: Phone.");
            new ColituDeviceOverLimit().Describe(Loc.I).Should().Be(Loc.I["paused.bodyNoLimit"]);
        }
        finally
        {
            Loc.I.SetLanguage(previous);
        }
    }

    [Fact]
    public void FindDevicePaused_LooksThroughWrappers()
    {
        var api = new ColituApiException(HttpStatusCode.Forbidden, "paused", "DEVICE_OVER_LIMIT") { OverLimit = new ColituDeviceOverLimit { DeviceLimit = 2 } };
        ColituVpnService.FindDevicePaused(new ColituConnectException("x", api))!.DeviceLimit.Should().Be(2);
        ColituVpnService.FindDevicePaused(new ColituConnectException("x")).Should().BeNull();
    }

    // ── Trial banner ──────────────────────────────────────────────────────
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private static ColituSubscription Trial(double daysLeft, int? devices = 1, int? nextLimit = 1, long? nextBytes = 10L << 30, string? nextPlan = "free") => new()
    {
        Status = "trialing",
        Active = true,
        EndsAt = Now.AddDays(daysLeft).ToString("O"),
        NextPlan = nextPlan,
        NextDeviceLimit = nextLimit,
        NextPlanTrafficBytes = nextBytes,
        DeviceCount = devices
    };

    private static string? Banner(ColituSubscription subscription, string? dismissedOn = null, string language = "en")
    {
        var previous = Loc.I.Language;
        try
        {
            Loc.I.SetLanguage(language);
            return ColituTrial.BannerText(subscription, Now, dismissedOn, Loc.I);
        }
        finally
        {
            Loc.I.SetLanguage(previous);
        }
    }

    [Fact]
    public void Banner_WithMoreDevicesThanTheFreePlan_SaysWhichStaysActive()
    {
        Banner(Trial(2.5, devices: 3)).Should().Be(
            "Your trial ends in 3 days. You’ll move to the free plan (10 GB a month, 1 device); the device you used most recently stays active, the others are paused (not signed out).");
    }

    [Fact]
    public void Banner_IsSimpler_WhenTheDevicesFit()
    {
        Banner(Trial(0.5, devices: 1)).Should().Be("Your trial ends in 1 day. You’ll move to the free plan (10 GB a month, 1 device).");
    }

    [Fact]
    public void Banner_LeavesOutNumbersTheApiDidNotSend()
    {
        Banner(Trial(1.2, devices: null, nextLimit: null, nextBytes: null)).Should().Be("Your trial ends in 2 days. You’ll move to the free plan.");
    }

    [Fact]
    public void Banner_OnlyInTheLastThreeDays_AndOncePerDay()
    {
        Banner(Trial(3.5)).Should().BeNull();
        Banner(Trial(-0.1)).Should().BeNull();
        Banner(Trial(1), dismissedOn: Now.ToLocalTime().ToString("yyyy-MM-dd")).Should().BeNull();
        Banner(Trial(1), dismissedOn: Now.AddDays(-1).ToLocalTime().ToString("yyyy-MM-dd")).Should().NotBeNull();
        Banner(new ColituSubscription { Status = "active", EndsAt = Now.AddDays(1).ToString("O") }).Should().BeNull();
    }

    [Fact]
    public void Banner_IsTranslated()
    {
        Banner(Trial(2.5, devices: 3), language: "ru").Should().StartWith("Пробный период закончится через 3 дня.");
        Banner(Trial(2.5, devices: 3), language: "tr").Should().StartWith("Deneme sürenizin bitmesine 3 gün kaldı.");
    }

    [Fact]
    public void TrialFields_AreParsedTolerantly()
    {
        var json = """
            {"status":"trialing","plan":"trial","expires_at":"2026-10-08T00:00:00Z","ends_at":"2026-10-08T00:00:00Z",
             "next_plan":{"name":"free","traffic_limit_bytes":10737418240,"device_limit":1},"next_device_limit":"1",
             "devices":{"active":2,"suspended":0,"registered":3,"limit":5}}
            """;
        var entitlement = JsonSerializer.Deserialize<ColituEntitlementDto>(json)!;
        var subscription = ColituAuthService.MapSubscription(entitlement);
        subscription.EndsAt.Should().Be("2026-10-08T00:00:00Z");
        subscription.NextPlan.Should().Be("free");
        subscription.NextPlanTrafficBytes.Should().Be(10737418240);
        subscription.NextDeviceLimit.Should().Be(1);
        subscription.DeviceCount.Should().Be(3);
        subscription.DevicesUsed.Should().Be(2);

        var plain = ColituAuthService.MapSubscription(JsonSerializer.Deserialize<ColituEntitlementDto>("""{"status":"active","next_plan":null,"next_device_limit":{"x":1}}""")!);
        plain.NextPlan.Should().BeNull();
        plain.NextDeviceLimit.Should().BeNull();
    }

    // ── TUN by default ────────────────────────────────────────────────────
    [Fact]
    public void NewInstalls_StartInTunMode()
    {
        new ColituVpnPreferences().Normalize().IsTunMode.Should().BeTrue();
    }

    [Theory]
    [InlineData("""{"Preferences":{"ConnectionMode":"proxy"}}""", true)]
    [InlineData("""{"Preferences":{"KillSwitchEnabled":true}}""", false)]
    [InlineData("""{"Preferences":{"ConnectionMode":""}}""", false)]
    [InlineData("""{}""", false)]
    [InlineData("garbage", false)]
    public void SavedStates_WithoutAMode_StayInProxyMode(string json, bool named)
    {
        // States from before 2.6.0 that do not name a mode were proxy mode; LoadState keeps them there.
        ColituVpnService.SavedConnectionMode(json).Should().Be(named);
    }
}
