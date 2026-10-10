using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using AwesomeAssertions;
using v2rayN.Services;
using Xunit;

namespace v2rayN.Tests;

/// <summary>Adaptive Connect 3.0: preferred network hints and the recovery set (panel docs/adaptive-connect-3-apps.md).</summary>
public class ColituAdaptiveConnect3Tests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    // ── 1. Network hints v2: preferred ───────────────────────────────────────
    [Fact]
    public void Preferred_IsParsedLikeBlocked_AndDropsWhatIsBlocked()
    {
        var dto = JsonSerializer.Deserialize<ColituServerListDto>("""
            {"servers":[],"network_hints":{"blocked":[" Hysteria2 ","hysteria2",""],"preferred":["VLESS-Reality"," trojan ","hysteria2","trojan",null],"scope":"network"}}
            """)!;

        ColituNetworkHintsPolicy.ParseProtocols(dto.NetworkHints!.Blocked).Should().Equal("hysteria2");
        ColituNetworkHintsPolicy.ParsePreferred(dto.NetworkHints.Preferred, dto.NetworkHints.Blocked).Should().Equal("vless-reality", "trojan");
    }

    [Fact]
    public void Hints_WithAnEmptyBlockedList_AndOldRecords_ReadAsEmpty()
    {
        var dto = JsonSerializer.Deserialize<ColituServerListDto>("""{"servers":[],"network_hints":{"blocked":[],"preferred":["vless-reality"]}}""")!;
        ColituNetworkHintsPolicy.ParseProtocols(dto.NetworkHints!.Blocked).Should().BeEmpty();
        ColituNetworkHintsPolicy.ParsePreferred(dto.NetworkHints.Preferred, dto.NetworkHints.Blocked).Should().Equal("vless-reality");

        // Old panel / old stored session: no field at all.
        var old = JsonSerializer.Deserialize<ColituServerListDto>("""{"servers":[],"network_hints":{"blocked":["trojan"]}}""")!;
        ColituNetworkHintsPolicy.ParsePreferred(old.NetworkHints!.Preferred, old.NetworkHints.Blocked).Should().BeEmpty();
        JsonSerializer.Deserialize<ColituVpnSession>("{}")!.NetworkHintsPreferred.Should().BeNull();
    }

    private static readonly string[] Offered = ["hysteria2", "vless-reality", "vless-xhttp", "trojan", "shadowsocks"];

    private static List<string>? Start(string[] preferred, string? lastGood, params string[] stalled) =>
        ColituNetworkHintsPolicy.HintedStart(Offered, protocol => protocol, preferred, stalled.Contains, lastGood,
            rest => rest.OrderBy(protocol => ColituVpnService.TransportRank(protocol, 0, stalled.Contains(protocol))));

    [Fact]
    public void HintedStart_PutsThePreferredFirstInTheHintsOrder_ThenTheRestByRank()
    {
        Start(["trojan", "vless-reality"], null).Should().Equal("trojan", "vless-reality", "hysteria2", "vless-xhttp", "shadowsocks");
    }

    [Fact]
    public void HintedStart_SkipsStalledAndNotOfferedPreferred()
    {
        Start(["trojan", "wireguard", "vless-reality"], null, "trojan").Should().Equal("vless-reality", "hysteria2", "vless-xhttp", "shadowsocks", "trojan");
        Start(["wireguard"], null).Should().BeNull();
        Start(["trojan"], null, "trojan").Should().BeNull();
    }

    [Fact]
    public void HintedStart_OwnExperienceBeatsTheHint_AndNoHintMeansNoChange()
    {
        Start(["trojan"], lastGood: "hysteria2").Should().BeNull();
        Start([], null).Should().BeNull();
    }

    // ── 2. Recovery set: parse ───────────────────────────────────────────────
    private static string Envelope(string id, string host = "203.0.113.7", string? grace = "2020-01-01T00:00:00Z") => """
        {"revision":3,"offline_grace_until":GRACE,"server":{"id":"ID","name":"ID","country":"DE"},
         "profile":{"format":"xray-mobile-v1","payload":{"schema_version":1,"protocol":"trojan","endpoint":{"host":"HOST","port":443},"credentials":{"password":"p"},"transport":{"type":"tcp"},"security":{"type":"tls","server_name":"x.example.com"}}},
         "candidates":[]}
        """.Replace("GRACE", grace == null ? "null" : $"\"{grace}\"").Replace("ID", id).Replace("HOST", host);

    private static string Set(string until = "2026-10-24T12:00:00Z", string generated = "2026-10-10T11:00:00Z", params string[] ids) =>
        $$"""{"generated_at":"{{generated}}","recovery_until":"{{until}}","configs":[{{string.Join(",", ids.Select(id => Envelope(id)))}}]}""";

    [Fact]
    public void Parse_ValidSet_KeepsTheServersInOrder()
    {
        var set = ColituRecoverySet.Parse(Set(ids: ["de-1", "fi-1", "se-1"]))!;

        set.Configs.Select(item => item.Server!.Id).Should().Equal("de-1", "fi-1", "se-1");
        set.RecoveryUntil.Should().Be(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
        set.GeneratedAt.Should().Be(new DateTimeOffset(2026, 10, 10, 11, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Parse_AtMostFourServers_DuplicatesAndEnvelopesWithoutProfileOrIdAreDropped()
    {
        ColituRecoverySet.Parse(Set(ids: ["a", "b", "c", "d", "e"]))!.Configs.Should().HaveCount(4);
        ColituRecoverySet.Parse(Set(ids: ["a", "A", "b"]))!.Configs.Select(item => item.Server!.Id).Should().Equal("a", "b");
        ColituRecoverySet.Parse("""{"recovery_until":"2026-10-24T12:00:00Z","configs":[{"server":{"id":"x"}},null,{"profile":{"format":"xray-mobile-v1","payload":{}}}]}""").Should().BeNull();
    }

    [Theory]
    [InlineData("""{"generated_at":"2026-10-10T11:00:00Z","recovery_until":"2026-10-24T12:00:00Z","configs":[]}""")]
    [InlineData("""{"generated_at":"2026-10-10T11:00:00Z","recovery_until":"2026-10-24T12:00:00Z"}""")]
    [InlineData("""{"generated_at":"2026-10-10T11:00:00Z","recovery_until":"soon","configs":[]}""")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("")]
    public void Parse_RejectsEmptyConfigsBadDatesAndGarbage(string json)
    {
        ColituRecoverySet.Parse(json).Should().BeNull();
    }

    [Fact]
    public void Parse_BadOrMissingRecoveryUntil_IsRejected()
    {
        ColituRecoverySet.Parse(Set(until: "tomorrow", ids: ["de-1"])).Should().BeNull();
        ColituRecoverySet.Parse($$"""{"generated_at":"2026-10-10T11:00:00Z","configs":[{{Envelope("de-1")}}]}""").Should().BeNull();
    }

    // ── 2. Recovery set: use ─────────────────────────────────────────────────
    [Fact]
    public void ARecoverySetPastItsEnd_IsExpired()
    {
        var set = ColituRecoverySet.Parse(Set(until: "2026-10-10T11:59:59Z", ids: ["de-1"]))!;
        set.IsExpired(Now).Should().BeTrue();
        ColituRecoverySet.Parse(Set(until: "2026-10-10T12:00:01Z", ids: ["de-1"]))!.IsExpired(Now).Should().BeFalse();
    }

    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(false, true, false, false)]   // a server the user picked (or a multihop route)
    [InlineData(true, false, false, false)]   // the panel answered, or only some bases failed
    [InlineData(true, true, true, false)]     // the cached settings can still connect
    public void ItIsUsedOnlyInAutomaticMode_WhenTheApiIsUnreachableAndTheCacheIsUnusable(bool automatic, bool unreachable, bool cacheUsable, bool expected)
    {
        ColituRecoverySet.ShouldUse(automatic, unreachable, cacheUsable).Should().Be(expected);
    }

    [Fact]
    public void OnlyANetworkLevelFailureOfTheApiCounts_NotAnAnswerOfThePanel()
    {
        ColituRecoverySet.IsApiUnreachable(new HttpRequestException("x", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused), null)).Should().BeTrue();
        ColituRecoverySet.IsApiUnreachable(new TaskCanceledException()).Should().BeTrue();
        ColituRecoverySet.IsApiUnreachable(new InvalidOperationException("x", new IOException("reset"))).Should().BeTrue();
        ColituRecoverySet.IsApiUnreachable(new ColituApiException(HttpStatusCode.ServiceUnavailable, "down")).Should().BeFalse();
        ColituRecoverySet.IsApiUnreachable(new ColituApiException(HttpStatusCode.Forbidden, "no", "DEVICE_REVOKED")).Should().BeFalse();
        ColituRecoverySet.IsApiUnreachable(new InvalidOperationException("Connection settings have expired.")).Should().BeFalse();
    }

    [Fact]
    public void ServersAreTriedInOrder_AndTheOnesThatFailedInThisConnectAreSkipped()
    {
        var set = ColituRecoverySet.Parse(Set(ids: ["de-1", "fi-1", "se-1"]))!;
        var failed = new List<string>();

        set.NextServer(failed)!.Server!.Id.Should().Be("de-1");
        failed.Add("DE-1");
        set.NextServer(failed)!.Server!.Id.Should().Be("fi-1");
        failed.Add("fi-1");
        set.NextServer(failed)!.Server!.Id.Should().Be("se-1");
        failed.Add("se-1");
        set.NextServer(failed).Should().BeNull();
        // A server of the panel's list that failed earlier but is not in the set changes nothing.
        set.NextServer(["nl-9"])!.Server!.Id.Should().Be("de-1");
    }

    [Fact]
    public async Task AnEnvelope_BecomesSettingsLikeACachedOne_AndItsOwnOfflineGraceIsIgnored()
    {
        // grace ended in 2020: GET /config would be refused, a recovery envelope is not.
        var envelope = ColituRecoverySet.Parse(Set(ids: ["de-1"]))!.Configs[0];
        envelope.OfflineGraceUntil!.Value.Should().BeBefore(Now);

        var config = await ColituApiClient.Instance.GetRecoveryConfigAsync(envelope);

        config.Should().NotBeNull();
        config!.ServerId.Should().Be("de-1");
        config.Candidates.Should().ContainSingle().Which.ShareLink.Should().StartWith("trojan://p@203.0.113.7:443?");
    }

    // ── 2. Recovery set: fetch and store ─────────────────────────────────────
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, null, true)]
    [InlineData(HttpStatusCode.Forbidden, "DEVICE_REVOKED", true)]
    [InlineData(HttpStatusCode.Forbidden, "ENTITLEMENT_INACTIVE", true)]
    [InlineData(HttpStatusCode.Unauthorized, "REFRESH_FAILED", false)]   // the refresh could not reach the panel
    [InlineData(HttpStatusCode.InternalServerError, null, false)]
    [InlineData(HttpStatusCode.BadGateway, null, false)]
    [InlineData(HttpStatusCode.NotFound, null, false)]
    [InlineData(HttpStatusCode.TooManyRequests, null, false)]
    public void A401Or403_DeletesTheStoredSet_OtherAnswersKeepIt(HttpStatusCode status, string? code, bool deletes)
    {
        ColituRecoverySet.DeletesSetOnFetchFailure(status, code).Should().Be(deletes);
    }

    [Fact]
    public void ANetworkError_KeepsTheStoredSet()
    {
        // A transport failure is not an answer at all: the fetch code only deletes on ColituApiException 401/403.
        ColituRecoverySet.IsApiUnreachable(new HttpRequestException("offline")).Should().BeTrue();
    }

    [Fact]
    public void RefreshIsDueAfter24Hours_OrWithoutASet_AndAtMostOneAttemptPer6Hours()
    {
        // No stored set: fetch now.
        ColituRecoverySet.RefreshDue(null, null, Now).Should().BeTrue();
        // ...but not again within 6 h of the last attempt, whatever it did.
        ColituRecoverySet.RefreshDue(null, Now.AddHours(-5), Now).Should().BeFalse();
        ColituRecoverySet.RefreshDue(null, Now.AddHours(-6), Now).Should().BeTrue();

        // A fresh set is left alone, a 24 h old one is refreshed.
        ColituRecoverySet.RefreshDue(Now.AddHours(-23), null, Now).Should().BeFalse();
        ColituRecoverySet.RefreshDue(Now.AddHours(-24), null, Now).Should().BeTrue();
        ColituRecoverySet.RefreshDue(Now.AddHours(-30), Now.AddHours(-1), Now).Should().BeFalse();
        ColituRecoverySet.RefreshDue(Now.AddHours(-30), Now.AddHours(-7), Now).Should().BeTrue();
        // A clock set back never freezes it.
        ColituRecoverySet.RefreshDue(Now.AddDays(3), null, Now).Should().BeTrue();
    }
}
