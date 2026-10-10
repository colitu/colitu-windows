using System.Net;
using System.Runtime.InteropServices;

namespace Colitu.KillSwitch;

public interface IKsFirewall
{
    /// <summary>Atomically replaces every Colitu filter with <paramref name="filters"/> (one WFP transaction).</summary>
    int Apply(IReadOnlyList<KsFilter> filters);

    /// <summary>Removes every Colitu filter; the provider and sublayer stay (a later arm reuses them).</summary>
    void RemoveFilters();

    /// <summary>Removes the filters, the sublayer and the provider (uninstall).</summary>
    void RemoveAll();

    /// <summary>Number of filters in the Colitu sublayer.</summary>
    int CountFilters();
}

/// <summary>
/// Persistent Windows Filtering Platform objects: a provider and a sublayer with fixed keys and
/// non-dynamic, persistent filters. Unlike the app's old dynamic session they survive the
/// process that added them (and a reboot), so a crash fails closed.
/// </summary>
public sealed class WfpFirewall : IKsFirewall
{
    public static readonly Guid ProviderKey = new("b75434ab-9ee6-4b62-8752-d88d851a933c");
    public static readonly Guid SublayerKey = new("111315ae-961e-4eff-b6ce-cdc7b43d7a83");

    private static readonly Guid LayerConnectV4 = new("c38d57d1-05a7-4c33-904f-7fbceee60e82");
    private static readonly Guid LayerConnectV6 = new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4");
    private static readonly Guid LayerRecvAcceptV4 = new("e1cd9fe7-f4b5-4273-96c0-592e487b8650");
    private static readonly Guid LayerRecvAcceptV6 = new("a3b42c97-9f04-4672-b87e-cee9c483257f");
    private static readonly Guid ConditionAppId = new("d78e1e87-8644-4ea5-9437-d809ecefc971");
    private static readonly Guid ConditionFlags = new("632ce23b-5167-435c-86d7-e903684aa80c");
    private static readonly Guid ConditionRemoteAddress = new("b235ae9a-1d64-49b8-a44c-5ff3d9095045");
    private static readonly Guid ConditionLocalAddress = new("d9ee00de-c1ef-4617-bfe3-ffd8f5a08957");
    private static readonly Guid ConditionRemotePort = new("c35a604d-d22b-4e1a-91b4-68f674ee674b");
    private static readonly Guid ConditionLocalPort = new("0c1ba1af-5765-453f-af22-a8f791ac775b");
    private static readonly Guid ConditionProtocol = new("3971ef2b-623e-4f9a-8cb1-6e79b806b9a7");

    private const uint RpcCAuthnWinNt = 10;
    private const uint FlagPersistent = 0x1;
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

    private const uint ErrFilterNotFound = 0x80320003;
    private const uint ErrProviderNotFound = 0x80320005;
    private const uint ErrSublayerNotFound = 0x80320007;
    private const uint ErrAlreadyExists = 0x80320009;

    private readonly object _gate = new();

    public int Apply(IReadOnlyList<KsFilter> filters)
    {
        lock (_gate)
        {
            using var engine = Engine.Open();
            using var memory = new NativeMemory();
            Check(FwpmTransactionBegin0(engine.Handle, 0), "FwpmTransactionBegin0");
            var committed = false;
            try
            {
                EnsureProviderAndSublayer(engine.Handle, memory);
                DeleteFilters(engine.Handle);
                var added = 0;
                foreach (var filter in filters)
                {
                    if (AddFilter(engine.Handle, memory, filter))
                    {
                        added++;
                    }
                }
                Check(FwpmTransactionCommit0(engine.Handle), "FwpmTransactionCommit0");
                committed = true;
                return added;
            }
            finally
            {
                if (!committed)
                {
                    FwpmTransactionAbort0(engine.Handle);
                }
            }
        }
    }

    public void RemoveFilters()
    {
        lock (_gate)
        {
            using var engine = Engine.Open();
            InTransaction(engine.Handle, () => DeleteFilters(engine.Handle));
        }
    }

