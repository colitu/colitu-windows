using System.Net;
using AwesomeAssertions;
using ServiceLib;
using v2rayN.Services;
using Xunit;

namespace v2rayN.Tests;

/// <summary>Windows 2.5.5: the fixes for tunnels that dropped although the network was fine.</summary>
public class ColituTunnelHealthTests
{
    [Fact]
    public void TunMode_RoutesWithoutWaitingForDns_ProxyModeStillResolves()
    {
        ColituVpnService.RoutingDomainStrategy(tun: true).Should().Be(Global.AsIs);
        ColituVpnService.RoutingDomainStrategy(tun: false).Should().Be(Global.IPIfNonMatch);
    }

    [Fact]
    public void Transports_FollowAFixedOrder_LatencyDoesNotLiftShadowsocks()
    {
        // 2026-10-05 08:54: shadowsocks (197 ms) went first and stalled 24 s later.
        var order = new[] { ("shadowsocks", 30), ("trojan", 31), ("vless-xhttp", 60), ("vless-reality", 80), ("hysteria2", -1) }
            .OrderBy(t => ColituVpnService.TransportRank(t.Item1, t.Item2, stalledRecently: false))
            .Select(t => t.Item1);

        order.Should().Equal("hysteria2", "vless-reality", "vless-xhttp", "trojan", "shadowsocks");
    }

    [Fact]
    public void StalledAndUnreachableTransports_GoLast()
    {
        var stalledReality = ColituVpnService.TransportRank("vless-reality", 40, stalledRecently: true);
        var unreachableTrojan = ColituVpnService.TransportRank("trojan", -1, stalledRecently: false);
        var shadowsocks = ColituVpnService.TransportRank("shadowsocks", 40, stalledRecently: false);

        shadowsocks.Should().BeLessThan(unreachableTrojan);
        unreachableTrojan.Should().BeLessThan(stalledReality);
    }

    [Fact]
    public void PathCheck_NothingAnswers_IsOffline()
    {
        ColituVpnService.ClassifyPath(true, null, [null, null]).Should().Be(ColituPathState.Offline);
        ColituVpnService.ClassifyPath(false, null, [null, null]).Should().Be(ColituPathState.Offline);
    }

    [Fact]
    public void PathCheck_InternetWorksButNotTheServer_IsServerUnreachable()
    {
        ColituVpnService.ClassifyPath(true, null, [12, null]).Should().Be(ColituPathState.ServerUnreachable);
    }

    [Fact]
    public void PathCheck_ServerAnswers_IsReachable()
    {
        // Russian ISPs can block foreign references; the server answering is enough.
        ColituVpnService.ClassifyPath(true, 40, [null, null]).Should().Be(ColituPathState.Reachable);
        ColituVpnService.ClassifyPath(true, 40, [12, 30]).Should().Be(ColituPathState.Reachable);
        // Server address unknown: the references decide.
        ColituVpnService.ClassifyPath(false, null, [12, null]).Should().Be(ColituPathState.Reachable);
    }

    [Theory]
    [InlineData("172.18.0.1", 30, "172.18.0.2")]
    [InlineData("172.18.0.2", 30, "172.18.0.1")]
    [InlineData("172.19.0.1", 30, "172.19.0.2")]
    public void TunDnsCheck_AsksTheOtherHostOfTheAdaptersSubnet(string local, int prefix, string peer)
    {
        ColituNetwork.TunPeer(IPAddress.Parse(local), prefix)!.Value.Peer.Should().Be(IPAddress.Parse(peer));
    }

    [Fact]
    public void TunDnsCheck_SkipsSubnetsWithoutASecondHost()
    {
        ColituNetwork.TunPeer(IPAddress.Parse("172.18.0.1"), 32).Should().BeNull();
        ColituNetwork.TunPeer(IPAddress.Parse("fd00::1"), 126).Should().BeNull();
    }

    [Fact]
    public void DnsQuery_IsARecursiveAQuery()
    {
        var query = ColituNetwork.BuildDnsQuery(0x1234, "c0a1b2c3.colitu.com");

        query[..12].Should().Equal(0x12, 0x34, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00);
        query[12..].Should().Equal(
            new byte[] { 8 }.Concat("c0a1b2c3"u8.ToArray())
                .Concat(new byte[] { 6 }).Concat("colitu"u8.ToArray())
                .Concat(new byte[] { 3 }).Concat("com"u8.ToArray())
                .Concat(new byte[] { 0, 0, 1, 0, 1 }));
    }

    [Theory]
    [InlineData(0x00, true)]   // NOERROR
    [InlineData(0x03, true)]   // NXDOMAIN: the resolver works, the name does not exist
    [InlineData(0x02, false)]  // SERVFAIL: the upstream did not answer
    [InlineData(0x05, false)]  // REFUSED
    public void DnsReply_TellsWhetherTheResolverWorks(byte rcode, bool works)
    {
        var reply = new byte[] { 0x12, 0x34, 0x81, (byte)(0x80 | rcode), 0, 1, 0, 0, 0, 0, 0, 0 };

        ColituNetwork.DnsAnswerState(reply, 0x1234).Should().Be(works);
    }

    [Fact]
    public void DnsReply_ForAnotherQueryOrNotAReply_IsIgnored()
    {
        ColituNetwork.DnsAnswerState(new byte[] { 0x99, 0x99, 0x81, 0x80, 0, 1, 0, 0, 0, 0, 0, 0 }, 0x1234).Should().BeNull();
        ColituNetwork.DnsAnswerState(new byte[] { 0x12, 0x34, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 }, 0x1234).Should().BeNull();
        ColituNetwork.DnsAnswerState(new byte[] { 0x12, 0x34 }, 0x1234).Should().BeNull();
    }
}
