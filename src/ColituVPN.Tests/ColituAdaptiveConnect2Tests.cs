using System.Text.Json.Nodes;
using AwesomeAssertions;
using v2rayN.Services;
using Xunit;

namespace v2rayN.Tests;

/// <summary>Adaptive Connect 2.0 (Android's final behaviour): watcher, stall marks, spare choice, spare health, parallel connect.</summary>
public class ColituAdaptiveConnect2Tests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private const string Net = "wifi|RU-AS0001";

    // ── 2/3: mid-session watcher ─────────────────────────────────────────────
    [Fact]
    public void Watcher_Every5sFor90s_ThenEvery30s()
    {
        ColituTunnelWatch.Interval(TimeSpan.FromSeconds(10)).Should().Be(TimeSpan.FromSeconds(5));
        ColituTunnelWatch.Interval(TimeSpan.FromSeconds(95)).Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void WithoutSpare_ThreeMissesWhileOnline_PrimaryDead_OfflineCountsNothing()
    {
        var watch = new ColituTunnelWatch();
        watch.Round(null, false, online: true).Should().Be(ColituWatchAction.None);
        watch.Round(null, false, online: false).Should().Be(ColituWatchAction.Offline);
        watch.Round(null, false, online: true).Should().Be(ColituWatchAction.None);
        watch.Round(null, false, online: true).Should().Be(ColituWatchAction.PrimaryDead);
        watch.Round(null, true, online: true).Should().Be(ColituWatchAction.None);
    }

    [Fact]
    public void WithSpare_DeadPrimaryAndWorkingNormalPath_NoReconnect_SpareCarries()
    {
        var watch = new ColituTunnelWatch();
        watch.Round(false, true, true).Should().Be(ColituWatchAction.None);
        watch.Round(false, true, true).Should().Be(ColituWatchAction.None);
        watch.Round(false, true, true).Should().Be(ColituWatchAction.SpareCarries);
        // Said once; the spare keeps carrying.
        watch.Round(false, true, true).Should().Be(ColituWatchAction.None);
        watch.PrimaryDeclaredDead.Should().BeTrue();
        // Reconnect only when the normal path fails too.
        watch.Round(false, false, true).Should().Be(ColituWatchAction.None);
        watch.Round(false, false, true).Should().Be(ColituWatchAction.None);
        watch.Round(false, false, true).Should().Be(ColituWatchAction.Reconnect);
    }

    [Fact]
    public void DeadSpareNothingCouldReplace_TwoPrimaryMissesReconnect()
    {
        var watch = new ColituTunnelWatch { SpareDeadUnreplaced = true };
        watch.Round(false, true, true).Should().Be(ColituWatchAction.None);
        watch.Round(false, true, true).Should().Be(ColituWatchAction.Reconnect);
    }

    // ── 4: stall marks can't lock a network ──────────────────────────────────
    [Fact]
    public void MarksCoveringAlmostEveryTransport_AreIgnored_AndTheOrderFollowsTheTiers()
    {
        string[] offered = ["hysteria2", "vless-reality", "trojan"];
        var marked = new HashSet<string> { "hysteria2", "vless-reality" };
        ColituTransportOrder.MarksCoverAlmostAll(offered, marked.Contains).Should().BeTrue();
        ColituTransportOrder.MarksCoverAlmostAll(offered, protocol => protocol == "hysteria2").Should().BeFalse();
        ColituTransportOrder.MarksCoverAlmostAll(["trojan"], _ => true).Should().BeFalse();

        var proven = new HashSet<string> { "vless-reality" };
        var failedThisRound = new HashSet<string> { "hysteria2" };
        string[] all = ["hysteria2", "vless-reality", "trojan", "shadowsocks"];
        var order = all.OrderBy(protocol => ColituTransportOrder.IgnoredMarksTier(protocol, proven.Contains, p => p is "hysteria2" or "vless-reality" or "shadowsocks", failedThisRound.Contains)).ToList();
        order.Should().Equal("vless-reality", "trojan", "shadowsocks", "hysteria2");
    }

    [Fact]
    public void ProvisionalMarks_Last10Minutes_And6HoursOnceAnotherTransportCarriesTraffic()
    {
        var memory = new ColituAdaptiveMemory();
        memory.MarkStalled(Net, "a", "hysteria2", Now, provisional: true);
        memory.IsStalled(Net, "a", "hysteria2", Now.AddMinutes(9)).Should().BeTrue();
        memory.IsStalled(Net, "a", "hysteria2", Now.AddMinutes(11)).Should().BeFalse();

        memory.MarkStalled(Net, "a", "hysteria2", Now.AddMinutes(20), provisional: true);
        memory.RememberGoodTransport(Net, "a", "vless-reality", Now.AddMinutes(21));
        memory.IsStalled(Net, "a", "hysteria2", Now.AddHours(5)).Should().BeTrue();
        memory.IsStalled(Net, "a", "hysteria2", Now.AddHours(7)).Should().BeFalse();
    }

    [Fact]
    public void MidSessionPenalty_ProvenGetsAtMost90Seconds_OthersTheFullPenalty()
    {
        ColituAdaptiveMemory.MidSessionPenalty(true, TimeSpan.FromMinutes(10)).Should().Be(TimeSpan.FromSeconds(90));
        ColituAdaptiveMemory.MidSessionPenalty(true, TimeSpan.FromHours(6)).Should().Be(TimeSpan.FromSeconds(90));
        ColituAdaptiveMemory.MidSessionPenalty(false, TimeSpan.FromMinutes(10)).Should().Be(TimeSpan.FromMinutes(10));
        ColituAdaptiveMemory.MidSessionPenalty(true, TimeSpan.FromSeconds(30)).Should().Be(TimeSpan.FromSeconds(30));
        ColituAdaptiveMemory.MidSessionPenalty(false, TimeSpan.FromSeconds(30)).Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void MarkMidSessionStall_ProvenTransport_ShortMark_UnprovenKeepsTheFullOne()
    {
        var memory = new ColituAdaptiveMemory();
        memory.RememberGoodTransport(Net, "a", "hysteria2", Now);
        memory.MarkMidSessionStall(Net, "a", "hysteria2", Now.AddMinutes(1), provisional: true).Should().BeTrue();
        memory.IsStalled(Net, "a", "hysteria2", Now.AddMinutes(1).AddSeconds(60)).Should().BeTrue();
        memory.IsStalled(Net, "a", "hysteria2", Now.AddMinutes(1).AddSeconds(100)).Should().BeFalse();
        // Another transport carrying traffic does not turn the short mark into 6 h.
        memory.RememberGoodTransport(Net, "a", "vless-reality", Now.AddMinutes(2));
        memory.IsStalled(Net, "a", "hysteria2", Now.AddMinutes(5)).Should().BeFalse();
        // The proof was removed by the first mark on server a: unproven now, the full 10 minutes.
        memory.MarkMidSessionStall(Net, "a", "hysteria2", Now.AddMinutes(3), provisional: true).Should().BeFalse();
        memory.IsStalled(Net, "a", "hysteria2", Now.AddMinutes(8)).Should().BeTrue();
        memory.IsStalled(Net, "a", "hysteria2", Now.AddMinutes(14)).Should().BeFalse();

        var other = new ColituAdaptiveMemory();
        other.MarkMidSessionStall(Net, "a", "trojan", Now, provisional: false).Should().BeFalse();
        other.IsStalled(Net, "a", "trojan", Now.AddHours(5)).Should().BeTrue();
    }

    [Fact]
    public void Watch_DeferredVerdict_ComesBackOnTheNextMiss_NotThreeMissesLater()
    {
        var watch = new ColituTunnelWatch();
        var last = ColituWatchAction.None;
        for (var i = 0; i < ColituTunnelWatch.MissesForDead; i++)
        {
            last = watch.Round(null, false, true);
        }
        last.Should().Be(ColituWatchAction.PrimaryDead);
        watch.Defer(last);
        watch.Round(null, false, true).Should().Be(ColituWatchAction.PrimaryDead);

        // A healthy round after a deferred verdict starts counting again.
        watch.Defer(ColituWatchAction.PrimaryDead);
        watch.Round(null, true, true).Should().Be(ColituWatchAction.None);
        watch.Round(null, false, true).Should().Be(ColituWatchAction.None);
        watch.NormalMisses.Should().Be(1);
    }

    // ── 5: spare choice ──────────────────────────────────────────────────────
    private static readonly string[] All = ["hysteria2", "vless-reality", "vless-xhttp", "trojan", "shadowsocks"];

    [Fact]
    public void NextServer_ProvenFirst_ThePrimarysOwnTransportBeforeOthers()
    {
        var proven = new HashSet<string> { "vless-reality", "trojan" };
        ColituWarmSpare.ChooseOnNextServer("vless-reality", All, singBox: false, proven.Contains, _ => false)
            .Should().Be(new ColituSpareChoice("vless-reality", "proven"));
        ColituWarmSpare.ChooseOnNextServer("hysteria2", All, singBox: true, proven.Contains, _ => false)
            .Should().Be(new ColituSpareChoice("vless-reality", "proven"));
    }

    [Fact]
    public void NextServer_NothingProven_OtherFamily_ThenTheSameTransport_HintedOnlyAsLastResort()
    {
        ColituWarmSpare.ChooseOnNextServer("hysteria2", All, singBox: true, _ => false, _ => false)
            .Should().Be(new ColituSpareChoice("vless-reality", "other-family"));
        // Xray: no Hysteria2, so the primary's own transport on the next server.
        ColituWarmSpare.ChooseOnNextServer("vless-reality", All, singBox: false, _ => false, _ => false)
            .Should().Be(new ColituSpareChoice("vless-reality", "same-transport"));
        ColituWarmSpare.ChooseOnNextServer("hysteria2", ["hysteria2", "vless-reality"], singBox: true, _ => false, _ => false, protocol => protocol == "vless-reality")
            .Should().Be(new ColituSpareChoice("hysteria2", "same-transport"));
        ColituWarmSpare.ChooseOnNextServer("hysteria2", ["vless-reality"], singBox: true, _ => false, _ => false, _ => true)
            .Should().Be(new ColituSpareChoice("vless-reality", "hinted-fallback"));
    }

    [Fact]
    public void SameServer_OtherFamily_SkippingStalled_ShadowsocksLast_NeverThePrimary()
    {
        ColituWarmSpare.ChooseOnSameServer("hysteria2", All, singBox: true, protocol => protocol == "vless-reality")
            .Should().Be(new ColituSpareChoice("trojan", "other-family"));
        ColituWarmSpare.ChooseOnSameServer("hysteria2", ["hysteria2", "shadowsocks", "trojan"], singBox: true, _ => false)!.Protocol.Should().Be("trojan");
        ColituWarmSpare.ChooseOnSameServer("vless-reality", ["vless-reality"], singBox: false, _ => false).Should().BeNull();
        // Xray cannot run the other family on desktop: the rest of the primary's family.
        ColituWarmSpare.ChooseOnSameServer("vless-reality", All, singBox: false, _ => false)
            .Should().Be(new ColituSpareChoice("vless-xhttp", "same-family"));
        ColituWarmSpare.ChooseOnSameServer("vless-reality", All, singBox: true, _ => false)
            .Should().Be(new ColituSpareChoice("hysteria2", "other-family"));
    }

    // ── 7: spare health probe ────────────────────────────────────────────────
    [Fact]
    public void SpareProbe_TwoMissesWhileOnline_Dead_OfflineMissesDoNotCount()
    {
        ColituSpareHealth.Interval("hysteria2").Should().Be(TimeSpan.FromSeconds(60));
        ColituSpareHealth.Interval("trojan").Should().Be(TimeSpan.FromSeconds(180));
        var health = new ColituSpareHealth();
        health.Probe(false, online: true).Should().BeFalse();
        health.Probe(false, online: false).Should().BeFalse();
        health.Probe(false, online: true).Should().BeTrue("2 misses while online make it dead: a replacement is picked");
        health.Probe(true, online: true).Should().BeFalse();
    }

    [Fact]
    public void SpareSwap_IsDeferredWhileTheTunnelIsBusy()
    {
        ColituSpareHealth.CanSwap(4 * 1024).Should().BeTrue();
        ColituSpareHealth.CanSwap(64 * 1024).Should().BeFalse("over 10 KB in the last 10 s: swap deferred");
        ColituSpareHealth.CanSwap(-1).Should().BeTrue("unknown traffic counts as quiet");
    }

    // ── 8: parallel connect ──────────────────────────────────────────────────
    [Fact]
    public void ParallelRound_Winner_RoleSwap_BothFail()
    {
        ColituParallelConnect.Decide(true, false).Should().Be(ColituParallelOutcome.PrimaryWins);
        ColituParallelConnect.Decide(false, true, primaryOkAfterGrace: true).Should().Be(ColituParallelOutcome.PrimaryWins);
        ColituParallelConnect.Decide(false, true).Should().Be(ColituParallelOutcome.SwapRoles);
        ColituParallelConnect.Decide(false, false).Should().Be(ColituParallelOutcome.BothFailed);
        ColituParallelConnect.Decide(false, null).Should().Be(ColituParallelOutcome.PrimaryFailed);
        ColituParallelConnect.PrimaryGrace.Should().Be(TimeSpan.FromMilliseconds(1500));
    }

    // ── 9: generated configs ─────────────────────────────────────────────────
    private const string Xray = """
    {
      "inbounds": [ { "tag": "socks", "port": 10808, "listen": "127.0.0.1", "protocol": "mixed" } ],
      "outbounds": [ { "tag": "proxy", "protocol": "vless", "settings": {} }, { "tag": "direct", "protocol": "freedom" }, { "tag": "dns", "protocol": "dns" } ],
      "dns": { "tag": "dns-module", "servers": [ "https://dns.example.net/dns-query" ] },
      "routing": { "rules": [ { "type": "field", "port": "53", "outboundTag": "dns" }, { "type": "field", "inboundTag": [ "dns-module" ], "outboundTag": "proxy" }, { "type": "field", "port": "0-65535", "outboundTag": "proxy" } ] }
    }
    """;

    [Fact]
    public void Xray_BothCheckInbounds_DnsRuleOnTop_TcpUserTimeout()
    {
        var spare = JsonNode.Parse("""{ "tag": "proxy", "protocol": "trojan", "settings": {} }""")!.AsObject();
        var json = ColituWarmSpare.ApplyXray(Xray, spare, verify: new(41001, "u", "p"), spareVerify: new(41002, "u2", "p2"))!;
        ColituWarmSpare.XrayProblems(json).Should().BeEmpty();
        var root = JsonNode.Parse(json)!;
        var rules = root["routing"]!["rules"]!.AsArray();
        rules[0]!["inboundTag"]![0]!.GetValue<string>().Should().Be("colitu-verify");
        rules[0]!["outboundTag"]!.GetValue<string>().Should().Be("proxy");
        rules[1]!["inboundTag"]![0]!.GetValue<string>().Should().Be("colitu-verify-spare");
        rules[1]!["outboundTag"]!.GetValue<string>().Should().Be("warm-spare");
        rules[2]!["inboundTag"]![0]!.GetValue<string>().Should().Be("dns-module");
        rules[2]!["balancerTag"]!.GetValue<string>().Should().Be("proxy-auto");
        rules.Count(rule => rule!["inboundTag"]?.ToJsonString().Contains("dns-module") == true).Should().Be(1);
        foreach (var outbound in root["outbounds"]!.AsArray().Where(item => item!["tag"]!.GetValue<string>() is "proxy" or "warm-spare"))
        {
            outbound!["streamSettings"]!["sockopt"]!["tcpUserTimeout"]!.GetValue<int>().Should().Be(10000);
        }
    }

    [Fact]
    public void Singbox_SpareCheckInbound_GoesToTheSpareOnly()
    {
        const string singbox = """
        { "inbounds": [], "outbounds": [ { "type": "hysteria2", "tag": "proxy" }, { "type": "direct", "tag": "direct" } ],
          "dns": { "servers": [ { "tag": "remote", "type": "https", "server": "dns.example.net", "detour": "proxy" } ] }, "route": { "rules": [], "final": "proxy" } }
        """;
        var spare = JsonNode.Parse("""{ "type": "vless", "tag": "proxy" }""")!.AsObject();
        var json = ColituWarmSpare.ApplySingbox(singbox, spare, verify: new(41001, "u", "p"), spareVerify: new(41002, "u2", "p2"))!;
        ColituWarmSpare.SingboxProblems(json).Should().BeEmpty();
        var root = JsonNode.Parse(json)!;
        root["route"]!["rules"]![1]!["outbound"]!.GetValue<string>().Should().Be("warm-spare");
        root["outbounds"]!.AsArray().Single(item => item!["tag"]!.GetValue<string>() == "warm-spare")!["tcp_keep_alive"]!.GetValue<string>().Should().Be("10s");
        root["outbounds"]!.AsArray().Single(item => item!["tag"]!.GetValue<string>() == "proxy-main")!["tcp_keep_alive"].Should().BeNull("Hysteria2 is QUIC");
    }
}
