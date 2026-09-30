using System.Net;
using System.Runtime.InteropServices;
using AwesomeAssertions;
using v2rayN.Services;
using Xunit;

namespace v2rayN.Tests;

public class ColituKillSwitchTests
{
    // Sizes of the fwpmtypes.h structures on 64-bit Windows (same layout as the WireGuard client uses).
    [Fact]
    public void NativeStructures_MatchTheWindowsLayout()
    {
        Environment.Is64BitProcess.Should().BeTrue();
        Marshal.SizeOf<ColituKillSwitch.FwpValue0>().Should().Be(16);
        Marshal.SizeOf<ColituKillSwitch.FwpmFilterCondition0>().Should().Be(40);
        Marshal.SizeOf<ColituKillSwitch.FwpmSession0>().Should().Be(72);
        Marshal.SizeOf<ColituKillSwitch.FwpmSublayer0>().Should().Be(72);
        Marshal.SizeOf<ColituKillSwitch.FwpmFilter0>().Should().Be(200);
        Marshal.OffsetOf<ColituKillSwitch.FwpmFilter0>("action").ToInt32().Should().Be(128);
        Marshal.OffsetOf<ColituKillSwitch.FwpmFilter0>("providerContextKey").ToInt32().Should().Be(152);
        Marshal.OffsetOf<ColituKillSwitch.FwpmFilter0>("filterId").ToInt32().Should().Be(176);
    }

    [Fact]
    public void Ipv4Mask_IsEncodedAsHostOrderIntegers()
    {
        var bytes = ColituKillSwitch.EncodeAddressMask(IPAddress.Parse("192.168.0.0"), 16);

        BitConverter.ToUInt32(bytes, 0).Should().Be(0xC0A80000);
        BitConverter.ToUInt32(bytes, 4).Should().Be(0xFFFF0000);
    }

    [Fact]
    public void Ipv4Mask_ClearsHostBits()
    {
        var bytes = ColituKillSwitch.EncodeAddressMask(IPAddress.Parse("172.18.0.1"), 30);

        BitConverter.ToUInt32(bytes, 0).Should().Be(0xAC120000);
        BitConverter.ToUInt32(bytes, 4).Should().Be(0xFFFFFFFC);
    }

    [Fact]
    public void Ipv6Mask_IsAddressBytesThenPrefix()
    {
        var bytes = ColituKillSwitch.EncodeAddressMask(IPAddress.Parse("fe80::"), 10);

        bytes.Should().HaveCount(17);
        bytes[0].Should().Be(0xFE);
        bytes[1].Should().Be(0x80);
        bytes[16].Should().Be(10);
    }

    [Fact]
    public void TunAdapterNetworks_MatchTheXrayTunInbound()
    {
        ColituKillSwitch.TunNetworks.Select(net => $"{net.Address}/{net.Prefix}")
            .Should().BeEquivalentTo(["172.18.0.0/30", "fdfe:dcba:9876::/126"]);
    }
}
