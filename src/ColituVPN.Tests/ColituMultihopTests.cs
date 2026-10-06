using System.Text.Json;
using AwesomeAssertions;
using v2rayN.Services;
using Xunit;

namespace v2rayN.Tests;

/// <summary>Panel contract of 2026-10-06, sections 3 and 4: multihop routes and the rotating exit IP.</summary>
public class ColituMultihopTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private const string ServerList = """
        {
          "servers": [
            {"id":"node-fi","name":"Helsinki","country":"FI","city":"Helsinki","status":"online","load":"low","latency_host":"fi.example","latency_port":443}
          ],
          "multihop": [
            {"id":"route-1","route_slug":"fi-de","multihop":true,"name":"Helsinki → Frankfurt",
             "country":"DE","city":"Frankfurt",
             "entry":{"node_id":"node-fi","name":"Helsinki","country":"FI","city":"Helsinki"},
             "exit":{"node_id":"node-de","name":"Frankfurt","country":"de","city":"Frankfurt"},
             "status":"online","load":"medium","protocols":["vless-reality","vless-xhttp"],
             "latency_host":"fi.example","latency_port":443,"latency_note":"entry_only_estimate"},
            {"id":"route-2","multihop":true,"name":"Broken","status":"online"},
            {"id":"","multihop":true,"entry":{"country":"SE"},"exit":{"country":"NL"}}
          ]
        }
        """;

    [Fact]
    public void ServerList_WithMultihop_ParsesRoutes()
    {
        var list = JsonSerializer.Deserialize<ColituServerListDto>(ServerList, Json)!;

        var routes = ColituApiClient.MapMultihop(list.Multihop);

        // A route without both ends or without an id is skipped.
        routes.Should().ContainSingle();
        var route = routes[0];
        route.IsMultihop.Should().BeTrue();
        route.Id.Should().Be("route-1");
        route.RouteSlug.Should().Be("fi-de");
        route.Entry!.Country.Should().Be("FI");
        route.Entry.NodeId.Should().Be("node-fi");
        route.Exit!.Country.Should().Be("DE");
        route.Exit.Label.Should().Be("Frankfurt");
        // The list flag and country filters follow the exit; the ping host is the entry's.
        route.CountryCode.Should().Be("DE");
        route.Host.Should().Be("fi.example");
        route.Port.Should().Be(443);
        route.Available.Should().BeTrue();
        route.Name.Should().Be("Helsinki → Frankfurt");
    }

    [Fact]
    public void ServerList_FromAnOlderPanel_HasNoMultihop()
    {
        var list = JsonSerializer.Deserialize<ColituServerListDto>("""{"servers":[{"id":"a","country":"FI","status":"online"}]}""", Json)!;

        list.Multihop.Should().BeNull();
        ColituApiClient.MapMultihop(list.Multihop).Should().BeEmpty();
    }

    [Fact]
    public void ServerList_StillReadsNormalServers()
    {
        var list = JsonSerializer.Deserialize<ColituServerListDto>(ServerList, Json)!;

        list.Servers.Should().ContainSingle().Which.Id.Should().Be("node-fi");
    }

    [Fact]
    public void RouteConfigEnvelope_CarriesTheRouteEnds()
    {
        const string envelope = """
            {"revision":7,"server":{"id":"route-1","name":"Helsinki → Frankfurt","country":"DE","multihop":true,"route_slug":"fi-de",
              "entry":{"node_id":"n1","country":"FI","city":"Helsinki"},"exit":{"node_id":"n2","country":"DE","city":"Frankfurt"}},
             "profile":{"format":"xray-mobile-v1","payload":{}},"candidates":[]}
            """;

        var parsed = JsonSerializer.Deserialize<ColituConfigEnvelopeDto>(envelope, Json)!;

        parsed.Server!.Multihop.Should().BeTrue();
        parsed.Server.RouteSlug.Should().Be("fi-de");
        parsed.Server.Entry!.Country.Should().Be("FI");
        parsed.Server.Exit!.City.Should().Be("Frankfurt");
    }

    [Fact]
    public void RouteConfigPath_EscapesTheId()
    {
        ColituApiClient.ConfigPathForRoute("abc-123").Should().Be("/multihop/routes/abc-123/config");
        ColituApiClient.ConfigPathForRoute("a/b?c").Should().Be("/multihop/routes/a%2Fb%3Fc/config");
    }

    [Fact]
    public void RestrictToVless_DropsHysteria2AndTheOtherTransports()
    {
        var config = new ColituVpnConfigResponse
        {
            ServerId = "n1",
            ProtocolType = "hysteria2",
            Candidates =
            [
                new ColituConfigCandidate { Protocol = "hysteria2", ShareLink = "hysteria2://x" },
                new ColituConfigCandidate { Protocol = "vless-reality", ShareLink = "vless://a" },
                new ColituConfigCandidate { Protocol = "trojan", ShareLink = "trojan://t" },
                new ColituConfigCandidate { Protocol = "vless-xhttp", ShareLink = "vless://b" }
            ],
            RawConfig = "hysteria2://x\nvless://a\ntrojan://t\nvless://b"
        };

        var restricted = ColituApiClient.RestrictToVless(config)!;

        restricted.Candidates.Select(item => item.Protocol).Should().Equal("vless-reality", "vless-xhttp");
        restricted.ProtocolType.Should().Be("vless-reality");
        restricted.RawConfig.Should().NotContain("hysteria2").And.NotContain("trojan");
        restricted.ServerId.Should().Be("n1");
    }

    [Fact]
    public void RestrictToVless_KeepsAVlessOnlyConfigAsIs_AndRefusesOneWithoutVless()
    {
        var vlessOnly = new ColituVpnConfigResponse
        {
            Candidates = [new ColituConfigCandidate { Protocol = "vless-reality", ShareLink = "vless://a" }]
        };
        ColituApiClient.RestrictToVless(vlessOnly).Should().BeSameAs(vlessOnly);

        var none = new ColituVpnConfigResponse
        {
            Candidates = [new ColituConfigCandidate { Protocol = "hysteria2", ShareLink = "hysteria2://x" }]
        };
        ColituApiClient.RestrictToVless(none).Should().BeNull();
    }

    // ── Rotation ────────────────────────────────────────────────────────────

    private const string RotationJson = """
        {"rotation":{"interval_seconds":600,"countries":["nl","DE","de","xx1"],"intervals":[300,600,1800],
         "available_countries":[{"country":"DE","in_default":true,"exits":1},{"country":"NL","in_default":true,"exits":2},{"country":"RU","in_default":false,"exits":1},{"country":"SE","in_default":true,"exits":0}],
         "protocols":["vless-reality","vless-xhttp"],"changes_exit_country":true}}
        """;

    [Fact]
    public void Rotation_ParsesThePreference()
    {
        var dto = JsonSerializer.Deserialize<ColituRotationEnvelopeDto>(RotationJson, Json)!;

        var preference = ColituRotation.Map(dto.Rotation);

        preference.IntervalSeconds.Should().Be(600);
        preference.Active.Should().BeTrue();
        preference.Countries.Should().Equal("DE", "NL");
        preference.Intervals.Should().Equal(300, 600, 1800);
        preference.Protocols.Should().Equal("vless-reality", "vless-xhttp");
        preference.ChangesExitCountry.Should().BeTrue();
        preference.AvailableCountries.Select(item => item.Country).Should().Equal("DE", "NL", "RU", "SE");
        preference.AvailableCountries.Single(item => item.Country == "RU").InDefault.Should().BeFalse();
    }

    [Fact]
    public void Rotation_ADefaultResponse_IsOffWithTheDefaultSet()
    {
        var dto = JsonSerializer.Deserialize<ColituRotationEnvelopeDto>(RotationJson.Replace("\"interval_seconds\":600", "\"interval_seconds\":0").Replace("[\"nl\",\"DE\",\"de\",\"xx1\"]", "[]"), Json)!;

        var preference = ColituRotation.Map(dto.Rotation);

        preference.Active.Should().BeFalse();
        // No chosen countries: the checklist shows the default set, without Russia and without exitless countries.
        ColituRotation.SelectedCountries(preference).Should().Equal("DE", "NL");
    }

    [Fact]
    public void Rotation_AnUnknownIntervalFromThePanel_ReadsAsOff()
    {
        var dto = new ColituRotationDto { IntervalSeconds = 42 };

        ColituRotation.Map(dto).IntervalSeconds.Should().Be(0);
        ColituRotation.Map(null).Active.Should().BeFalse();
    }

    [Fact]
    public void RotationStatus_Parses()
    {
        const string json = """
            {"status":{"active":true,"reason":"","interval_seconds":600,
             "entry":{"node_id":"e","name":"Helsinki","country":"FI","city":"Helsinki"},
             "current_exit":{"node_id":"x","name":"Frankfurt","country":"de","city":"Frankfurt"},
             "next_exit":{"node_id":"y","country":"NL"},
             "window_started_at":"2026-10-06T10:00:00Z","next_change_at":"2026-10-06T10:10:00Z",
             "exit_set":["x","y"],"protocols":["vless-reality"]}}
            """;

        var status = ColituRotation.MapStatus(JsonSerializer.Deserialize<ColituRotationStatusEnvelopeDto>(json, Json)!.Status)!;

        status.Active.Should().BeTrue();
        status.IntervalSeconds.Should().Be(600);
        status.Entry!.Country.Should().Be("FI");
        status.CurrentExit!.Country.Should().Be("DE");
        status.CurrentExit.Label.Should().Be("Frankfurt");
        status.NextExit!.Country.Should().Be("NL");
        status.NextChangeAt.Should().Be(new DateTimeOffset(2026, 10, 6, 10, 10, 0, TimeSpan.Zero));
        status.WindowStartedAt.Should().Be(new DateTimeOffset(2026, 10, 6, 10, 0, 0, TimeSpan.Zero));
        status.Protocols.Should().Equal("vless-reality");
    }

    [Fact]
    public void RotationStatus_Inactive_KeepsTheReason_AndToleratesOddShapes()
    {
        const string json = """{"status":{"active":false,"reason":"not_in_mesh","interval_seconds":300,"entry":"Helsinki","current_exit":null}}""";

        var status = ColituRotation.MapStatus(JsonSerializer.Deserialize<ColituRotationStatusEnvelopeDto>(json, Json)!.Status)!;

        status.Active.Should().BeFalse();
        status.Reason.Should().Be("not_in_mesh");
        status.Entry!.Name.Should().Be("Helsinki");
        status.CurrentExit.Should().BeNull();
        status.NextChangeAt.Should().BeNull();
    }

    private static readonly ColituRotationCountry[] Available =
    [
        new() { Country = "DE", InDefault = true, Exits = 1 },
        new() { Country = "NL", InDefault = true, Exits = 2 },
        new() { Country = "RU", InDefault = false, Exits = 1 },
        new() { Country = "SE", InDefault = true, Exits = 0 }
    ];

    [Theory]
    [InlineData(0, true)]
    [InlineData(300, true)]
    [InlineData(600, true)]
    [InlineData(1800, true)]
    [InlineData(60, false)]
    [InlineData(900, false)]
    [InlineData(-300, false)]
    public void Interval_IsOffOr5Or10Or30Minutes(int seconds, bool valid)
    {
        ColituRotation.IsValidInterval(seconds).Should().Be(valid);
    }

    [Fact]
    public void Validation_NoCountries_MeansTheDefaultSet()
    {
        ColituRotation.Validate(600, [], Available).Should().Be(ColituRotation.Validation.Ok);
        ColituRotation.Validate(600, null, Available).Should().Be(ColituRotation.Validation.Ok);
    }

    [Fact]
    public void Validation_NeedsAtLeastTwoCountriesWithExits()
    {
        ColituRotation.Validate(600, ["DE", "NL"], Available).Should().Be(ColituRotation.Validation.Ok);
        ColituRotation.Validate(600, ["DE", "RU"], Available).Should().Be(ColituRotation.Validation.Ok);
        ColituRotation.Validate(600, ["DE"], Available).Should().Be(ColituRotation.Validation.TooFewCountries);
        // SE has no exits and FR is not offered: neither counts.
        ColituRotation.Validate(600, ["DE", "SE"], Available).Should().Be(ColituRotation.Validation.TooFewCountries);
        ColituRotation.Validate(600, ["de", "FR"], Available).Should().Be(ColituRotation.Validation.TooFewCountries);
    }

    [Fact]
    public void Validation_RejectsAnIntervalTheMenuDoesNotOffer()
    {
        ColituRotation.Validate(123, [], Available).Should().Be(ColituRotation.Validation.InvalidInterval);
        ColituRotation.Validate(0, ["DE"], Available).Should().Be(ColituRotation.Validation.Ok);
    }

    [Fact]
    public void Countries_AreNormalized()
    {
        ColituRotation.NormalizeCountries([" nl ", "DE", "de", "uk", "X", "123", "", "R U"]).Should().Equal("DE", "GB", "NL");
        ColituRotation.NormalizeCountries(null).Should().BeEmpty();
    }

    [Fact]
    public void DefaultSet_ExcludesRussiaAndCountriesWithoutExits()
    {
        ColituRotation.DefaultCountries(Available).Should().Equal("DE", "NL");
    }

    [Fact]
    public void Payload_SendsAnEmptyListWhileTheChecklistIsTheDefaultSet()
    {
        ColituRotation.PayloadCountries(["NL", "DE"], Available).Should().BeEmpty();
        ColituRotation.PayloadCountries(["DE", "NL", "RU"], Available).Should().Equal("DE", "NL", "RU");
        ColituRotation.PayloadCountries(["DE", "RU"], Available).Should().Equal("DE", "RU");
    }

    [Fact]
    public void StatusPoll_WaitsForTheNextChange_ButNeverUnderASixtySecondGap()
    {
        var now = new DateTimeOffset(2026, 10, 6, 10, 0, 0, TimeSpan.Zero);

        // 4 minutes to go: poll right after it.
        ColituRotation.NextPollDelay(now.AddMinutes(4), now).Should().Be(TimeSpan.FromSeconds(241));
        // Due in 5 s, already due, or unknown: not before 60 s.
        ColituRotation.NextPollDelay(now.AddSeconds(5), now).Should().Be(TimeSpan.FromSeconds(60));
        ColituRotation.NextPollDelay(now.AddMinutes(-3), now).Should().Be(TimeSpan.FromSeconds(60));
        ColituRotation.NextPollDelay(null, now).Should().Be(TimeSpan.FromSeconds(60));
        // A far-off value (clock skew) is capped.
        ColituRotation.NextPollDelay(now.AddDays(2), now).Should().BeLessThanOrEqualTo(TimeSpan.FromMinutes(35));
    }

    [Theory]
    [InlineData(247, "4:07")]
    [InlineData(600, "10:00")]
    [InlineData(59, "0:59")]
    [InlineData(0, "0:00")]
    [InlineData(-5, "0:00")]
    public void Countdown_IsMinutesAndSeconds(int seconds, string expected)
    {
        ColituRotation.FormatCountdown(TimeSpan.FromSeconds(seconds)).Should().Be(expected);
    }

    [Fact]
    public void MultihopAndRotationStrings_ExistInEveryLanguage()
    {
        foreach (var key in new[] { "multihop.section", "multihop.ping", "multihop.home", "rotation.title", "rotation.off", "rotation.minutes", "rotation.home", "account.manualConfig" })
        {
            Loc.ValuesOf(key).Should().HaveCount(3, key);
        }
        Loc.ValuesOf("multihop.ping")[2].Should().Be("Estimated · +1 hop");
    }
}
