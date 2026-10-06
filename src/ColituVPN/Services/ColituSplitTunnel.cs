using System.Net.Sockets;
using Colitu.KillSwitch;

namespace v2rayN.Services;

public static class ColituSplitTunnelModes
{
    public const string Off = "off";
    /// <summary>The selected apps and sites bypass the VPN; everything else uses it.</summary>
    public const string Bypass = "bypass";
    /// <summary>Only the selected apps and sites use the VPN; everything else goes direct.</summary>
    public const string Only = "only";

    public static string Normalize(string? mode) => (mode ?? "").Trim().ToLowerInvariant() switch
    {
        Bypass => Bypass,
        Only => Only,
        _ => Off
    };
}

/// <summary>
/// Split tunnelling: validation of the user's lists and the routing rules they turn into.
///
/// Engine (the rules go into the Colitu routing profile, which both cores read):
/// - TUN mode: sing-box 1.14 runs the TUN adapter. Apps match by <c>process_path</c> (full path)
///   and <c>process_name</c>, sites by <c>domain_suffix</c>, addresses by <c>ip_cidr</c>. While
///   apps are listed, every transport runs on sing-box (Xray cannot see which program sent a TUN
///   packet on Windows); vless-xhttp, which sing-box does not speak, is skipped then.
/// - Proxy mode: no app rules (a system proxy cannot tell programs apart); sites and addresses
///   are routed by the core (Xray or sing-box), so their DNS also resolves directly.
/// - "Only selected": the selected entries go to the proxy and a final catch-all rule sends the
///   rest direct (sing-box then also uses the direct DNS server as final resolver).
/// </summary>
public static class ColituSplitTunnel
{
    public const int MaxApps = KsProtocol.MaxApps;
    public const int MaxDomains = 256;
    public const int MaxNetworks = KsProtocol.MaxNetworks;

    public const string RuleAppsId = "colitu-split-apps";
    public const string RuleDomainsId = "colitu-split-domains";
    public const string RuleNetworksId = "colitu-split-networks";
    public const string RuleRestId = "colitu-split-rest-direct";

    /// <summary>Lowercase ASCII (punycode) domain without a leading "*." or "."; null when invalid.</summary>
    public static string? NormalizeDomain(string? value)
    {
        var text = (value ?? "").Trim().TrimEnd('.').ToLowerInvariant();
        if (text.StartsWith("*.", StringComparison.Ordinal))
        {
            text = text[2..];
        }
        else if (text.StartsWith('.'))
        {
            text = text[1..];
        }
        if (text.Length is 0 or > 253 || text.Contains("://", StringComparison.Ordinal) || text.Contains('/') || text.Contains(':'))
        {
            return null;
        }
        try
        {
            text = new IdnMapping().GetAscii(text);
        }
        catch (ArgumentException)
        {
            return null;
        }
        var labels = text.Split('.');
        if (labels.Length < 2 || labels.Any(label => label.Length is 0 or > 63 || label.StartsWith('-') || label.EndsWith('-')
                || !label.All(ch => char.IsAsciiLetterLower(ch) || char.IsAsciiDigit(ch) || ch == '-')))
        {
            return null;
        }
        // "1.2.3.4" is an address, not a site; and a numeric top-level domain does not exist.
        return labels[^1].All(char.IsAsciiDigit) ? null : text;
    }

    /// <summary>"192.0.2.0/24", "2001:db8::/32" or one address, as a canonical CIDR; null when invalid.</summary>
    public static string? NormalizeNetwork(string? value)
    {
        if (!KsValidator.TryParseNetwork(value, out var network) || network.Prefix == 0)
        {
            return null;
        }
        return network.ToString();
    }

    /// <summary>A full path to a program (".exe"); null when it is not one.</summary>
    public static string? NormalizeApp(string? value)
    {
        var text = (value ?? "").Trim().Trim('"');
        return KsValidator.IsPlainExePath(text) ? text : null;
    }

    public static List<string> NormalizeList(IEnumerable<string>? values, Func<string?, string?> normalize, int max) =>
        (values ?? [])
            .Select(normalize)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .ToList();

    /// <summary>Whether the split lists change anything (a mode with no usable entry is off).</summary>
    public static bool IsActive(ColituVpnPreferences preferences, bool tun) =>
        preferences.SplitTunnelMode != ColituSplitTunnelModes.Off && EntryCount(preferences, tun) > 0;

