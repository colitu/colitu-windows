using System.Net;

namespace v2rayN.Services;

/// <summary>
/// Kill switch built on the Windows Filtering Platform, the same technique as
/// the WireGuard client. While engaged, outbound connections are blocked
/// except for the VPN core, this app, loopback (the local proxy), the TUN
/// adapter, the local network and DHCP. Filters live in a dynamic WFP session:
/// Windows removes them when the session closes, so a crash can never leave
/// the computer permanently offline.
/// </summary>
public sealed class ColituKillSwitch
{
    private static readonly Guid SublayerKey = new("7c1f5f3e-2f7a-4d0c-9b8e-c01170b1a5e1");
    private static readonly Guid LayerConnectV4 = new("c38d57d1-05a7-4c33-904f-7fbceee60e82");
    private static readonly Guid LayerConnectV6 = new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4");
    private static readonly Guid ConditionAppId = new("d78e1e87-8644-4ea5-9437-d809ecefc971");
    private static readonly Guid ConditionFlags = new("632ce23b-5167-435c-86d7-e903684aa80c");
    private static readonly Guid ConditionRemoteAddress = new("b235ae9a-1d64-49b8-a44c-5ff3d9095045");
    private static readonly Guid ConditionLocalAddress = new("d9ee00de-c1ef-4617-bfe3-ffd8f5a08957");
    private static readonly Guid ConditionRemotePort = new("c35a604d-d22b-4e1a-91b4-68f674ee674b");
    private static readonly Guid ConditionProtocol = new("3971ef2b-623e-4f9a-8cb1-6e79b806b9a7");

    private const uint RpcCAuthnWinNt = 10;
    private const uint SessionFlagDynamic = 0x1;
    private const uint ActionBlock = 0x1 | 0x1000;
    private const uint ActionPermit = 0x2 | 0x1000;
    private const uint FlagIsLoopback = 0x1;
    private const uint TypeUInt8 = 1;
    private const uint TypeUInt16 = 2;
    private const uint TypeUInt32 = 3;
    private const uint TypeByteBlob = 12;
    private const uint TypeV4AddrMask = 0x100;
    private const uint TypeV6AddrMask = 0x101;
    private const uint MatchEqual = 0;
    private const uint MatchFlagsAllSet = 6;

    // The TUN adapter of the Xray core (ServiceLib Sample/SampleTunInbound).
    internal static readonly (IPAddress Address, int Prefix)[] TunNetworks =
    [
        (IPAddress.Parse("172.18.0.0"), 30),
        (IPAddress.Parse("fdfe:dcba:9876::"), 126)
    ];

    // Local network, link-local, multicast and broadcast stay reachable (printers, casting, the router page).
    internal static readonly (IPAddress Address, int Prefix)[] LocalNetworks =
    [
        (IPAddress.Parse("10.0.0.0"), 8),
        (IPAddress.Parse("172.16.0.0"), 12),
        (IPAddress.Parse("192.168.0.0"), 16),
        (IPAddress.Parse("169.254.0.0"), 16),
        (IPAddress.Parse("224.0.0.0"), 4),
        (IPAddress.Parse("255.255.255.255"), 32),
        (IPAddress.Parse("fe80::"), 10),
        (IPAddress.Parse("fc00::"), 7),
        (IPAddress.Parse("ff00::"), 8)
    ];

    private readonly object _gate = new();
    private IntPtr _engine;

    public bool IsEngaged
    {
        get
        {
            lock (_gate)
            {
                return _engine != IntPtr.Zero;
            }
        }
    }

