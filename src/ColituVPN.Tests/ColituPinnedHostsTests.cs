using System.Net;
using System.Net.Sockets;
using AwesomeAssertions;
using v2rayN.Services;
using Xunit;

namespace v2rayN.Tests;

/// <summary>
/// The kill switch blocks DNS outside the tunnel and Windows resolves names in the system DNS
/// client: the API's addresses are pinned before arming and dialled directly.
/// </summary>
public class ColituPinnedHostsTests
{
    [Fact]
    public void Order_PutsIPv4First_DropsPlaceholdersAndDuplicates()
    {
        var ordered = ColituPinnedHosts.Order(
        [
            IPAddress.Parse("2a01:4f8::1"),
            IPAddress.Parse("198.18.0.5"),
            IPAddress.Parse("203.0.113.10"),
            null,
            IPAddress.Parse("203.0.113.10"),
            IPAddress.Parse("::ffff:203.0.113.11"),
            IPAddress.Loopback,
            IPAddress.Any
        ]);

        ordered.Select(address => address.ToString()).Should().Equal("203.0.113.10", "203.0.113.11", "2a01:4f8::1");
    }

    [Fact]
    public void Set_KeepsLastGoodAddresses_WhenResolutionFails()
    {
        var host = $"api-{Guid.NewGuid():N}.test";
        ColituPinnedHosts.Set(host, [IPAddress.Parse("203.0.113.20")]).Should().BeTrue();

        ColituPinnedHosts.Set(host, [null, IPAddress.Parse("198.18.0.1")]).Should().BeFalse();

        ColituPinnedHosts.AddressesOf(host).Should().Equal(IPAddress.Parse("203.0.113.20"));
        ColituPinnedHosts.AddressesOf(host.ToUpperInvariant()).Should().HaveCount(1);
    }

    [Fact]
    public void Candidates_PinnedAddressesFirst_ThenTheHostName()
    {
        var host = $"api-{Guid.NewGuid():N}.test";
        ColituPinnedHosts.Set(host, [IPAddress.Parse("2a01:4f8::2"), IPAddress.Parse("203.0.113.30")]);

        var candidates = ColituPinnedHosts.Candidates(new DnsEndPoint(host, 443));

        candidates.Should().HaveCount(3);
        candidates[0].Should().Be(new IPEndPoint(IPAddress.Parse("203.0.113.30"), 443));
        candidates[1].Should().Be(new IPEndPoint(IPAddress.Parse("2a01:4f8::2"), 443));
        candidates[2].Should().BeOfType<DnsEndPoint>().Which.Host.Should().Be(host);
    }

    [Fact]
    public void Candidates_UnknownHost_IsTheDefaultBehaviour()
    {
        var endpoint = new DnsEndPoint($"other-{Guid.NewGuid():N}.test", 443);

        ColituPinnedHosts.Candidates(endpoint).Should().Equal(endpoint);
    }

    [Fact]
    public void Candidates_DohResolvers_NeedNoLookup()
    {
        ColituPinnedHosts.Candidates(new DnsEndPoint("cloudflare-dns.com", 443))[0]
            .Should().Be(new IPEndPoint(IPAddress.Parse("1.1.1.1"), 443));
        ColituPinnedHosts.Candidates(new DnsEndPoint("dns.google", 443))[0]
            .Should().Be(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 443));
    }

    [Fact]
    public async Task ConnectAsync_DialsThePinnedAddress_NotTheName()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        // A name that cannot resolve: only the pinned address can connect. A dead address is
        // tried first (refused at once), then the next pinned one.
        var host = $"pinned-{Guid.NewGuid():N}.invalid";
        ColituPinnedHosts.PinForTest(host, [IPAddress.Parse("127.0.0.2"), IPAddress.Loopback]);

        var accept = listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
        await using var stream = await ColituPinnedHosts.ConnectToAsync(new DnsEndPoint(host, port), TestContext.Current.CancellationToken);

        using var accepted = await accept;
        accepted.Connected.Should().BeTrue();
    }
}
