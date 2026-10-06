using System.Net;
using System.Net.Sockets;

namespace Colitu.KillSwitch;

public enum KsLayer
{
    ConnectV4,
    ConnectV6,
    RecvAcceptV4,
    RecvAcceptV6
}

/// <summary>One condition of a filter. Conditions on the same field are OR'ed by WFP, different fields AND'ed.</summary>
public abstract record KsCondition;

public sealed record KsLoopback : KsCondition;

/// <summary>The program, by full path (WFP turns it into an app id).</summary>
public sealed record KsApp(string Path) : KsCondition;

public sealed record KsLocalNetwork(KsNetwork Network) : KsCondition;

public sealed record KsRemoteNetwork(KsNetwork Network) : KsCondition;

public sealed record KsRemotePort(ushort Port) : KsCondition;

public sealed record KsProtocolCondition(byte Protocol) : KsCondition;

public sealed record KsFilter(KsLayer Layer, byte Weight, bool Permit, string Name, IReadOnlyList<KsCondition> Conditions)
{
    public bool IsV6 => Layer is KsLayer.ConnectV6 or KsLayer.RecvAcceptV6;
    public bool IsOutbound => Layer is KsLayer.ConnectV4 or KsLayer.ConnectV6;
}

/// <summary>Where the Colitu programs live: next to the service, in the admin-only install folder.</summary>
public sealed record KsInstallLayout(string AppPath, IReadOnlyList<string> CorePaths)
{
    public static KsInstallLayout FromDirectory(string directory) => new(
        System.IO.Path.Combine(directory, KsProtocol.AppExeName),
        [
            System.IO.Path.Combine(directory, "bin", "xray", "xray.exe"),
            System.IO.Path.Combine(directory, "bin", "sing_box", "sing-box.exe"),
        ]);
}

/// <summary>
/// Turns a validated allow list into WFP filters. Weights inside the Colitu sublayer decide
/// (the highest matching filter wins):
///
/// 15 permit loopback (the local proxy, the core's control ports)
/// 14 permit the Colitu app (panel API, its own DNS lookups and probes) — control plane
/// 14 permit the cores: everywhere (coreAccess=full) or only to the server endpoints
/// 14 permit traffic whose local address is the VPN adapter (everything inside the tunnel)
/// 14 bypass split tunnelling: permit the excluded apps
/// 12 block DNS (port 53) outside the tunnel, so no resolver on the LAN learns the names
/// 11 permit the local network (optional), DHCP, and bypassed networks
///  0 block everything else
///
/// "Only selected apps use the VPN" inverts it: everything may go out directly, except the
/// selected apps and networks, which may only use the tunnel (blocked at 13, below loopback and
/// the adapter permit; permit-all at 0).
/// </summary>
public static class KsPlanBuilder
{
    // The TUN adapters of both cores (ServiceLib Sample/SampleTunInbound, tun_singbox_inbound).
    public static readonly KsNetwork[] TunNetworks =
    [
        new(IPAddress.Parse("172.18.0.0"), 30),
        new(IPAddress.Parse("fdfe:dcba:9876::"), 126)
    ];

    // Local network, link-local, multicast and broadcast (printers, casting, the router page).
    public static readonly KsNetwork[] LocalNetworks =
    [
        new(IPAddress.Parse("10.0.0.0"), 8),
        new(IPAddress.Parse("172.16.0.0"), 12),
        new(IPAddress.Parse("192.168.0.0"), 16),
        new(IPAddress.Parse("169.254.0.0"), 16),
        new(IPAddress.Parse("224.0.0.0"), 4),
        new(IPAddress.Parse("255.255.255.255"), 32),
        new(IPAddress.Parse("fe80::"), 10),
        new(IPAddress.Parse("fc00::"), 7),
        new(IPAddress.Parse("ff00::"), 8)
    ];

    private static readonly KsLayer[] AllLayers = [KsLayer.ConnectV4, KsLayer.ConnectV6, KsLayer.RecvAcceptV4, KsLayer.RecvAcceptV6];
    private const byte Udp = 17;
    private const byte Tcp = 6;

    public static IReadOnlyList<KsFilter> Build(KsValidArm arm, KsInstallLayout layout)
    {
        return arm.SplitMode == KsSplitMode.Only ? BuildOnlySelected(arm) : BuildBlockOutside(arm, layout);
    }

