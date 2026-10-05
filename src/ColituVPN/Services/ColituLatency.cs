using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace v2rayN.Services;

/// <summary>
/// Ping of a location: the TCP connect time from this PC to the node's latency
/// probe address (the panel's latency_host / latency_port), best of two tries,
/// like the phone apps. The socket is pinned to the physical adapter with
/// IP_UNICAST_IF, the same way the core keeps its own traffic out of the TUN
/// adapter, so the number describes this PC's own path to the server even while
/// the VPN is connected. In system-proxy mode sockets bypass the proxy anyway.
/// </summary>
public static class ColituLatency
{
    private const int TimeoutMs = 2500;
    private const int IpUnicastIf = 31;

    /// <summary>Pings of every server with a probe address, by server id.</summary>
    public static async Task<Dictionary<string, int>> MeasureAllAsync(IEnumerable<ColituVpnServer> servers)
    {
        var index = ColituNetwork.PhysicalInterfaceIndex();
        var probes = servers
            .Where(server => server.Id.IsNotEmpty() && server.Host.IsNotEmpty() && server.Port is > 0 and < 65536)
            .GroupBy(server => server.Id!, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(async server => (server.Id!, await MeasureAsync(server.Host!, server.Port!.Value, index)));
        var results = await Task.WhenAll(probes);
        return results
            .Where(result => result.Item2 != null)
            .ToDictionary(result => result.Item1, result => result.Item2!.Value, StringComparer.OrdinalIgnoreCase);
    }

    public static async Task<int?> MeasureAsync(string host, int port, int? interfaceIndex)
    {
        IPAddress? address;
        try
        {
            // Resolved first (DNS over HTTPS, cached) so the lookup is not counted as ping
            // and a fake-IP answer from another VPN client is never dialled.
            using var resolveTimeout = new CancellationTokenSource(TimeoutMs);
            address = await ColituNetwork.ResolveServerAsync(host, resolveTimeout.Token);
        }
        catch
        {
            return null;
        }
        if (address == null || address.AddressFamily != AddressFamily.InterNetwork || !IsPublic(address))
        {
            return null;
        }

        int? best = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var ms = await ConnectMsAsync(address, port, interfaceIndex);
            if (ms != null && (best == null || ms < best))
            {
                best = ms;
            }
        }
        return best;
    }

    /// <summary>
    /// VPN nodes are on the internet: a probe address in loopback, private, CGNAT or
    /// link-local space (a bad or tampered server list) is never dialled, so the ping
    /// cannot be turned into a scan of this computer or its local network.
    /// </summary>
    internal static bool IsPublic(IPAddress address)
    {
        var b = address.GetAddressBytes();
        if (b.Length != 4) return false;
        return !(b[0] is 0 or 10 or 127
            || b[0] >= 224
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168));
    }

    private static async Task<int?> ConnectMsAsync(IPAddress address, int port, int? interfaceIndex)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            if (interfaceIndex is > 0)
            {
                try
                {
                    // IPv4 IP_UNICAST_IF takes the index in network byte order.
                    socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)IpUnicastIf,
                        BitConverter.GetBytes(IPAddress.HostToNetworkOrder(interfaceIndex.Value)));
                }
                catch (SocketException)
                {
                    // Not pinned: the measurement still works, possibly through the tunnel.
                }
            }
            using var timeout = new CancellationTokenSource(TimeoutMs);
            var watch = Stopwatch.StartNew();
            await socket.ConnectAsync(new IPEndPoint(address, port), timeout.Token);
            watch.Stop();
            return Math.Max(1, (int)watch.ElapsedMilliseconds);
        }
        catch
        {
            return null;
        }
    }
}
