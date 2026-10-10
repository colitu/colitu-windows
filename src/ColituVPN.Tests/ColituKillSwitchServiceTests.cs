using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using AwesomeAssertions;
using Colitu.KillSwitch;
using v2rayN.Services;
using Xunit;

namespace v2rayN.Tests;

/// <summary>Windows 2.6.0: the kill-switch service — message validation, allow lists, state machine.</summary>
public class ColituKillSwitchServiceTests
{
    private static readonly KsInstallLayout Layout = KsInstallLayout.FromDirectory(@"C:\Program Files\Colitu VPN");

    private static KsRequest Arm(KsArm arm) => new() { V = KsProtocol.Version, Cmd = KsProtocol.CmdArm, Arm = arm, Reason = "test" };

    private static KsArm EndpointsArm(params KsEndpoint[] endpoints) => new()
    {
        CoreAccess = KsProtocol.CoreAccessEndpoints,
        Endpoints = endpoints.ToList()
    };

    // ── Message validation ────────────────────────────────────────────────
    [Fact]
    public void Status_AndDisarm_AreValid()
    {
        KsValidator.TryValidate(new KsRequest { V = 1, Cmd = "status" }, out var status, out _).Should().BeTrue();
        status!.Cmd.Should().Be("status");
        KsValidator.TryValidate(new KsRequest { V = 1, Cmd = "disarm", Purge = true, Reason = "uninstall" }, out var disarm, out _).Should().BeTrue();
        disarm!.Purge.Should().BeTrue();
    }