    public void RemoveAll()
    {
        lock (_gate)
        {
            using var engine = Engine.Open();
            InTransaction(engine.Handle, () =>
            {
                DeleteFilters(engine.Handle);
                var sublayer = SublayerKey;
                CheckIgnoring(FwpmSubLayerDeleteByKey0(engine.Handle, ref sublayer), "FwpmSubLayerDeleteByKey0", ErrSublayerNotFound);
                var provider = ProviderKey;
                CheckIgnoring(FwpmProviderDeleteByKey0(engine.Handle, ref provider), "FwpmProviderDeleteByKey0", ErrProviderNotFound);
            });
        }
    }

    public int CountFilters()
    {
        lock (_gate)
        {
            using var engine = Engine.Open();
            return EnumerateOurFilters(engine.Handle).Count;
        }
    }

    private static void InTransaction(IntPtr engine, Action action)
    {
        Check(FwpmTransactionBegin0(engine, 0), "FwpmTransactionBegin0");
        var committed = false;
        try
        {
            action();
            Check(FwpmTransactionCommit0(engine), "FwpmTransactionCommit0");
            committed = true;
        }
        finally
        {
            if (!committed)
            {
                FwpmTransactionAbort0(engine);
            }
        }
    }

    private static void EnsureProviderAndSublayer(IntPtr engine, NativeMemory memory)
    {
        var provider = new FwpmProvider0
        {
            providerKey = ProviderKey,
            displayData = memory.Display("Colitu VPN", "Colitu VPN kill switch"),
            flags = FlagPersistent,
            serviceName = memory.String(KsProtocol.ServiceName)
        };
        CheckIgnoring(FwpmProviderAdd0(engine, ref provider, IntPtr.Zero), "FwpmProviderAdd0", ErrAlreadyExists);

        var sublayer = new FwpmSublayer0
        {
            subLayerKey = SublayerKey,
            displayData = memory.Display("Colitu VPN kill switch", "Blocks traffic outside the VPN while Colitu protects this computer"),
            flags = FlagPersistent,
            providerKey = memory.Guid(ProviderKey),
            weight = 0xFFFF
        };
        CheckIgnoring(FwpmSubLayerAdd0(engine, ref sublayer, IntPtr.Zero), "FwpmSubLayerAdd0", ErrAlreadyExists);
    }

    private static void DeleteFilters(IntPtr engine)
    {
        foreach (var key in EnumerateOurFilters(engine))
        {
            var filterKey = key;
            CheckIgnoring(FwpmFilterDeleteByKey0(engine, ref filterKey), "FwpmFilterDeleteByKey0", ErrFilterNotFound);
        }
    }

    /// <summary>Keys of every filter in the Colitu sublayer (whatever version of the service added it).</summary>
    private static List<Guid> EnumerateOurFilters(IntPtr engine)
    {
        var keys = new List<Guid>();
        Check(FwpmFilterCreateEnumHandle0(engine, IntPtr.Zero, out var handle), "FwpmFilterCreateEnumHandle0");
        try
        {
            while (true)
            {
                Check(FwpmFilterEnum0(engine, handle, 512, out var entries, out var count), "FwpmFilterEnum0");
                try
                {
                    for (var i = 0; i < count; i++)
                    {
                        var pointer = Marshal.ReadIntPtr(entries, i * IntPtr.Size);
                        var filter = Marshal.PtrToStructure<FwpmFilter0>(pointer);
                        if (filter.subLayerKey == SublayerKey)
                        {
                            keys.Add(filter.filterKey);
                        }
                    }
                }
                finally
                {
                    if (entries != IntPtr.Zero)
                    {
                        FwpmFreeMemory0(ref entries);
                    }
                }
                if (count < 512)
                {
                    break;
                }
            }
        }
        finally
        {
            FwpmFilterDestroyEnumHandle0(engine, handle);
        }
        return keys;
    }

