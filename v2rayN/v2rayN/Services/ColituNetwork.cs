using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace v2rayN.Services;

/// <summary>
/// Network facts the VPN needs before it starts the core: the server's real
/// address, the physical adapter to send through, and whether another VPN is
/// already holding the default route.
/// </summary>
public static class ColituNetwork
{
    private static readonly HttpClient Doh = new(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(4) }) { Timeout = TimeSpan.FromSeconds(5) };
    private static readonly Dictionary<string, (IPAddress Address, DateTimeOffset Until)> ResolveCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object CacheGate = new();

    /// <summary>
    /// Resolves a server host name to a real address, asking DNS-over-HTTPS
    /// first. Other VPN clients running in "fake IP" mode (Clash, sing-box)
    /// answer every system DNS query with a private placeholder address; the
    /// core would then dial that placeholder on the physical adapter and hang.
    /// Returns null when only such placeholders are available.
    /// </summary>
    public static async Task<IPAddress?> ResolveServerAsync(string host, CancellationToken token = default)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            return literal;
        }

        lock (CacheGate)
        {
            if (ResolveCache.TryGetValue(host, out var cached) && cached.Until > DateTimeOffset.UtcNow)
            {
                return cached.Address;
            }
        }

        var resolved = await ResolveViaDohAsync(host, token) ?? await ResolveViaSystemAsync(host, token);
        if (resolved != null)
        {
            lock (CacheGate)
            {
                ResolveCache[host] = (resolved, DateTimeOffset.UtcNow.AddMinutes(10));
            }
        }
        return resolved;
    }

    private static async Task<IPAddress?> ResolveViaDohAsync(string host, CancellationToken token)
    {
        foreach (var endpoint in new[] { "https://cloudflare-dns.com/dns-query", "https://dns.google/resolve" })
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{endpoint}?name={Uri.EscapeDataString(host)}&type=A");
                request.Headers.TryAddWithoutValidation("accept", "application/dns-json");
                using var response = await Doh.SendAsync(request, token);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                if (!document.RootElement.TryGetProperty("Answer", out var answers))
                {
                    continue;
                }
                foreach (var answer in answers.EnumerateArray())
                {
                    if (answer.TryGetProperty("type", out var type) && type.GetInt32() == 1
                        && answer.TryGetProperty("data", out var data)
                        && IPAddress.TryParse(data.GetString(), out var address)
                        && !IsPlaceholder(address))
                    {
                        return address;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                Logging.SaveLog($"ColituNetwork.ResolveViaDoh {endpoint}: {ex.Message}");
            }
        }
        return null;
    }

    private static async Task<IPAddress?> ResolveViaSystemAsync(string host, CancellationToken token)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, token);
            return addresses.FirstOrDefault(address => !IsPlaceholder(address));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
        {
            Logging.SaveLog($"ColituNetwork.ResolveViaSystem {host}: {ex.Message}");
            return null;
        }
    }

    /// <summary>198.18.0.0/15 is the pool fake-IP DNS resolvers hand out; it is never a real server.</summary>
    internal static bool IsPlaceholder(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }
        var bytes = address.GetAddressBytes();
        return bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19);
    }

    /// <summary>
    /// The physical adapter (Ethernet or Wi-Fi) that carries the internet
    /// connection: up, with a gateway, lowest route metric. Virtual adapters of
    /// other VPN clients are skipped so the core never sends its traffic into
    /// another tunnel, which would loop or die when that tunnel goes down.
    /// </summary>
    public static string? PhysicalInterfaceName()
    {
        try
        {
            var metrics = DefaultRouteMetrics();
            var best = NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
                .Where(adapter => adapter.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet)
                .Where(adapter => !IsVirtual(adapter))
                .Select(adapter => (adapter, properties: adapter.GetIPProperties()))
                .Where(item => item.properties.GatewayAddresses.Any(gateway => gateway.Address.AddressFamily == AddressFamily.InterNetwork && !gateway.Address.Equals(IPAddress.Any)))
                .Select(item => (item.adapter, metric: metrics.TryGetValue(item.properties.GetIPv4Properties().Index, out var metric) ? metric : uint.MaxValue))
                .OrderBy(item => item.metric)
                .ThenBy(item => item.adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 1 : 0)
                .FirstOrDefault();
            return best.adapter?.Name;
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituNetwork.PhysicalInterfaceName", ex);
            return null;
        }
    }

    /// <summary>True once any adapter has an IPv4 default route (the network is usable).</summary>
    public static bool HasDefaultRoute()
    {
        try
        {
            return DefaultRouteMetrics().Count > 0;
        }
        catch
        {
            return NetworkInterface.GetIsNetworkAvailable();
        }
    }

    /// <summary>Name of another VPN client's adapter that currently owns a default route, if any.</summary>
    public static string? CompetingVpnAdapter()
    {
        try
        {
            var metrics = DefaultRouteMetrics();
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up && IsVirtual(adapter))
                // Colitu's own TUN adapters (Xray and sing-box) are not "another VPN".
                .Where(adapter => !adapter.Name.Contains("xray_tun", StringComparison.OrdinalIgnoreCase)
                    && !adapter.Name.Contains("singbox_tun", StringComparison.OrdinalIgnoreCase))
                .Where(adapter =>
                {
                    try
                    {
                        return metrics.ContainsKey(adapter.GetIPProperties().GetIPv4Properties().Index);
                    }
                    catch
                    {
                        return false;
                    }
                })
                .Select(adapter => adapter.Name)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static bool IsVirtual(NetworkInterface adapter)
    {
        var text = adapter.Description + " " + adapter.Name;
        return adapter.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Loopback or NetworkInterfaceType.Ppp
            || text.Contains("Wintun", StringComparison.OrdinalIgnoreCase)
            || text.Contains("TAP-Windows", StringComparison.OrdinalIgnoreCase)
            || text.Contains("TAP Adapter", StringComparison.OrdinalIgnoreCase)
            || text.Contains("WireGuard", StringComparison.OrdinalIgnoreCase)
            || text.Contains("OpenVPN", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Tunnel", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
            || text.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase)
            || text.Contains("VMware", StringComparison.OrdinalIgnoreCase)
            || text.Contains("vEthernet", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Interface index → metric of its IPv4 default route (GetIpForwardTable, effective metric).</summary>
    private static Dictionary<int, uint> DefaultRouteMetrics()
    {
        var result = new Dictionary<int, uint>();
        var size = 0;
        GetIpForwardTable(IntPtr.Zero, ref size, false);
        if (size <= 0)
        {
            return result;
        }
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetIpForwardTable(buffer, ref size, false) != 0)
            {
                return result;
            }
            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<MibIpForwardRow>();
            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MibIpForwardRow>(buffer + 4 + i * rowSize);
                if (row.dwForwardDest != 0 || row.dwForwardMask != 0)
                {
                    continue;
                }
                var index = (int)row.dwForwardIfIndex;
                if (!result.TryGetValue(index, out var existing) || row.dwForwardMetric1 < existing)
                {
                    result[index] = row.dwForwardMetric1;
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibIpForwardRow
    {
        public uint dwForwardDest;
        public uint dwForwardMask;
        public uint dwForwardPolicy;
        public uint dwForwardNextHop;
        public uint dwForwardIfIndex;
        public uint dwForwardType;
        public uint dwForwardProto;
        public uint dwForwardAge;
        public uint dwForwardNextHopAS;
        public uint dwForwardMetric1;
        public uint dwForwardMetric2;
        public uint dwForwardMetric3;
        public uint dwForwardMetric4;
        public uint dwForwardMetric5;
    }

    [DllImport("iphlpapi.dll")]
    private static extern int GetIpForwardTable(IntPtr table, ref int size, bool order);
}