    /// <summary>Blocks all traffic except the listed programs and the exceptions above.</summary>
    public void Engage(IEnumerable<string> allowedPrograms)
    {
        lock (_gate)
        {
            if (_engine != IntPtr.Zero)
            {
                return;
            }

            using var memory = new NativeMemory();
            var session = new FwpmSession0
            {
                displayData = memory.Display("Colitu VPN kill switch"),
                flags = SessionFlagDynamic,
                txnWaitTimeoutInMSec = 5000
            };
            Check(FwpmEngineOpen0(null, RpcCAuthnWinNt, IntPtr.Zero, ref session, out var engine), "FwpmEngineOpen0");

            var committed = false;
            try
            {
                Check(FwpmTransactionBegin0(engine, 0), "FwpmTransactionBegin0");
                var sublayer = new FwpmSublayer0
                {
                    subLayerKey = SublayerKey,
                    displayData = memory.Display("Colitu VPN kill switch"),
                    weight = 0xFFFF
                };
                Check(FwpmSubLayerAdd0(engine, ref sublayer, IntPtr.Zero), "FwpmSubLayerAdd0");

                foreach (var layer in new[] { LayerConnectV4, LayerConnectV6 })
                {
                    var v6 = layer == LayerConnectV6;

                    AddFilter(engine, memory, layer, 15, ActionPermit, "Permit loopback",
                        Condition(ConditionFlags, MatchFlagsAllSet, TypeUInt32, (IntPtr)FlagIsLoopback));

                    foreach (var program in allowedPrograms.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        AddFilter(engine, memory, layer, 14, ActionPermit, "Permit Colitu program",
                            Condition(ConditionAppId, MatchEqual, TypeByteBlob, memory.AppId(program)));
                    }

                    foreach (var (address, prefix) in TunNetworks.Where(net => (net.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6) == v6))
                    {
                        AddFilter(engine, memory, layer, 13, ActionPermit, "Permit traffic through the VPN adapter",
                            Condition(ConditionLocalAddress, MatchEqual, v6 ? TypeV6AddrMask : TypeV4AddrMask, memory.AddressMask(address, prefix)));
                    }

                    // DNS must go through the tunnel; a LAN resolver would reveal browsing.
                    AddFilter(engine, memory, layer, 12, ActionBlock, "Block DNS outside the VPN",
                        Condition(ConditionRemotePort, MatchEqual, TypeUInt16, (IntPtr)53));

                    foreach (var (address, prefix) in LocalNetworks.Where(net => (net.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6) == v6))
                    {
                        AddFilter(engine, memory, layer, 11, ActionPermit, "Permit local network",
                            Condition(ConditionRemoteAddress, MatchEqual, v6 ? TypeV6AddrMask : TypeV4AddrMask, memory.AddressMask(address, prefix)));
                    }

                    AddFilter(engine, memory, layer, 11, ActionPermit, "Permit DHCP",
                        Condition(ConditionProtocol, MatchEqual, TypeUInt8, (IntPtr)17),
                        Condition(ConditionRemotePort, MatchEqual, TypeUInt16, (IntPtr)(v6 ? 547 : 67)));

                    AddFilter(engine, memory, layer, 0, ActionBlock, "Block everything else");
                }

                Check(FwpmTransactionCommit0(engine), "FwpmTransactionCommit0");
                committed = true;
                _engine = engine;
            }
            finally
            {
                if (!committed)
                {
                    FwpmTransactionAbort0(engine);
                    FwpmEngineClose0(engine);
                }
            }
        }
    }

    /// <summary>Removes every filter (closing the dynamic session deletes them).</summary>
    public void Release()
    {
        lock (_gate)
        {
            if (_engine == IntPtr.Zero)
            {
                return;
            }
            FwpmEngineClose0(_engine);
            _engine = IntPtr.Zero;
        }
    }

    private static FwpmFilterCondition0 Condition(Guid field, uint match, uint type, IntPtr value) => new()
    {
        fieldKey = field,
        matchType = match,
        conditionValue = new FwpValue0 { type = type, value = value }
    };

    private static void AddFilter(IntPtr engine, NativeMemory memory, Guid layer, byte weight, uint action, string name, params FwpmFilterCondition0[] conditions)
    {
        var filter = new FwpmFilter0
        {
            filterKey = Guid.NewGuid(),
            displayData = memory.Display(name),
            layerKey = layer,
            subLayerKey = SublayerKey,
            weight = new FwpValue0 { type = TypeUInt8, value = (IntPtr)weight },
            numFilterConditions = (uint)conditions.Length,
            filterCondition = conditions.Length == 0 ? IntPtr.Zero : memory.Array(conditions),
            action = new FwpmAction0 { type = action }
        };
        Check(FwpmFilterAdd0(engine, ref filter, IntPtr.Zero, out _), $"FwpmFilterAdd0 ({name})");
    }

    private static void Check(uint result, string call)
    {
        if (result != 0)
        {
            throw new InvalidOperationException($"{call} failed: 0x{result:X8}");
        }
    }

    /// <summary>
    /// FWP_V4_ADDR_AND_MASK takes host-order integers; FWP_V6_ADDR_AND_MASK takes
    /// the 16 address bytes followed by the prefix length.
    /// </summary>
    internal static byte[] EncodeAddressMask(IPAddress address, int prefix)
    {
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4)
        {
            var addr = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
            var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
            var result = new byte[8];
            BitConverter.GetBytes(addr & mask).CopyTo(result, 0);
            BitConverter.GetBytes(mask).CopyTo(result, 4);
            return result;
        }