    /// <returns>False when the filter was skipped because none of its programs exist on disk.</returns>
    private static bool AddFilter(IntPtr engine, NativeMemory memory, KsFilter spec)
    {
        var conditions = new List<FwpmFilterCondition0>();
        var apps = spec.Conditions.OfType<KsApp>().ToList();
        var existingApps = apps.Where(app => File.Exists(app.Path)).ToList();
        if (apps.Count > 0 && existingApps.Count == 0)
        {
            // A program filter without its program would match every program.
            return false;
        }
        foreach (var condition in spec.Conditions)
        {
            switch (condition)
            {
                case KsLoopback:
                    conditions.Add(Condition(ConditionFlags, MatchFlagsAllSet, TypeUInt32, (IntPtr)FlagIsLoopback));
                    break;
                case KsApp app when existingApps.Contains(app):
                    conditions.Add(Condition(ConditionAppId, MatchEqual, TypeByteBlob, memory.AppId(app.Path)));
                    break;
                case KsApp:
                    break;
                case KsLocalNetwork local:
                    conditions.Add(Condition(ConditionLocalAddress, MatchEqual, local.Network.IsV6 ? TypeV6AddrMask : TypeV4AddrMask, memory.AddressMask(local.Network)));
                    break;
                case KsRemoteNetwork remote:
                    conditions.Add(Condition(ConditionRemoteAddress, MatchEqual, remote.Network.IsV6 ? TypeV6AddrMask : TypeV4AddrMask, memory.AddressMask(remote.Network)));
                    break;
                case KsRemotePort port:
                    conditions.Add(Condition(ConditionRemotePort, MatchEqual, TypeUInt16, (IntPtr)port.Port));
                    break;
                case KsLocalPort port:
                    conditions.Add(Condition(ConditionLocalPort, MatchEqual, TypeUInt16, (IntPtr)port.Port));
                    break;
                case KsProtocolCondition protocol:
                    conditions.Add(Condition(ConditionProtocol, MatchEqual, TypeUInt8, (IntPtr)protocol.Protocol));
                    break;
                default:
                    throw new InvalidOperationException($"Unknown condition {condition.GetType().Name}");
            }
        }

        var filter = new FwpmFilter0
        {
            filterKey = Guid.NewGuid(),
            displayData = memory.Display(spec.Name, "Colitu VPN kill switch"),
            flags = FlagPersistent,
            providerKey = memory.Guid(ProviderKey),
            layerKey = spec.Layer switch
            {
                KsLayer.ConnectV4 => LayerConnectV4,
                KsLayer.ConnectV6 => LayerConnectV6,
                KsLayer.RecvAcceptV4 => LayerRecvAcceptV4,
                _ => LayerRecvAcceptV6
            },
            subLayerKey = SublayerKey,
            weight = new FwpValue0 { type = TypeUInt8, value = (IntPtr)spec.Weight },
            numFilterConditions = (uint)conditions.Count,
            filterCondition = conditions.Count == 0 ? IntPtr.Zero : memory.Array(conditions),
            action = new FwpmAction0 { type = spec.Permit ? ActionPermit : ActionBlock }
        };
        Check(FwpmFilterAdd0(engine, ref filter, IntPtr.Zero, out _), $"FwpmFilterAdd0 ({spec.Name})");
        return true;
    }

    private static FwpmFilterCondition0 Condition(Guid field, uint match, uint type, IntPtr value) => new()
    {
        fieldKey = field,
        matchType = match,
        conditionValue = new FwpValue0 { type = type, value = value }
    };

    private static void Check(uint result, string call)
    {
        if (result != 0)
        {
            throw new KsFirewallException($"{call} failed: 0x{result:X8}");
        }
    }

    private static void CheckIgnoring(uint result, string call, uint ignored)
    {
        if (result != 0 && result != ignored)
        {
            throw new KsFirewallException($"{call} failed: 0x{result:X8}");
        }
    }

    /// <summary>FWP_V4_ADDR_AND_MASK takes host-order integers; FWP_V6_ADDR_AND_MASK the 16 bytes and the prefix.</summary>
    internal static byte[] EncodeAddressMask(KsNetwork network)
    {
        var bytes = network.Address.GetAddressBytes();
        if (bytes.Length == 4)
        {
            var addr = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
            var mask = network.Prefix == 0 ? 0u : uint.MaxValue << (32 - network.Prefix);
            var result = new byte[8];
            BitConverter.GetBytes(addr & mask).CopyTo(result, 0);
            BitConverter.GetBytes(mask).CopyTo(result, 4);
            return result;
        }
        var v6 = new byte[17];
        bytes.CopyTo(v6, 0);
        v6[16] = (byte)network.Prefix;
        return v6;
    }