    private static List<KsFilter> BuildBlockOutside(KsValidArm arm, KsInstallLayout layout)
    {
        var filters = new List<KsFilter>();
        foreach (var layer in AllLayers)
        {
            var v6 = layer is KsLayer.ConnectV6 or KsLayer.RecvAcceptV6;
            var outbound = layer is KsLayer.ConnectV4 or KsLayer.ConnectV6;

            filters.Add(new(layer, 15, true, "Permit loopback", [new KsLoopback()]));
            filters.Add(new(layer, 14, true, "Permit the Colitu app", [new KsApp(layout.AppPath)]));

            var cores = layout.CorePaths.Select(path => (KsCondition)new KsApp(path)).ToList();
            if (arm.CoreFullAccess)
            {
                filters.Add(new(layer, 14, true, "Permit the VPN cores", cores));
            }
            else if (outbound)
            {
                foreach (var endpoint in arm.Endpoints.Where(item => (item.Address.AddressFamily == AddressFamily.InterNetworkV6) == v6))
                {
                    var conditions = new List<KsCondition>(cores)
                    {
                        new KsRemoteNetwork(new KsNetwork(endpoint.Address, v6 ? 128 : 32)),
                        new KsRemotePort(endpoint.Port)
                    };
                    if (endpoint.Proto != KsProto.Any)
                    {
                        conditions.Add(new KsProtocolCondition(endpoint.Proto == KsProto.Tcp ? Tcp : Udp));
                    }
                    filters.Add(new(layer, 14, true, "Permit the VPN cores to the VPN server", conditions));
                }
            }

            foreach (var network in TunNetworks.Where(net => net.IsV6 == v6))
            {
                filters.Add(new(layer, 14, true, "Permit traffic through the VPN adapter", [new KsLocalNetwork(network)]));
            }

            if (arm.SplitMode == KsSplitMode.Bypass && arm.Apps.Count > 0)
            {
                filters.Add(new(layer, 14, true, "Permit apps excluded from the VPN", arm.Apps.Select(path => (KsCondition)new KsApp(path)).ToList()));
            }

            // DNS must go through the tunnel; a LAN resolver would reveal browsing.
            filters.Add(new(layer, 12, false, "Block DNS outside the VPN", [new KsRemotePort(53)]));

            if (arm.AllowLan)
            {
                filters.Add(new(layer, 11, true, "Permit local network",
                    LocalNetworks.Where(net => net.IsV6 == v6).Select(net => (KsCondition)new KsRemoteNetwork(net)).ToList()));
            }
            else
            {
                // Without the LAN, link-local and multicast still carry DHCP and neighbour discovery.
                filters.Add(new(layer, 11, true, "Permit link-local network",
                    LocalNetworks.Where(net => net.IsV6 == v6 && IsLinkLocalOrMulticast(net)).Select(net => (KsCondition)new KsRemoteNetwork(net)).ToList()));
            }

            filters.Add(new(layer, 11, true, "Permit DHCP", [new KsProtocolCondition(Udp), new KsRemotePort((ushort)(v6 ? 547 : 67))]));

            if (arm.SplitMode == KsSplitMode.Bypass)
            {
                var networks = arm.Networks.Where(net => net.IsV6 == v6).Select(net => (KsCondition)new KsRemoteNetwork(net)).ToList();
                if (networks.Count > 0)
                {
                    filters.Add(new(layer, 11, true, "Permit networks excluded from the VPN", networks));
                }
            }

            filters.Add(new(layer, 0, false, "Block everything else", []));
        }
        return filters;
    }

    private static List<KsFilter> BuildOnlySelected(KsValidArm arm)
    {
        var filters = new List<KsFilter>();
        foreach (var layer in AllLayers)
        {
            var v6 = layer is KsLayer.ConnectV6 or KsLayer.RecvAcceptV6;
            filters.Add(new(layer, 15, true, "Permit loopback", [new KsLoopback()]));
            foreach (var network in TunNetworks.Where(net => net.IsV6 == v6))
            {
                filters.Add(new(layer, 14, true, "Permit traffic through the VPN adapter", [new KsLocalNetwork(network)]));
            }
            if (arm.Apps.Count > 0)
            {
                filters.Add(new(layer, 13, false, "Block VPN-only apps outside the VPN", arm.Apps.Select(path => (KsCondition)new KsApp(path)).ToList()));
            }
            var networks = arm.Networks.Where(net => net.IsV6 == v6).Select(net => (KsCondition)new KsRemoteNetwork(net)).ToList();
            if (networks.Count > 0)
            {
                filters.Add(new(layer, 13, false, "Block VPN-only networks outside the VPN", networks));
            }
            filters.Add(new(layer, 0, true, "Permit everything else (only selected apps use the VPN)", []));
        }
        return filters;
    }

    private static bool IsLinkLocalOrMulticast(KsNetwork network)
    {
        var text = network.ToString();
        return text is "169.254.0.0/16" or "224.0.0.0/4" or "255.255.255.255/32" or "fe80::/10" or "ff00::/8";
    }
}
