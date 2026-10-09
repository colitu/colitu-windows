using AwesomeAssertions;
using v2rayN.Services;
using Xunit;

namespace v2rayN.Tests;

/// <summary>Adaptive Connect v2: the automatic server order and the per-network memory.</summary>
public class ColituAdaptiveConnectTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private const string Wifi = "wifi|TR-AS9121";
    private const string Lte = "cellular|RU-AS0001";

    private static ColituVpnServer Server(string id, string country = "DE") => new() { Id = id, CountryCode = country, Available = true };

    private static Dictionary<string, ColituPingSample> Pings(params (string Id, int? Ms)[] pings) =>
        pings.ToDictionary(item => item.Id, item => new ColituPingSample(item.Ms, Now.AddMinutes(-1), Wifi));

    private static List<string?> Rank(IEnumerable<ColituVpnServer> servers, IReadOnlyDictionary<string, ColituPingSample>? pings = null,
        ColituAdaptiveMemory? memory = null, string? country = null, string network = Wifi) =>
        ColituAdaptiveConnect.Rank(servers, pings ?? new Dictionary<string, ColituPingSample>(), memory ?? new(), network, country, Now)
            .Select(server => server.Id).ToList();

    [Fact]
    public void NetworkKey_IsLinkAndClientNetwork()
    {
        ColituAdaptiveConnect.NetworkKey("wifi", "TR-AS9121").Should().Be("wifi|TR-AS9121");
        ColituAdaptiveConnect.NetworkKey(null, null).Should().Be("other|");
        ColituAdaptiveConnect.NetworkKey("Ethernet", " TR-AS1 ").Should().Be("ethernet|TR-AS1");
    }

    [Fact]
    public void Rank_WithoutPings_KeepsThePanelOrder_AndSkipsRoutesAndUnusableServers()
    {
        var servers = new[]
        {
            Server("a"), new ColituVpnServer { Id = "route", IsMultihop = true, Available = true },
            Server("b"), new ColituVpnServer { Id = "down", Available = false },
            new ColituVpnServer { Id = "locked", Available = true, Locked = true }, Server("c")
        };

        Rank(servers).Should().Equal("a", "b", "c");
    }

    [Fact]
    public void Rank_FreshPingsAscending_ThenUnpingedInPanelOrder()
    {
        var servers = new[] { Server("a"), Server("b"), Server("c"), Server("d") };

        Rank(servers, Pings(("c", 40), ("b", 90))).Should().Equal("c", "b", "a", "d");
    }

    [Fact]
    public void Rank_IgnoresStalePingsAndPingsFromAnotherNetwork()
    {
        var servers = new[] { Server("a"), Server("b"), Server("c") };
        var pings = new Dictionary<string, ColituPingSample>
        {
            ["b"] = new(30, Now.AddMinutes(-11), Wifi),
            ["c"] = new(20, Now.AddMinutes(-1), Lte),
        };

        Rank(servers, pings).Should().Equal("a", "b", "c");
    }

    [Fact]
    public void Rank_FailedPing_GoesAfterAllOthersExceptPenalized()
    {
        var servers = new[] { Server("dead"), Server("slow"), Server("unpinged"), Server("penalized") };
        var memory = new ColituAdaptiveMemory();
        memory.Penalize(Wifi, "penalized", Now.AddMinutes(-5));

        Rank(servers, Pings(("dead", null), ("slow", 400)), memory).Should().Equal("slow", "unpinged", "dead", "penalized");
    }

    [Fact]
    public void Rank_OwnCountryGoesAfterForeignOnes_OnlyWhenTheCountryIsKnown()
    {
        var servers = new[] { Server("tr1", "TR"), Server("de1", "DE"), Server("tr2", "TR"), Server("nl1", "NL") };
        var pings = Pings(("tr1", 10), ("de1", 60), ("nl1", 50));

        Rank(servers, pings, country: "tr").Should().Equal("nl1", "de1", "tr1", "tr2");
        Rank(servers, pings, country: null).Should().Equal("tr1", "nl1", "de1", "tr2");
    }

    [Fact]
    public void Rank_LastGoodServerOnThisNetwork_GoesFirst()
    {
        var servers = new[] { Server("a"), Server("b"), Server("c") };
        var memory = new ColituAdaptiveMemory();
        memory.RememberGoodServer(Wifi, "c", Now.AddHours(-2));

        Rank(servers, Pings(("a", 20), ("b", 30), ("c", 300)), memory).Should().Equal("c", "a", "b");
        // Another network: the memory is not applied there.
        Rank(servers, memory: memory, network: Lte).Should().Equal("a", "b", "c");
    }

    [Fact]
    public void Rank_LastGoodInOwnCountry_StillAfterForeignServers()
    {
        var servers = new[] { Server("tr1", "TR"), Server("de1", "DE") };
        var memory = new ColituAdaptiveMemory();
        memory.RememberGoodServer(Wifi, "tr1", Now.AddMinutes(-10));

        Rank(servers, memory: memory, country: "TR").Should().Equal("de1", "tr1");
    }

    [Fact]
    public void Rank_PenalizedServers_RotateOldestPenaltyFirst()
    {
        var servers = new[] { Server("a"), Server("b"), Server("c") };
        var memory = new ColituAdaptiveMemory();
        memory.Penalize(Wifi, "b", Now.AddMinutes(-20));
        memory.Penalize(Wifi, "c", Now.AddMinutes(-10));
        memory.Penalize(Wifi, "a", Now.AddMinutes(-1));

        Rank(servers, memory: memory).Should().Equal("b", "c", "a");
    }

    [Fact]
    public void Penalty_ExpiresAfter30Minutes_AndOnlyOnItsNetwork()
    {
        var memory = new ColituAdaptiveMemory();
        memory.Penalize(Wifi, "a", Now);

        memory.IsPenalized(Wifi, "a", Now.AddMinutes(29)).Should().BeTrue();
        memory.IsPenalized(Wifi, "a", Now.AddMinutes(31)).Should().BeFalse();
        memory.IsPenalized(Lte, "a", Now.AddMinutes(1)).Should().BeFalse();
    }

    [Fact]
    public void LastGoodServerAndTransport_ExpireAfter24Hours()
    {
        var memory = new ColituAdaptiveMemory();
        memory.RememberGoodServer(Wifi, "a", Now);
        memory.RememberGoodTransport(Wifi, "a", "vless-reality", Now);

        memory.LastGoodServer(Wifi, Now.AddHours(23)).Should().Be("a");
        memory.LastGoodTransport(Wifi, "a", Now.AddHours(23)).Should().Be("vless-reality");
        memory.LastGoodServer(Wifi, Now.AddHours(25)).Should().BeNull();
        memory.LastGoodTransport(Wifi, "a", Now.AddHours(25)).Should().BeNull();
        memory.LastGoodTransport(Wifi, "b", Now).Should().BeNull();
    }

    [Fact]
    public void StalledTransport_ExpiresAfter6Hours_PerServerAndNetwork()
    {
        var memory = new ColituAdaptiveMemory();
        memory.MarkStalled(Wifi, "a", "hysteria2", Now);

        memory.IsStalled(Wifi, "a", "hysteria2", Now.AddHours(5)).Should().BeTrue();
        memory.IsStalled(Wifi, "a", "hysteria2", Now.AddHours(7)).Should().BeFalse();
        memory.IsStalled(Wifi, "b", "hysteria2", Now).Should().BeFalse();
        memory.IsStalled(Lte, "a", "hysteria2", Now).Should().BeFalse();
        memory.IsStalled(Wifi, "a", "vless-reality", Now).Should().BeFalse();
    }

    [Fact]
    public void StalledOnTwoServers_IsMarkedForTheWholeNetwork()
    {
        var memory = new ColituAdaptiveMemory();
        memory.MarkStalled(Wifi, "a", "hysteria2", Now);
        memory.MarkStalled(Wifi, "b", "hysteria2", Now.AddMinutes(1));

        memory.IsStalled(Wifi, "c", "hysteria2", Now.AddMinutes(2)).Should().BeTrue();
        memory.IsStalled(Lte, "c", "hysteria2", Now.AddMinutes(2)).Should().BeFalse();
    }

    [Fact]
    public void Success_ClearsTheStallMark_AndAFailureClearsLastGood()
    {
        var memory = new ColituAdaptiveMemory();
        memory.MarkStalled(Wifi, "a", "trojan", Now);
        memory.RememberGoodTransport(Wifi, "a", "trojan", Now.AddMinutes(1));
        memory.IsStalled(Wifi, "a", "trojan", Now.AddMinutes(2)).Should().BeFalse();

        memory.RememberGoodServer(Wifi, "a", Now);
        memory.Penalize(Wifi, "a", Now.AddMinutes(1));
        memory.LastGoodServer(Wifi, Now.AddMinutes(2)).Should().BeNull();

        memory.RememberGoodServer(Wifi, "a", Now.AddMinutes(3));
        memory.IsPenalized(Wifi, "a", Now.AddMinutes(4)).Should().BeFalse();
    }

    [Fact]
    public void Load_PrunesExpiredEntries_AndTheStoreIsCapped()
    {
        var memory = new ColituAdaptiveMemory();
        memory.Load(
        [
            new ColituAdaptiveEntry { Kind = ColituAdaptiveMemory.Penalized, Network = Wifi, Server = "old", At = Now.AddHours(-1) },
            new ColituAdaptiveEntry { Kind = ColituAdaptiveMemory.Penalized, Network = Wifi, Server = "new", At = Now.AddMinutes(-1) },
            new ColituAdaptiveEntry { Kind = "unknown", Network = Wifi, Server = "x", At = Now },
        ], Now);

        memory.Count.Should().Be(1);
        memory.IsPenalized(Wifi, "new", Now).Should().BeTrue();

        for (var i = 0; i < 300; i++)
        {
            memory.Penalize(Wifi, $"s{i}", Now.AddSeconds(i));
        }
        memory.Count.Should().Be(ColituAdaptiveMemory.MaxEntries);
        // The newest facts stay.
        memory.IsPenalized(Wifi, "s299", Now.AddSeconds(300)).Should().BeTrue();
        memory.IsPenalized(Wifi, "s0", Now.AddSeconds(300)).Should().BeFalse();
    }

    [Fact]
    public void Snapshot_RoundTripsThroughLoad()
    {
        var memory = new ColituAdaptiveMemory();
        memory.RememberGoodServer(Wifi, "a", Now);
        memory.MarkStalled(Wifi, "a", "hysteria2", Now);

        var copy = new ColituAdaptiveMemory();
        copy.Load(memory.Snapshot(Now), Now.AddMinutes(1));

        copy.LastGoodServer(Wifi, Now.AddMinutes(1)).Should().Be("a");
        copy.IsStalled(Wifi, "a", "hysteria2", Now.AddMinutes(1)).Should().BeTrue();
    }

    [Fact]
    public void ConfigPath_AsksForANode_OrExcludesAtMostTen()
    {
        ColituApiClient.ConfigPath(null, null).Should().Be("/config?protocol=auto");
        ColituApiClient.ConfigPath("node-1", ["x"]).Should().Be("/config?protocol=auto&node=node-1");
        var exclude = Enumerable.Range(1, 12).Select(i => $"n{i}").Append("n1").ToList();
        ColituApiClient.ConfigPath(null, exclude).Should().Be("/config?protocol=auto&exclude=n1,n2,n3,n4,n5,n6,n7,n8,n9,n10");
    }

    [Fact]
    public void LastGoodTransport_GoesFirstWhileReachable()
    {
        ColituVpnService.TransportRank("trojan", 50, stalledRecently: false, lastGood: true)
            .Should().BeLessThan(ColituVpnService.TransportRank("hysteria2", -1, stalledRecently: false));
        ColituVpnService.TransportRank("trojan", -1, stalledRecently: false, lastGood: true)
            .Should().BeGreaterThan(ColituVpnService.TransportRank("vless-reality", 50, stalledRecently: false));
        ColituVpnService.TransportRank("trojan", 50, stalledRecently: true, lastGood: true)
            .Should().BeGreaterThan(ColituVpnService.TransportRank("shadowsocks", 50, stalledRecently: false));
    }

    [Fact]
    public void SpeedBudget_PerServerAndWholeConnect()
    {
        ColituVpnService.ServerBudget(TimeSpan.Zero).Should().Be(TimeSpan.FromSeconds(20));
        ColituVpnService.ServerBudget(TimeSpan.FromSeconds(30)).Should().Be(TimeSpan.FromSeconds(15));
        ColituVpnService.ServerBudget(TimeSpan.FromSeconds(44)).Should().Be(ColituVpnService.TransportBudget);
    }

    [Fact]
    public void ServerFailure_IsNotAnOfflineDeviceOrThePanel()
    {
        ColituVpnService.IsServerFailure(new ColituConnectException("unreachable")).Should().BeTrue();
        ColituVpnService.IsServerFailure(new InvalidOperationException("VPN core stopped")).Should().BeTrue();
        ColituVpnService.IsServerFailure(new ColituConnectException("offline", offline: true)).Should().BeFalse();
        ColituVpnService.IsServerFailure(new OperationCanceledException()).Should().BeFalse();
        ColituVpnService.IsServerFailure(new ColituApiException(System.Net.HttpStatusCode.BadGateway, "down")).Should().BeFalse();
    }

    [Fact]
    public void TrafficCheck_UsesGenericEndpoints_AndOnly2xxCounts()
    {
        ColituVpnService.TrafficProbeUrls.Should().HaveCount(3);
        ColituVpnService.TrafficProbeUrls.Should().OnlyContain(url => !url.Contains("colitu", StringComparison.OrdinalIgnoreCase));
        ColituVpnService.IsTrafficProbeSuccess(204).Should().BeTrue();
        ColituVpnService.IsTrafficProbeSuccess(200).Should().BeTrue();
        ColituVpnService.IsTrafficProbeSuccess(302).Should().BeFalse();
        ColituVpnService.IsTrafficProbeSuccess(403).Should().BeFalse();
    }
}