    private sealed class Engine : IDisposable
    {
        public IntPtr Handle { get; private init; }

        public static Engine Open()
        {
            // No session: not dynamic, so the objects outlive this handle.
            Check(FwpmEngineOpen0(null, RpcCAuthnWinNt, IntPtr.Zero, IntPtr.Zero, out var handle), "FwpmEngineOpen0");
            return new Engine { Handle = handle };
        }

        public void Dispose() => FwpmEngineClose0(Handle);
    }

    /// <summary>Unmanaged allocations that must outlive the WFP calls of one transaction.</summary>
    private sealed class NativeMemory : IDisposable
    {
        private readonly List<IntPtr> _hGlobal = [];
        private readonly List<IntPtr> _wfp = [];

        public FwpmDisplayData0 Display(string name, string description) => new()
        {
            name = Keep(Marshal.StringToHGlobalUni(name)),
            description = Keep(Marshal.StringToHGlobalUni(description))
        };

        public IntPtr String(string value) => Keep(Marshal.StringToHGlobalUni(value));

        public IntPtr Guid(Guid value)
        {
            var pointer = Keep(Marshal.AllocHGlobal(16));
            Marshal.Copy(value.ToByteArray(), 0, pointer, 16);
            return pointer;
        }

        public IntPtr AddressMask(KsNetwork network)
        {
            var bytes = EncodeAddressMask(network);
            var pointer = Keep(Marshal.AllocHGlobal(bytes.Length));
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            return pointer;
        }

        public IntPtr AppId(string path)
        {
            Check(FwpmGetAppIdFromFileName0(path, out var blob), "FwpmGetAppIdFromFileName0");
            _wfp.Add(blob);
            return blob;
        }

        public IntPtr Array(List<FwpmFilterCondition0> conditions)
        {
            var size = Marshal.SizeOf<FwpmFilterCondition0>();
            var pointer = Keep(Marshal.AllocHGlobal(size * conditions.Count));
            for (var i = 0; i < conditions.Count; i++)
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
    internal struct FwpmSublayer0
    {
        public Guid subLayerKey;
        public FwpmDisplayData0 displayData;
        public uint flags;
        public IntPtr providerKey;
        public FwpByteBlob providerData;
        public ushort weight;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FwpmProvider0
    {
        public Guid providerKey;
        public FwpmDisplayData0 displayData;
        public uint flags;
        public FwpByteBlob providerData;
        public IntPtr serviceName;
    }

    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)]
    private static extern uint FwpmEngineOpen0(string? serverName, uint authnService, IntPtr authIdentity, IntPtr session, out IntPtr engineHandle);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmEngineClose0(IntPtr engineHandle);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmTransactionBegin0(IntPtr engineHandle, uint flags);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmTransactionCommit0(IntPtr engineHandle);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmTransactionAbort0(IntPtr engineHandle);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmProviderAdd0(IntPtr engineHandle, ref FwpmProvider0 provider, IntPtr securityDescriptor);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmProviderDeleteByKey0(IntPtr engineHandle, ref Guid key);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmSubLayerAdd0(IntPtr engineHandle, ref FwpmSublayer0 subLayer, IntPtr securityDescriptor);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmSubLayerDeleteByKey0(IntPtr engineHandle, ref Guid key);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmFilterAdd0(IntPtr engineHandle, ref FwpmFilter0 filter, IntPtr securityDescriptor, out ulong id);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmFilterDeleteByKey0(IntPtr engineHandle, ref Guid key);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmFilterCreateEnumHandle0(IntPtr engineHandle, IntPtr enumTemplate, out IntPtr enumHandle);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmFilterEnum0(IntPtr engineHandle, IntPtr enumHandle, uint numEntriesRequested, out IntPtr entries, out uint numEntriesReturned);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmFilterDestroyEnumHandle0(IntPtr engineHandle, IntPtr enumHandle);

    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)]
    private static extern uint FwpmGetAppIdFromFileName0(string fileName, out IntPtr appId);

    [DllImport("fwpuclnt.dll")]
    private static extern void FwpmFreeMemory0(ref IntPtr p);
}

public sealed class KsFirewallException(string message) : Exception(message);