        var v6 = new byte[17];
        bytes.CopyTo(v6, 0);
        v6[16] = (byte)prefix;
        return v6;
    }

    /// <summary>Unmanaged allocations that must outlive the WFP calls of one transaction.</summary>
    private sealed class NativeMemory : IDisposable
    {
        private readonly List<IntPtr> _hGlobal = [];
        private readonly List<IntPtr> _wfp = [];

        public FwpmDisplayData0 Display(string name) => new() { name = Keep(Marshal.StringToHGlobalUni(name)) };

        public IntPtr AddressMask(IPAddress address, int prefix)
        {
            var bytes = EncodeAddressMask(address, prefix);
            var pointer = Keep(Marshal.AllocHGlobal(bytes.Length));
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            return pointer;
        }

        public IntPtr AppId(string path)
        {
            Check(FwpmGetAppIdFromFileName0(path, out var blob), $"FwpmGetAppIdFromFileName0 ({path})");
            _wfp.Add(blob);
            return blob;
        }

        public IntPtr Array(FwpmFilterCondition0[] conditions)
        {
            var size = Marshal.SizeOf<FwpmFilterCondition0>();
            var pointer = Keep(Marshal.AllocHGlobal(size * conditions.Length));
            for (var i = 0; i < conditions.Length; i++)
            {
                Marshal.StructureToPtr(conditions[i], pointer + i * size, false);
            }
            return pointer;
        }

        private IntPtr Keep(IntPtr pointer)
        {
            _hGlobal.Add(pointer);
            return pointer;
        }

        public void Dispose()
        {
            foreach (var pointer in _hGlobal)
            {
                Marshal.FreeHGlobal(pointer);
            }
            for (var i = 0; i < _wfp.Count; i++)
            {
                var pointer = _wfp[i];
                FwpmFreeMemory0(ref pointer);
            }
        }
    }

    // ── fwpuclnt.dll (layouts for 64-bit Windows; see fwpmtypes.h) ──────────
    [StructLayout(LayoutKind.Sequential)]
    internal struct FwpmDisplayData0
    {
        public IntPtr name;
        public IntPtr description;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FwpByteBlob
    {
        public uint size;
        public IntPtr data;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FwpValue0
    {
        public uint type;
        public IntPtr value;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FwpmFilterCondition0
    {
        public Guid fieldKey;
        public uint matchType;
        public FwpValue0 conditionValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FwpmAction0
    {
        public uint type;
        public Guid filterType;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FwpmFilter0
    {
        public Guid filterKey;
        public FwpmDisplayData0 displayData;
        public uint flags;
        public IntPtr providerKey;
        public FwpByteBlob providerData;
        public Guid layerKey;
        public Guid subLayerKey;
        public FwpValue0 weight;
        public uint numFilterConditions;
        public IntPtr filterCondition;
        public FwpmAction0 action;
        public uint alignment; // the following union holds a UINT64, so it starts on an 8-byte boundary
        public Guid providerContextKey;
        public IntPtr reserved;
        public ulong filterId;
        public FwpValue0 effectiveWeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FwpmSession0
    {
        public Guid sessionKey;
        public FwpmDisplayData0 displayData;
        public uint flags;
        public uint txnWaitTimeoutInMSec;
        public uint processId;
        public IntPtr sid;
        public IntPtr username;
        public int kernelMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FwpmSublayer0
    {
        public Guid subLayerKey;
        public FwpmDisplayData0 displayData;
        public uint flags;
        public IntPtr providerKey;
        public FwpByteBlob providerData;
        public ushort weight;
    }

    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)]
    private static extern uint FwpmEngineOpen0(string? serverName, uint authnService, IntPtr authIdentity, ref FwpmSession0 session, out IntPtr engineHandle);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmEngineClose0(IntPtr engineHandle);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmTransactionBegin0(IntPtr engineHandle, uint flags);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmTransactionCommit0(IntPtr engineHandle);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmTransactionAbort0(IntPtr engineHandle);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmSubLayerAdd0(IntPtr engineHandle, ref FwpmSublayer0 subLayer, IntPtr securityDescriptor);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmFilterAdd0(IntPtr engineHandle, ref FwpmFilter0 filter, IntPtr securityDescriptor, out ulong id);

    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)]
    private static extern uint FwpmGetAppIdFromFileName0(string fileName, out IntPtr appId);

    [DllImport("fwpuclnt.dll")]
    private static extern void FwpmFreeMemory0(ref IntPtr p);
}