    [Theory]
    [InlineData(0, "status")]
    [InlineData(2, "status")]
    [InlineData(1, "exec")]
    [InlineData(1, "")]
    [InlineData(1, null)]
    [InlineData(1, "STATUS")]
    public void WrongVersionOrUnknownCommand_IsRejected(int version, string? cmd)
    {
        KsValidator.TryValidate(new KsRequest { V = version, Cmd = cmd }, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void ArgumentsThatDoNotBelongToTheCommand_AreRejected()
    {
        KsValidator.TryValidate(new KsRequest { V = 1, Cmd = "status", Purge = true }, out _, out _).Should().BeFalse();
        KsValidator.TryValidate(new KsRequest { V = 1, Cmd = "disarm", Arm = new KsArm { CoreAccess = "full" } }, out _, out _).Should().BeFalse();
        KsValidator.TryValidate(new KsRequest { V = 1, Cmd = "arm", Purge = true, Arm = new KsArm { CoreAccess = "full" } }, out _, out _).Should().BeFalse();
        KsValidator.TryValidate(new KsRequest { V = 1, Cmd = "arm" }, out _, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("203.0.113.7", true)]
    [InlineData("2001:db8::1", true)]
    [InlineData("example.com", false)]
    [InlineData("0x7f.1", false)]
    [InlineData("1", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("::", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("fe80::1%12", false)]
    [InlineData("::ffff:1.2.3.4", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Endpoints_TakeOnlyUnicastIpLiterals(string? ip, bool valid)
    {
        var request = Arm(EndpointsArm(new KsEndpoint { Ip = ip, Port = 443, Proto = "tcp" }));
        KsValidator.TryValidate(request, out _, out _).Should().Be(valid);
    }

    [Theory]
    [InlineData(0, "tcp", false)]
    [InlineData(65536, "tcp", false)]
    [InlineData(443, "sctp", false)]
    [InlineData(443, null, false)]
    [InlineData(443, "udp", true)]
    [InlineData(443, "any", true)]
    public void Endpoints_NeedAPortAndAKnownProtocol(int port, string? proto, bool valid)
    {
        var request = Arm(EndpointsArm(new KsEndpoint { Ip = "203.0.113.7", Port = port, Proto = proto }));
        KsValidator.TryValidate(request, out _, out _).Should().Be(valid);
    }

    [Fact]
    public void TooManyEntries_AreRejected()
    {
        var endpoints = Enumerable.Range(1, KsProtocol.MaxEndpoints + 1).Select(i => new KsEndpoint { Ip = $"203.0.113.{i % 250 + 1}", Port = 1000 + i, Proto = "tcp" }).ToArray();
        KsValidator.TryValidate(Arm(EndpointsArm(endpoints)), out _, out _).Should().BeFalse();

        var apps = Enumerable.Range(1, KsProtocol.MaxApps + 1).Select(i => $@"C:\Apps\app{i}.exe").ToList();
        KsValidator.TryValidate(Arm(new KsArm { CoreAccess = "full", SplitMode = "bypass", Apps = apps }), out _, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(@"C:\Program Files\Game\game.exe", true)]
    [InlineData(@"D:\Apps\Browser.EXE", true)]
    [InlineData(@"\\server\share\app.exe", false)]
    [InlineData(@"\\?\C:\app.exe", false)]
    [InlineData(@"C:\Apps\..\Windows\cmd.exe", false)]
    [InlineData(@"C:\Apps\app.exe:stream", false)]
    [InlineData(@"C:\Apps\*.exe", false)]
    [InlineData(@"C:\Apps\app.dll", false)]
    [InlineData(@"app.exe", false)]
    [InlineData(@"C:/Apps/app.exe", false)]
    [InlineData(@"C:\Apps\\app.exe", false)]
    [InlineData("C:\\Apps\\a\npp.exe", false)]
    public void SplitApps_MustBePlainLocalExePaths(string path, bool valid)
    {
        KsValidator.IsPlainExePath(path).Should().Be(valid);
    }

    [Fact]
    public void SplitEntries_NeedASplitMode()
    {
        var request = Arm(new KsArm { CoreAccess = "full", SplitMode = "off", Networks = ["198.51.100.0/24"] });
        KsValidator.TryValidate(request, out _, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("198.51.100.7/24", "198.51.100.0/24")]
    [InlineData("198.51.100.7", "198.51.100.7/32")]
    [InlineData("2001:db8::1/32", "2001:db8::/32")]
    public void Networks_AreCanonical(string text, string expected)
    {
        KsValidator.TryParseNetwork(text, out var network).Should().BeTrue();
        network.ToString().Should().Be(expected);
    }

    [Theory]
    [InlineData("198.51.100.0/33")]
    [InlineData("198.51.100.0/-1")]
    [InlineData("198.51.100.0/24/1")]
    [InlineData("example.com/24")]
    [InlineData("10")]
    public void BadNetworks_AreRejected(string text)
    {
        KsValidator.TryParseNetwork(text, out _).Should().BeFalse();
    }

    [Fact]
    public void ReasonsWithControlCharacters_OrTooLong_AreRejected()
    {
        KsValidator.TryValidate(new KsRequest { V = 1, Cmd = "status", Reason = "a\nb" }, out _, out _).Should().BeFalse();
        KsValidator.TryValidate(new KsRequest { V = 1, Cmd = "status", Reason = new string('a', KsProtocol.MaxReasonLength + 1) }, out _, out _).Should().BeFalse();
        KsValidator.TryValidate(new KsRequest { V = 1, Cmd = "status", Id = "id with spaces" }, out _, out _).Should().BeFalse();
    }

    // ── Codec ─────────────────────────────────────────────────────────────
    [Fact]
    public void UnknownJsonMembers_AreRejected()
    {
        KsCodec.TryDecodeRequest(Encoding.UTF8.GetBytes("""{"v":1,"cmd":"status","run":"calc.exe"}"""), out _, out _).Should().BeFalse();
        KsCodec.TryDecodeRequest(Encoding.UTF8.GetBytes("""{"v":1,"cmd":"status"}"""), out var ok, out _).Should().BeTrue();
        ok!.Cmd.Should().Be("status");
        KsCodec.TryDecodeRequest(Encoding.UTF8.GetBytes("not json"), out _, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Requests_RoundTripThroughTheFrame()
    {
        using var stream = new MemoryStream();
        var request = Arm(EndpointsArm(new KsEndpoint { Ip = "203.0.113.7", Port = 443, Proto = "udp" }));
        await KsCodec.WriteFrameAsync(stream, KsCodec.Encode(request), TestContext.Current.CancellationToken);
        stream.Position = 0;
        var payload = await KsCodec.ReadFrameAsync(stream, TestContext.Current.CancellationToken);
        KsCodec.TryDecodeRequest(payload, out var decoded, out _).Should().BeTrue();
        decoded!.Arm!.Endpoints!.Single().Port.Should().Be(443);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(KsProtocol.MaxMessageBytes + 1)]
    public async Task FramesOutsideTheLimit_AreRefusedBeforeReading(int length)
    {
        using var stream = new MemoryStream();
        stream.Write(BitConverter.GetBytes(length));
        stream.Write(new byte[16]);
        stream.Position = 0;
        var read = () => KsCodec.ReadFrameAsync(stream, TestContext.Current.CancellationToken);
        await read.Should().ThrowAsync<InvalidDataException>();
    }

    // ── Allow list → filters ──────────────────────────────────────────────
    private static IReadOnlyList<KsFilter> Plan(KsArm arm)
    {
        KsValidator.TryValidateArm(arm, out var valid, out var error).Should().BeTrue(error);
        return KsPlanBuilder.Build(valid!, Layout);
    }

    [Fact]
    public void EveryLayer_PermitsLoopbackAppAndAdapter_AndBlocksTheRest()
    {
        var filters = Plan(EndpointsArm());
        foreach (var layer in Enum.GetValues<KsLayer>())
        {
            var mine = filters.Where(filter => filter.Layer == layer).ToList();
            mine.Should().Contain(filter => filter.Permit && filter.Weight == 15 && filter.Conditions.OfType<KsLoopback>().Any());
            mine.Should().Contain(filter => filter.Permit && filter.Conditions.OfType<KsApp>().Any(app => app.Path == Layout.AppPath));
            mine.Should().Contain(filter => filter.Permit && filter.Conditions.OfType<KsLocalNetwork>().Any());
            mine.Should().Contain(filter => !filter.Permit && filter.Weight == 0 && filter.Conditions.Count == 0);
            mine.Should().Contain(filter => !filter.Permit && filter.Conditions.OfType<KsRemotePort>().Any(port => port.Port == 53));
        }
    }

    [Fact]
    public void Cores_ReachOnlyTheServerEndpoints_WhenRestricted()
    {
        var filters = Plan(EndpointsArm(
            new KsEndpoint { Ip = "203.0.113.7", Port = 443, Proto = "tcp" },
            new KsEndpoint { Ip = "2001:db8::7", Port = 8443, Proto = "udp" }));

        filters.Should().NotContain(filter => filter.Permit && filter.Conditions.All(c => c is KsApp) && filter.Conditions.OfType<KsApp>().Any(app => Layout.CorePaths.Contains(app.Path)));
        var v4 = filters.Single(filter => filter.Layer == KsLayer.ConnectV4 && filter.Conditions.OfType<KsRemotePort>().Any(port => port.Port == 443));
        v4.Conditions.OfType<KsRemoteNetwork>().Single().Network.ToString().Should().Be("203.0.113.7/32");
        v4.Conditions.OfType<KsProtocolCondition>().Single().Protocol.Should().Be(6);
        v4.Conditions.OfType<KsApp>().Select(app => app.Path).Should().BeEquivalentTo(Layout.CorePaths);
        var v6 = filters.Single(filter => filter.Layer == KsLayer.ConnectV6 && filter.Conditions.OfType<KsRemotePort>().Any(port => port.Port == 8443));
        v6.Conditions.OfType<KsProtocolCondition>().Single().Protocol.Should().Be(17);
        // An IPv4 server never opens anything on the IPv6 layer, and inbound layers stay closed to it.
        filters.Where(filter => filter.Layer == KsLayer.ConnectV6).Should().NotContain(filter => filter.Conditions.OfType<KsRemotePort>().Any(port => port.Port == 443));
        filters.Where(filter => !filter.IsOutbound).Should().NotContain(filter => filter.Conditions.OfType<KsRemotePort>().Any(port => port.Port == 443 || port.Port == 8443));
    }

    [Fact]
    public void FullCoreAccess_PermitsTheCoresAnywhere()
    {
        var filters = Plan(new KsArm { CoreAccess = KsProtocol.CoreAccessFull });
        filters.Should().Contain(filter => filter.Layer == KsLayer.ConnectV4 && filter.Permit
            && filter.Conditions.All(c => c is KsApp) && filter.Conditions.Count == Layout.CorePaths.Count);
    }

    [Fact]
    public void LocalNetwork_IsOptional()
    {
        var withLan = Plan(new KsArm { CoreAccess = "full", AllowLan = true });
        var withoutLan = Plan(new KsArm { CoreAccess = "full", AllowLan = false });

        static IEnumerable<string> Networks(IEnumerable<KsFilter> filters) => filters
            .Where(filter => filter.Layer == KsLayer.ConnectV4 && filter.Permit)
            .SelectMany(filter => filter.Conditions.OfType<KsRemoteNetwork>())
            .Select(remote => remote.Network.ToString());

        Networks(withLan).Should().Contain(["192.168.0.0/16", "10.0.0.0/8"]);
        Networks(withoutLan).Should().NotContain(["192.168.0.0/16", "10.0.0.0/8", "172.16.0.0/12"]);
        // DHCP and link-local keep working without the LAN.
        Networks(withoutLan).Should().Contain("169.254.0.0/16");
        withoutLan.Should().Contain(filter => filter.Layer == KsLayer.ConnectV4 && filter.Conditions.OfType<KsRemotePort>().Any(port => port.Port == 67));
        // ...but only from the DHCP client port: remote port 67/547 alone is a way out of the tunnel.
        withoutLan.Where(filter => filter.Permit && filter.Conditions.OfType<KsRemotePort>().Any(port => port.Port is 67 or 547))
            .Should().OnlyContain(filter => filter.Conditions.OfType<KsLocalPort>().Any(port => port.Port is 68 or 546));
    }

    [Fact]
    public void SplitBypass_LetsTheExcludedAppsAndNetworksOut()
    {
        var filters = Plan(new KsArm
        {
            CoreAccess = "full",
            SplitMode = "bypass",
            Apps = [@"C:\Games\game.exe"],
            Networks = ["198.51.100.0/24", "2001:db8::/32"]
        });
        filters.Should().Contain(filter => filter.Layer == KsLayer.ConnectV4 && filter.Permit && filter.Conditions.OfType<KsApp>().Any(app => app.Path == @"C:\Games\game.exe"));
        filters.Should().Contain(filter => filter.Layer == KsLayer.ConnectV4 && filter.Permit && filter.Conditions.OfType<KsRemoteNetwork>().Any(net => net.Network.ToString() == "198.51.100.0/24"));
        filters.Should().Contain(filter => filter.Layer == KsLayer.ConnectV6 && filter.Permit && filter.Conditions.OfType<KsRemoteNetwork>().Any(net => net.Network.ToString() == "2001:db8::/32"));
        filters.Should().Contain(filter => filter.Layer == KsLayer.ConnectV4 && !filter.Permit && filter.Weight == 0);
    }

    [Fact]
    public void SplitOnly_BlocksOnlyTheSelected_OutsideTheTunnel()
    {
        var filters = Plan(new KsArm
        {
            CoreAccess = "full",
            SplitMode = "only",
            Apps = [@"C:\Apps\browser.exe"],
            Networks = ["198.51.100.0/24"]
        });
        var v4 = filters.Where(filter => filter.Layer == KsLayer.ConnectV4).ToList();
        v4.Should().Contain(filter => filter.Permit && filter.Weight == 0 && filter.Conditions.Count == 0, "everything else goes direct");
        v4.Should().Contain(filter => !filter.Permit && filter.Conditions.OfType<KsApp>().Any(app => app.Path == @"C:\Apps\browser.exe"));
        v4.Should().Contain(filter => !filter.Permit && filter.Conditions.OfType<KsRemoteNetwork>().Any());
        // The adapter permit outranks the block: the selected apps still use the tunnel.
        var adapter = v4.Single(filter => filter.Permit && filter.Conditions.OfType<KsLocalNetwork>().Any());
        adapter.Weight.Should().BeGreaterThan(v4.Where(filter => !filter.Permit).Max(filter => filter.Weight));
    }

    [Fact]
    public void AddressMasks_UseTheWfpLayout()
    {
        var v4 = WfpFirewall.EncodeAddressMask(new KsNetwork(IPAddress.Parse("192.168.0.0"), 16));
        BitConverter.ToUInt32(v4, 0).Should().Be(0xC0A80000);
        BitConverter.ToUInt32(v4, 4).Should().Be(0xFFFF0000);
        var v6 = WfpFirewall.EncodeAddressMask(new KsNetwork(IPAddress.Parse("fe80::"), 10));
        v6.Should().HaveCount(17);
        v6[16].Should().Be(10);
    }

    [Fact]
    public void NativeStructures_MatchTheWindowsLayout()
    {
        System.Runtime.InteropServices.Marshal.SizeOf<WfpFirewall.FwpmFilter0>().Should().Be(200);
        System.Runtime.InteropServices.Marshal.SizeOf<WfpFirewall.FwpmSublayer0>().Should().Be(72);
        // FWPM_PROVIDER0: key 16, display data 16, flags 4 (+4), blob 16, service name 8.
        System.Runtime.InteropServices.Marshal.SizeOf<WfpFirewall.FwpmProvider0>().Should().Be(64);
        System.Runtime.InteropServices.Marshal.OffsetOf<WfpFirewall.FwpmFilter0>("subLayerKey").ToInt32().Should().Be(80);
    }

    // ── State machine ─────────────────────────────────────────────────────
    private sealed class FakeFirewall : IKsFirewall
    {
        public List<KsFilter> Filters { get; } = [];
        public bool Purged { get; private set; }
        public bool Fail { get; set; }

        public int Apply(IReadOnlyList<KsFilter> filters)
        {
            if (Fail)
            {
                throw new KsFirewallException("BFE stopped");
            }
            Filters.Clear();
            Filters.AddRange(filters);
            return filters.Count;
        }

        public void RemoveFilters() => Filters.Clear();

        public void RemoveAll()
        {
            Filters.Clear();
            Purged = true;
        }

        public int CountFilters() => Filters.Count;
    }

    private sealed class MemoryStore : IKsStateStore
    {
        public KsState? State { get; set; }

        public KsState? Load() => State == null ? null : System.Text.Json.JsonSerializer.Deserialize<KsState>(System.Text.Json.JsonSerializer.Serialize(State));

        public void Save(KsState state) => State = System.Text.Json.JsonSerializer.Deserialize<KsState>(System.Text.Json.JsonSerializer.Serialize(state));
    }

    private sealed class FakeSystem : IKsSystem
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
        public DateTimeOffset BootTime { get; set; } = new(2026, 10, 6, 8, 0, 0, TimeSpan.Zero);
        public Dictionary<int, DateTimeOffset> Processes { get; } = [];

        public DateTimeOffset? ProcessStartTime(int pid) => Processes.TryGetValue(pid, out var start) ? start : null;
    }

    private sealed class ListLog : IKsLog
    {
        public List<string> Lines { get; } = [];

        public void Write(string message) => Lines.Add(message);
    }

    private static (KsEngine Engine, FakeFirewall Firewall, MemoryStore Store, FakeSystem System) NewEngine(MemoryStore? store = null, FakeFirewall? firewall = null, FakeSystem? system = null)
    {
        firewall ??= new FakeFirewall();
        store ??= new MemoryStore();
        system ??= new FakeSystem();
        return (new KsEngine(firewall, store, system, new ListLog(), Layout), firewall, store, system);
    }

    private static KsValidRequest Valid(KsRequest request)
    {
        KsValidator.TryValidate(request, out var valid, out var error).Should().BeTrue(error);
        return valid!;
    }

    private static KsRequest ArmRequest(bool keepAfterReboot = false) => Arm(new KsArm
    {
        CoreAccess = "endpoints",
        Endpoints = [new KsEndpoint { Ip = "203.0.113.7", Port = 443, Proto = "tcp" }],
        KeepAfterReboot = keepAfterReboot
    });

    [Fact]
    public void Arm_ThenDisarm_AddsAndRemovesTheFilters()
    {
        var (engine, firewall, store, system) = NewEngine();
        system.Processes[4242] = system.Now;

        var armed = engine.Handle(Valid(ArmRequest()), 4242);
        armed.Ok.Should().BeTrue();
        armed.Status!.Armed.Should().BeTrue();
        armed.Status.OwnerAlive.Should().BeTrue();
        firewall.Filters.Should().NotBeEmpty();
        store.State!.Armed.Should().BeTrue();

        var disarmed = engine.Handle(Valid(new KsRequest { V = 1, Cmd = "disarm", Reason = "user" }), 4242);
        disarmed.Status!.Armed.Should().BeFalse();
        firewall.Filters.Should().BeEmpty();
        store.State!.Armed.Should().BeFalse();
        store.State.LastDisarmReason.Should().Be("user");
    }

    [Fact]
    public void AppCrash_LeavesTheFiltersInPlace()
    {
        var (engine, firewall, _, system) = NewEngine();
        system.Processes[4242] = system.Now;
        engine.Handle(Valid(ArmRequest()), 4242);

        system.Processes.Remove(4242); // the app died
        var status = engine.Status();

        status.Armed.Should().BeTrue();
        status.OwnerAlive.Should().BeFalse();
        firewall.Filters.Should().NotBeEmpty();
    }

    [Fact]
    public void ReusedProcessId_IsNotTheOwner()
    {
        var (engine, _, _, system) = NewEngine();
        system.Processes[4242] = system.Now;
        engine.Handle(Valid(ArmRequest()), 4242);
        system.Processes[4242] = system.Now.AddMinutes(5); // another process got the id
        engine.Status().OwnerAlive.Should().BeFalse();
    }

    [Fact]
    public void ServiceRestart_RestoresTheFiltersOfAnArmedSwitch()
    {
        var (engine, firewall, store, system) = NewEngine();
        engine.Handle(Valid(ArmRequest()), 1);
        var count = firewall.Filters.Count;
        firewall.Filters.Clear(); // deleted behind the service's back, or a service crash mid-way

        var (restarted, _, _, _) = NewEngine(store, firewall, system);
        restarted.OnServiceStart();

        restarted.Armed.Should().BeTrue();
        firewall.Filters.Should().HaveCount(count);
    }

    [Fact]
    public void Reboot_WithoutAutoReconnect_Disarms()
    {
        var (engine, firewall, store, system) = NewEngine();
        engine.Handle(Valid(ArmRequest(keepAfterReboot: false)), 1);

        system.BootTime = system.Now.AddHours(2);
        system.Now = system.Now.AddHours(2).AddMinutes(1);
        var (afterBoot, _, _, _) = NewEngine(store, firewall, system);
        afterBoot.OnServiceStart();

        afterBoot.Armed.Should().BeFalse();
        firewall.Filters.Should().BeEmpty();
        store.State!.LastDisarmReason.Should().Contain("restarted");
    }

    [Fact]
    public void Reboot_WithAutoReconnect_StaysClosed()
    {
        var (engine, firewall, store, system) = NewEngine();
        engine.Handle(Valid(ArmRequest(keepAfterReboot: true)), 1);

        system.BootTime = system.Now.AddHours(2);
        system.Now = system.Now.AddHours(2).AddMinutes(1);
        var (afterBoot, _, _, _) = NewEngine(store, firewall, system);
        afterBoot.OnServiceStart();

        afterBoot.Armed.Should().BeTrue();
        firewall.Filters.Should().NotBeEmpty();
        afterBoot.Status().ArmedBeforeBoot.Should().BeTrue();
    }

    [Fact]
    public void FiltersWithoutState_StayClosed()
    {
        var firewall = new FakeFirewall();
        firewall.Filters.Add(new KsFilter(KsLayer.ConnectV4, 0, false, "Block everything else", []));
        var (engine, _, _, _) = NewEngine(firewall: firewall);
        engine.OnServiceStart();
        engine.Armed.Should().BeTrue();
        firewall.Filters.Should().NotBeEmpty();
    }

    [Fact]
    public void StrayFilters_OfADisarmedSwitch_AreRemoved()
    {
        var store = new MemoryStore { State = new KsState { Armed = false } };
        var firewall = new FakeFirewall();
        firewall.Filters.Add(new KsFilter(KsLayer.ConnectV4, 0, false, "Block everything else", []));
        var (engine, _, _, _) = NewEngine(store, firewall);
        engine.OnServiceStart();
        engine.Armed.Should().BeFalse();
        firewall.Filters.Should().BeEmpty();
    }

    [Fact]
    public void ReArming_ReplacesTheAllowList()
    {
        var (engine, firewall, _, _) = NewEngine();
        engine.Handle(Valid(ArmRequest()), 1);
        engine.Handle(Valid(Arm(new KsArm { CoreAccess = "full" })), 1);
        firewall.Filters.Should().NotContain(filter => filter.Conditions.OfType<KsRemotePort>().Any(port => port.Port == 443));
        engine.Status().Armed.Should().BeTrue();
    }

    [Fact]
    public void FailedFirstArm_IsNotReportedAsArmed()
    {
        var firewall = new FakeFirewall { Fail = true };
        var (engine, _, store, _) = NewEngine(firewall: firewall);
        var response = engine.Handle(Valid(ArmRequest()), 1);
        response.Ok.Should().BeFalse();
        response.Error.Should().Be(KsProtocol.ErrFirewall);
        response.Status!.Armed.Should().BeFalse();
        store.State!.Armed.Should().BeFalse();
    }

    [Fact]
    public void FailedReArm_KeepsTheSwitchArmed()
    {
        var (engine, firewall, _, _) = NewEngine();
        engine.Handle(Valid(ArmRequest()), 1);
        firewall.Fail = true;
        var response = engine.Handle(Valid(ArmRequest()), 1);
        response.Ok.Should().BeFalse();
        response.Status!.Armed.Should().BeTrue();
        firewall.Filters.Should().NotBeEmpty();
    }

    [Fact]
    public void PurgeDisarm_RemovesProviderAndSublayer()
    {
        var (engine, firewall, _, _) = NewEngine();
        engine.Handle(Valid(ArmRequest()), 1);
        engine.Handle(Valid(new KsRequest { V = 1, Cmd = "disarm", Purge = true, Reason = "uninstall" }), 1);
        firewall.Purged.Should().BeTrue();
        firewall.Filters.Should().BeEmpty();
    }

    // ── Pipe security ─────────────────────────────────────────────────────
    [Fact]
    public void Pipe_IsOpenToSystemAndAdministratorsOnly()
    {
        var security = KsPipeServer.CreateSecurity();
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();
        security.AreAccessRulesProtected.Should().BeTrue();
        var allowed = rules.Where(rule => rule.AccessControlType == AccessControlType.Allow).Select(rule => (SecurityIdentifier)rule.IdentityReference).ToList();
        allowed.Should().BeEquivalentTo([
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)]);
        allowed.Should().NotContain(new SecurityIdentifier(WellKnownSidType.WorldSid, null));
        allowed.Should().NotContain(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null));
        allowed.Should().NotContain(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null));
        rules.Should().Contain(rule => rule.AccessControlType == AccessControlType.Deny
            && ((SecurityIdentifier)rule.IdentityReference).IsWellKnown(WellKnownSidType.NetworkSid));
    }

    // ── App side: the allow list the app sends ────────────────────────────
    [Fact]
    public void AppArm_PinsTheCoresToTheServers_WhenNothingGoesDirect()
    {
        var arm = ColituVpnService.BuildKillSwitchArm(new ColituVpnPreferences(PrivacyModeEnabled: true), tun: true,
            [("203.0.113.7", 443, "vless-reality"), ("203.0.113.7", 8443, "hysteria2"), ("203.0.113.7", 2053, "vless-xhttp")],
            directRouting: false, ["1.1.1.1"], keepAfterReboot: true);

        arm.CoreAccess.Should().Be(KsProtocol.CoreAccessEndpoints);
        arm.Endpoints!.Select(e => $"{e.Ip}:{e.Port}/{e.Proto}").Should().BeEquivalentTo(
            ["203.0.113.7:443/tcp", "203.0.113.7:8443/udp", "203.0.113.7:2053/any", "1.1.1.1:53/any"]);
        arm.KeepAfterReboot.Should().BeTrue();
        KsValidator.TryValidate(Arm(arm), out _, out var error).Should().BeTrue(error);
    }

    [Fact]
    public void AppArm_GivesTheCoresFullAccess_ForDirectRoutingOrHostNames()
    {
        ColituVpnService.BuildKillSwitchArm(new ColituVpnPreferences(), true, [("203.0.113.7", 443, "trojan")], directRouting: true, [], false)
            .CoreAccess.Should().Be(KsProtocol.CoreAccessFull);
        ColituVpnService.BuildKillSwitchArm(new ColituVpnPreferences(), true, [("node.example.com", 443, "trojan")], directRouting: false, [], false)
            .CoreAccess.Should().Be(KsProtocol.CoreAccessFull);
    }

    [Fact]
    public void AppArm_CarriesSplitTunnelEntries_AppsOnlyInTunMode()
    {
        var preferences = new ColituVpnPreferences(SplitTunnelMode: ColituSplitTunnelModes.Bypass,
            SplitTunnelApps: [@"C:\Games\game.exe"], SplitTunnelNetworks: ["198.51.100.0/24"], KillSwitchAllowLan: false);

        var tun = ColituVpnService.BuildKillSwitchArm(preferences, true, null, false, [], false);
        tun.SplitMode.Should().Be(KsProtocol.SplitBypass);
        tun.Apps.Should().Equal(@"C:\Games\game.exe");
        tun.Networks.Should().Equal("198.51.100.0/24");
        tun.AllowLan.Should().BeFalse();
        tun.CoreAccess.Should().Be(KsProtocol.CoreAccessFull);

        var proxy = ColituVpnService.BuildKillSwitchArm(preferences, false, null, false, [], false);
        proxy.Apps.Should().BeEmpty();
        proxy.Networks.Should().Equal("198.51.100.0/24");
    }
}