    /// <summary>Entries that take effect in this mode (apps only count in TUN mode).</summary>
    public static int EntryCount(ColituVpnPreferences preferences, bool tun) =>
        (tun ? (preferences.SplitTunnelApps?.Count ?? 0) : 0)
        + (preferences.SplitTunnelDomains?.Count ?? 0)
        + (preferences.SplitTunnelNetworks?.Count ?? 0);

    /// <summary>TUN mode with apps listed: only sing-box can match programs, so every transport runs on it.</summary>
    public static bool NeedsSingBox(ColituVpnPreferences preferences, bool tun) =>
        tun && preferences.SplitTunnelMode != ColituSplitTunnelModes.Off && (preferences.SplitTunnelApps?.Count ?? 0) > 0;

    /// <summary>Process entries for one app: its full path and its file name (either matches).</summary>
    public static IEnumerable<string> ProcessMatchers(string path)
    {
        yield return path;
        var name = Path.GetFileName(path);
        if (!string.IsNullOrEmpty(name))
        {
            yield return name;
        }
    }

    /// <summary>
    /// The split-tunnel rules, in order: apps, sites, addresses (bypass: to direct; only: to the
    /// proxy). the final rule is the final catch-all of "only selected", which must come
    /// after every other Colitu rule.
    /// </summary>
    public static (List<RulesItem> Rules, RulesItem? Final) BuildRules(ColituVpnPreferences preferences, bool tun)
    {
        preferences = preferences.Normalize();
        var rules = new List<RulesItem>();
        if (!IsActive(preferences, tun))
        {
            return (rules, null);
        }
        var only = preferences.SplitTunnelMode == ColituSplitTunnelModes.Only;
        var outbound = only ? Global.ProxyTag : Global.DirectTag;

        if (tun && preferences.SplitTunnelApps is { Count: > 0 } apps)
        {
            rules.Add(new RulesItem
            {
                Id = RuleAppsId,
                Remarks = only ? "Split tunnelling: apps that use the VPN" : "Split tunnelling: apps outside the VPN",
                OutboundTag = outbound,
                Process = apps.SelectMany(ProcessMatchers).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Enabled = true
            });
        }
        if (preferences.SplitTunnelDomains is { Count: > 0 } domains)
        {
            rules.Add(new RulesItem
            {
                Id = RuleDomainsId,
                Remarks = only ? "Split tunnelling: sites that use the VPN" : "Split tunnelling: sites outside the VPN",
                OutboundTag = outbound,
                // "domain:" matches the domain and its subdomains (Xray), domain_suffix in sing-box.
                Domain = domains.Select(domain => $"domain:{domain}").ToList(),
                Enabled = true
            });
        }
        if (preferences.SplitTunnelNetworks is { Count: > 0 } networks)
        {
            rules.Add(new RulesItem
            {
                Id = RuleNetworksId,
                Remarks = only ? "Split tunnelling: addresses that use the VPN" : "Split tunnelling: addresses outside the VPN",
                OutboundTag = outbound,
                Ip = networks.ToList(),
                Enabled = true
            });
        }

        RulesItem? final = only
            ? new RulesItem
            {
                Id = RuleRestId,
                Remarks = "Split tunnelling: everything else direct",
                OutboundTag = Global.DirectTag,
                Port = "0-65535",
                Network = "tcp,udp",
                Enabled = true
            }
            : null;
        return (rules, final);
    }

    /// <summary>Kill-switch side: which apps and networks the WFP allow list names.</summary>
    public static (string Mode, List<string> Apps, List<string> Networks) KillSwitchEntries(ColituVpnPreferences preferences, bool tun)
    {
        preferences = preferences.Normalize();
        if (!IsActive(preferences, tun))
        {
            return (KsProtocol.SplitOff, [], []);
        }
        var mode = preferences.SplitTunnelMode == ColituSplitTunnelModes.Only ? KsProtocol.SplitOnly : KsProtocol.SplitBypass;
        // In proxy mode an app setting cannot split anything, so it must not open the firewall either.
        var apps = tun ? (preferences.SplitTunnelApps ?? []).ToList() : [];
        return (mode, apps, (preferences.SplitTunnelNetworks ?? []).ToList());
    }

    public static bool IsIpv6(string network) => KsValidator.TryParseNetwork(network, out var parsed) && parsed.Address.AddressFamily == AddressFamily.InterNetworkV6;
}
