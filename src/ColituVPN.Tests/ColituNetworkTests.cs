using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using AwesomeAssertions;
using v2rayN.Services;
using Xunit;

namespace v2rayN.Tests;

public class ColituNetworkTests
{
    [Theory]
    [InlineData("198.18.0.36", true)]
    [InlineData("198.19.255.1", true)]
    [InlineData("198.17.0.1", false)]
    [InlineData("45.138.101.216", false)]
    [InlineData("192.168.0.1", false)]
    public void FakeIpPool_IsRecognised(string address, bool placeholder)
    {
        ColituNetwork.IsPlaceholder(IPAddress.Parse(address)).Should().Be(placeholder);
    }

    [Fact]
    public void PhysicalInterface_IsNeverAVirtualAdapter()
    {
        var name = ColituNetwork.PhysicalInterfaceName();
        if (name == null)
        {
            return; // no physical adapter with a gateway on this machine (CI)
        }
        var adapter = NetworkInterface.GetAllNetworkInterfaces().Single(item => item.Name == name);
        adapter.NetworkInterfaceType.Should().BeOneOf(NetworkInterfaceType.Ethernet, NetworkInterfaceType.Wireless80211, NetworkInterfaceType.GigabitEthernet);
        adapter.Description.Should().NotContainAny("Wintun", "TAP", "WireGuard", "Hyper-V", "VMware", "VirtualBox");
    }

    [Fact]
    public async Task LiteralAddress_IsReturnedWithoutLookup()
    {
        (await ColituNetwork.ResolveServerAsync("45.138.101.216")).Should().Be(IPAddress.Parse("45.138.101.216"));
    }

    [Fact]
    public void ShareLink_UsesThePinnedAddressAndKeepsTheNameForTls()
    {
        var payload = JsonDocument.Parse("""{"schema_version":1,"protocol":"trojan","endpoint":{"host":"vpn.example.com","port":443},"credentials":{"password":"p"},"transport":{"type":"tcp"},"security":{"type":"tls","server_name":"vpn.example.com"}}""").RootElement.Clone();

        ColituShareLinkBuilder.HostOf(payload).Should().Be("vpn.example.com");
        var link = ColituShareLinkBuilder.Build(payload, "x", "203.0.113.7");

        link.Should().StartWith("trojan://p@203.0.113.7:443?").And.Contain("sni=vpn.example.com");
    }

    [Fact]
    public void ShareLink_WithoutPin_DialsTheName()
    {
        var payload = JsonDocument.Parse("""{"schema_version":1,"protocol":"shadowsocks","endpoint":{"host":"vpn.example.com","port":8388},"credentials":{"method":"aes-128-gcm","password":"p"},"transport":{"type":"tcp"},"security":{"type":"none"}}""").RootElement.Clone();

        ColituShareLinkBuilder.Build(payload, "x").Should().Contain("@vpn.example.com:8388");
    }
}
