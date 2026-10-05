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
    private static HttpClient Doh = CreateDohClient();
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

    private static HttpClient CreateDohClient() => new(new SocketsHttpHandler
    {
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(4),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30)
    })
    { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>Drops keep-alive DoH connections opened over a route that changed with the tunnel.</summary>
    public static void ResetConnections()
    {
        var old = Interlocked.Exchange(ref Doh, CreateDohClient());
        _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ => old.Dispose(), TaskScheduler.Default);
    }

    /// <summary>
    /// Asks both resolvers at once and takes the first real answer: one of them is often slow or
    /// blocked (Russia), and asking them in turn cost up to 5 s before the second was even tried.
    /// </summary>
    private static async Task<IPAddress?> ResolveViaDohAsync(string host, CancellationToken token)
    {
        using var race = CancellationTokenSource.CreateLinkedTokenSource(token);
        var client = Doh;
        var pending = new[] { "https://cloudflare-dns.com/dns-query", "https://dns.google/resolve" }
            .Select(endpoint => QueryDohAsync(client, endpoint, host, race.Token))
            .ToList();
        while (pending.Count > 0)
        {
            var finished = await Task.WhenAny(pending);
            pending.Remove(finished);
            if (await finished is { } address)
            {
                race.Cancel();
                return address;
            }
        }
        return null;
    }

    private static async Task<IPAddress?> QueryDohAsync(HttpClient client, string endpoint, string host, CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{endpoint}?name={Uri.EscapeDataString(host)}&type=A");
            request.Headers.TryAddWithoutValidation("accept", "application/dns-json");
            using var response = await client.SendAsync(request, token);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            if (!document.RootElement.TryGetProperty("Answer", out var answers))
            {
                return null;
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
        catch (OperationCanceledException)
        {
            // The other resolver answered first, or the connection attempt was cancelled.
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
    public static string? PhysicalInterfaceName() => PhysicalInterface()?.Name;

    /// <summary>IPv4 interface index of <see cref="PhysicalInterfaceName"/>, for IP_UNICAST_IF.</summary>
    public static int? PhysicalInterfaceIndex()
    {
        try
        {
            return PhysicalInterface()?.GetIPProperties().GetIPv4Properties().Index;
        }
        catch
        {
            return null;
        }
    }

    private static NetworkInterface? PhysicalInterface()
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
            return best.adapter;
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituNetwork.PhysicalInterface", ex);
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

    /// <summary>
    /// Whether any adapter other than Colitu's own TUN is connected with an IPv4 gateway. Broader
    /// than <see cref="PhysicalInterfaceName"/> on purpose: PPPoE and mobile broadband count too.
    /// False means the computer is offline and no reconnect can help.
    /// </summary>
    public static bool HasPhysicalNetwork()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
                .Where(adapter => adapter.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .Where(adapter => !IsColituTun(adapter))
                .Any(adapter => adapter.GetIPProperties().GatewayAddresses
                    .Any(gateway => gateway.Address.AddressFamily == AddressFamily.InterNetwork && !gateway.Address.Equals(IPAddress.Any)));
        }
        catch
        {
            // Unknown: let the recovery go ahead rather than wait for ever.
            return true;
        }
    }

    private static bool IsColituTun(NetworkInterface adapter) =>
        adapter.Name.Equals("xray_tun", StringComparison.OrdinalIgnoreCase)
        || adapter.Name.Equals("singbox_tun", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Sends one DNS query into Colitu's TUN adapter, where the core answers every DNS query, for
    /// a random name no cache can hold. True when an answer comes back (a name error counts: the
    /// resolver works), false when it stays silent or fails, null when there is no TUN adapter.
    /// </summary>
    public static async Task<bool?> TunnelDnsAnswersAsync(TimeSpan timeout)
    {
        if (TunAddresses() is not { } tun)
        {
            return null;
        }
        try
        {
            using var udp = new UdpClient(new IPEndPoint(tun.Local, 0));
            var id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
            var query = BuildDnsQuery(id, $"c{Random.Shared.Next():x8}.colitu.com");
            await udp.SendAsync(query, new IPEndPoint(tun.Peer, 53));
            using var cts = new CancellationTokenSource(timeout);
            while (true)
            {
                var reply = await udp.ReceiveAsync(cts.Token);
                if (DnsAnswerState(reply.Buffer, id) is { } answered)
                {
                    return answered;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>The TUN adapter's own address and the other host of its /30 (where the core listens).</summary>
    private static (IPAddress Local, IPAddress Peer)? TunAddresses()
    {
        try
        {
            var unicast = NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up && IsColituTun(adapter))
                .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
                .FirstOrDefault(address => address.Address.AddressFamily == AddressFamily.InterNetwork);
            return unicast == null ? null : TunPeer(unicast.Address, unicast.PrefixLength);
        }
        catch
        {
            return null;
        }
    }

    internal static (IPAddress Local, IPAddress Peer)? TunPeer(IPAddress local, int prefixLength)
    {
        if (local.AddressFamily != AddressFamily.InterNetwork || prefixLength is < 8 or > 30)
        {
            return null;
        }
        var bytes = local.GetAddressBytes();
        var value = (uint)bytes[0] << 24 | (uint)bytes[1] << 16 | (uint)bytes[2] << 8 | bytes[3];
        var mask = uint.MaxValue << (32 - prefixLength);
        var network = value & mask;
        var peer = value == network + 1 ? network + 2 : network + 1;
        return (local, new IPAddress(new[] { (byte)(peer >> 24), (byte)((peer >> 16) & 0xFF), (byte)((peer >> 8) & 0xFF), (byte)(peer & 0xFF) }));
    }

    /// <summary>A recursive A query for <paramref name="name"/>.</summary>
    internal static byte[] BuildDnsQuery(ushort id, string name)
    {
        var packet = new List<byte> { (byte)(id >> 8), (byte)(id & 0xFF), 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(label);
            packet.Add((byte)bytes.Length);
            packet.AddRange(bytes);
        }
        packet.AddRange(new byte[] { 0x00, 0x00, 0x01, 0x00, 0x01 });
        return packet.ToArray();
    }

    /// <summary>
    /// For a reply to query <paramref name="id"/>: true when the resolver answered (no error or
    /// "no such name"), false when it reported a failure (SERVFAIL, refused). Null for anything else.
    /// </summary>
    internal static bool? DnsAnswerState(byte[] reply, ushort id)
    {
        if (reply.Length < 12 || reply[0] != (byte)(id >> 8) || reply[1] != (byte)(id & 0xFF) || (reply[2] & 0x80) == 0)
        {
            return null;
        }
        var rcode = reply[3] & 0x0F;
        return rcode is 0 or 3;
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
            // The table can grow between the two calls (the TUN adapter adding its routes):
            // retry with the size Windows asks for instead of reporting "no default route".
            int status;
            for (var attempt = 0; (status = GetIpForwardTable(buffer, ref size, false)) == ErrorInsufficientBuffer && attempt < 3; attempt++)
            {
                Marshal.FreeHGlobal(buffer);
                buffer = Marshal.AllocHGlobal(size);
            }
            if (status != 0)
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

    private const int ErrorInsufficientBuffer = 122;

    [DllImport("iphlpapi.dll")]
    private static extern int GetIpForwardTable(IntPtr table, ref int size, bool order);
}
