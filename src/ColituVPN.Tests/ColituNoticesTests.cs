using System.IO;
using AwesomeAssertions;
using v2rayN.Services;
using Xunit;

namespace v2rayN.Tests;

/// <summary>The in-app notice banner: response parsing, picking the next notice, id store, paths.</summary>
public class ColituNoticesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private const string Sample = """
        {"notices":[
          {"id":"usage_80:1790812800","kind":"usage","level":"warning","title":"80% used","body":"Body one","button":"Upgrade","url":"https://colitu.com/pricing","push":true,"expires_at":"2026-11-01T00:00:00Z"},
          {"id":"campaign:autumn","kind":"campaign","level":"promo","title":"Autumn","body":"Body two"}
        ]}
        """;

    [Fact]
    public void Parse_ReadsTheContract()
    {
        var notices = ColituNotices.Parse(Sample);
        notices.Should().HaveCount(2);
        var first = notices[0];
        first.Id.Should().Be("usage_80:1790812800");
        first.Kind.Should().Be("usage");
        first.Level.Should().Be("warning");
        first.Title.Should().Be("80% used");
        first.Body.Should().Be("Body one");
        first.Button.Should().Be("Upgrade");
        first.Url.Should().Be("https://colitu.com/pricing");
        first.Push.Should().BeTrue();
        first.ExpiresAt.Should().Be("2026-11-01T00:00:00Z");
        first.HasButton.Should().BeTrue();
        notices[1].HasButton.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"notices":null}""")]
    [InlineData("""{"notices":[]}""")]
    [InlineData("""{"notices":"x"}""")]
    public void Parse_AnythingUnusableIsNoNotices(string? json)
    {
        ColituNotices.Parse(json).Should().BeEmpty();
    }

    [Fact]
    public void Parse_DropsBrokenEntriesAndDuplicates()
    {
        var notices = ColituNotices.Parse("""
            {"notices":[
              {"id":"","title":"no id"},
              {"id":"a","title":"","body":""},
              {"id":"b","title":"kept"},
              {"id":"b","title":"duplicate"},
              null
            ]}
            """);
        notices.Select(n => n.Id).Should().Equal("b");
        notices[0].Title.Should().Be("kept");
    }

    [Theory]
    [InlineData("critical", "critical")]
    [InlineData("WARNING", "warning")]
    [InlineData("promo", "promo")]
    [InlineData("info", "info")]
    [InlineData("rainbow", "info")]
    [InlineData(null, "info")]
    public void Level_FallsBackToInfo(string? level, string expected)
    {
        ColituNotices.NormalizeLevel(level).Should().Be(expected);
    }

    [Theory]
    [InlineData("http://colitu.com/x")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/system32/calc.exe")]
    [InlineData("mailto:a@b.c")]
    [InlineData("/relative")]
    public void UnsafeLinks_RemoveTheButton(string url)
    {
        var json = $$"""{"notices":[{"id":"x","title":"t","button":"Open","url":"{{url}}"}]}""";
        var notice = ColituNotices.Parse(json).Single();
        notice.Url.Should().BeNull();
        notice.Button.Should().BeNull();
        notice.HasButton.Should().BeFalse();
    }

    [Fact]
    public void Button_NeedsALabel()
    {
        var notice = ColituNotices.Parse("""{"notices":[{"id":"x","title":"t","url":"https://colitu.com"}]}""").Single();
        notice.HasButton.Should().BeFalse();
    }

    [Fact]
    public void LongTexts_AreClipped()
    {
        var json = $$"""{"notices":[{"id":"x","title":"{{new string('t', 500)}}","body":"{{new string('b', 5000)}}"}]}""";
        var notice = ColituNotices.Parse(json).Single();
        notice.Title.Length.Should().Be(200);
        notice.Body.Length.Should().Be(1000);
    }

    // ── Picking the next notice ───────────────────────────────────────────
    [Fact]
    public void PickNext_TakesTheFirstOpenNotice()
    {
        var notices = ColituNotices.Parse(Sample);
        ColituNotices.PickNext(notices, _ => false, Now)!.Id.Should().Be("usage_80:1790812800");
    }

    [Fact]
    public void PickNext_SkipsDismissed_ThenRunsOut()
    {
        var notices = ColituNotices.Parse(Sample);
        var dismissed = new HashSet<string> { "usage_80:1790812800" };
        ColituNotices.PickNext(notices, dismissed.Contains, Now)!.Id.Should().Be("campaign:autumn");
        dismissed.Add("campaign:autumn");
        ColituNotices.PickNext(notices, dismissed.Contains, Now).Should().BeNull();
    }

    [Fact]
    public void PickNext_SkipsExpired()
    {
        var notices = ColituNotices.Parse(Sample);
        var later = new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
        ColituNotices.PickNext(notices, _ => false, later)!.Id.Should().Be("campaign:autumn");
    }

    [Fact]
    public void PickNext_NothingToShow()
    {
        ColituNotices.PickNext(null, _ => false, Now).Should().BeNull();
        ColituNotices.PickNext([], _ => false, Now).Should().BeNull();
    }

    [Theory]
    [InlineData("2026-10-09T11:59:59Z", true)]
    [InlineData("2026-10-09T12:00:00Z", true)]
    [InlineData("2026-10-09T12:00:01Z", false)]
    [InlineData("2026-10-09T15:00:01+03:00", false)]
    [InlineData("garbage", false)]
    [InlineData(null, false)]
    public void Expiry_UsesTheRfc3339Time(string? expiresAt, bool expired)
    {
        ColituNotices.IsExpired(new ColituNotice { Id = "x", Title = "t", ExpiresAt = expiresAt }, Now).Should().Be(expired);
    }

    // ── Paths ─────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("tr", "/client/notices?lang=tr")]
    [InlineData("RU", "/client/notices?lang=ru")]
    [InlineData("en", "/client/notices?lang=en")]
    [InlineData("de", "/client/notices?lang=en")]
    [InlineData(null, "/client/notices?lang=en")]
    public void FetchPath_CarriesTheLanguage(string? language, string expected)
    {
        ColituNotices.FetchPath(language).Should().Be(expected);
    }

    [Fact]
    public void EventPath_EscapesTheId()
    {
        ColituNotices.EventPath("usage_80:1790812800").Should().Be("/client/notices/usage_80%3A1790812800/events");
        ColituNotices.EventPath("a/b c").Should().Be("/client/notices/a%2Fb%20c/events");
    }

    // ── Remembered ids ────────────────────────────────────────────────────
    [Fact]
    public void Prune_ForgetsIdsOlderThan90Days()
    {
        var ids = new Dictionary<string, DateTimeOffset>
        {
            ["old"] = Now - TimeSpan.FromDays(91),
            ["edge"] = Now - TimeSpan.FromDays(90),
            ["new"] = Now - TimeSpan.FromDays(1)
        };
        ColituNotices.Prune(ids, Now);
        ids.Keys.Should().BeEquivalentTo(["edge", "new"]);
    }

    [Fact]
    public void Prune_KeepsTheNewest200()
    {
        var ids = Enumerable.Range(0, 250).ToDictionary(i => $"id{i}", i => Now - TimeSpan.FromHours(250 - i));
        ColituNotices.Prune(ids, Now);
        ids.Should().HaveCount(200);
        ids.Should().ContainKey("id249").And.ContainKey("id50").And.NotContainKey("id49");
    }

    [Fact]
    public void Store_RemembersSeenAndDismissedAcrossInstances()
    {
        var path = Path.Combine(Path.GetTempPath(), $"colitu-notices-{Guid.NewGuid():N}.json");
        try
        {
            var store = new ColituNoticeStore(() => path);
            store.IsDismissed("a").Should().BeFalse();
            store.MarkSeen("a", DateTimeOffset.UtcNow).Should().BeTrue();
            store.MarkSeen("a", DateTimeOffset.UtcNow).Should().BeFalse();
            store.MarkDismissed("a", DateTimeOffset.UtcNow);
            store.IsDismissed("a").Should().BeTrue();

            var reopened = new ColituNoticeStore(() => path);
            reopened.IsDismissed("a").Should().BeTrue();
            reopened.IsDismissed("b").Should().BeFalse();
            reopened.MarkSeen("a", DateTimeOffset.UtcNow).Should().BeFalse();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Store_ForgetsOldIdsOnLoad_AndSurvivesABrokenFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"colitu-notices-{Guid.NewGuid():N}.json");
        try
        {
            var old = DateTimeOffset.UtcNow - TimeSpan.FromDays(120);
            File.WriteAllText(path, "{\"dismissed\":{\"stale\":\"" + old.ToString("O") + "\",\"fresh\":\"" + DateTimeOffset.UtcNow.ToString("O") + "\"},\"seen\":{}}");
            var store = new ColituNoticeStore(() => path);
            store.IsDismissed("stale").Should().BeFalse();
            store.IsDismissed("fresh").Should().BeTrue();

            File.WriteAllText(path, "{ broken");
            new ColituNoticeStore(() => path).IsDismissed("fresh").Should().BeFalse();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
