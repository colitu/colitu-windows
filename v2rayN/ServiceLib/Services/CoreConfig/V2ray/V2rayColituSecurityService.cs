namespace ServiceLib.Services.CoreConfig;

public partial class CoreConfigV2rayService
{
    private static readonly List<string> ColituDiscordFallbackDomains =
    [
        "domain:discord.com",
        "domain:discordapp.com",
        "domain:discord.gg",
        "domain:cdn.discordapp.com",
        "domain:gateway.discord.gg",
        "domain:media.discordapp.net",
    ];

    private static readonly List<string> ColituSecureRemoteDns =
    [
        "https://cloudflare-dns.com/dns-query",
        "https://dns.google/dns-query",
    ];

    private void ApplyColituSecurityProfile()
    {
        if (!ColituXrayAssets.IsColituContext(context))
        {
            return;
        }

        var assetCheck = ColituXrayAssets.EnsureDataFiles();
        if (!assetCheck.Success)
        {
            throw new InvalidOperationException(assetCheck.Message);
        }

        _coreConfig.routing.domainStrategy = Global.IPIfNonMatch;
        ApplyColituDnsProfile();
        InsertColituRoutingRules();

        Logging.SaveLog(
            $"Colitu routing profile applied. xrayAssets={assetCheck.AssetDirectory}, domainStrategy={_coreConfig.routing.domainStrategy}, ipv6Tun={_config.TunModeItem.EnableIPv6Address}");
    }

    private void ApplyColituDnsProfile()
    {
        var dnsNode = JsonUtils.SerializeToNode(_coreConfig.dns) as JsonObject ?? new JsonObject();
        var servers = NormalizeColituDnsServers(dnsNode["servers"] as JsonArray);

        if (!servers.Any(IsSecureRemoteDnsNode))
        {
            foreach (var dns in ColituSecureRemoteDns)
            {
                servers.Add(dns);
            }
        }

        dnsNode["servers"] = servers;
        dnsNode["tag"] = Global.DnsTag;
        dnsNode["queryStrategy"] = _config.TunModeItem.EnableIPv6Address ? "UseIPv4v6" : "UseIPv4";

        _coreConfig.dns = dnsNode;
    }

    private static JsonArray NormalizeColituDnsServers(JsonArray? existingServers)
    {
        var result = new JsonArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (existingServers != null)
        {
            foreach (var server in existingServers)
            {
                if (server is null || IsLocalDnsNode(server))
                {
                    continue;
                }

                var json = server.ToJsonString();
                if (seen.Add(json))
                {
                    result.Add(JsonNode.Parse(json));
                }
            }
        }

        if (result.Count == 0)
        {
            foreach (var dns in ColituSecureRemoteDns)
            {
                result.Add(dns);
            }
        }

        return result;
    }

    private static bool IsLocalDnsNode(JsonNode server)
    {
        if (server is JsonValue value && value.TryGetValue<string>(out var address))
        {
            return IsLocalDnsAddress(address);
        }

        if (server is JsonObject obj
            && obj["address"] is JsonValue addressValue
            && addressValue.TryGetValue<string>(out var objectAddress))
        {
            return IsLocalDnsAddress(objectAddress);
        }

        return false;
    }

    private static bool IsSecureRemoteDnsNode(JsonNode server)
    {
        if (server is JsonValue value && value.TryGetValue<string>(out var address))
        {
            return ColituSecureRemoteDns.Contains(address, StringComparer.OrdinalIgnoreCase);
        }

        if (server is JsonObject obj
            && obj["address"] is JsonValue addressValue
            && addressValue.TryGetValue<string>(out var objectAddress))
        {
            return ColituSecureRemoteDns.Contains(objectAddress, StringComparer.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool IsLocalDnsAddress(string? address)
    {
        if (address.IsNullOrEmpty())
        {
            return false;
        }

        return address.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || address.StartsWith("dhcp", StringComparison.OrdinalIgnoreCase);
    }

    private void InsertColituRoutingRules()
    {
        var proxyRule = BuildFinalRule();
        var rules = new List<RulesItem4Ray>
        {
            new()
            {
                type = "field",
                domain = ["domain:localhost"],
                outboundTag = Global.DirectTag,
            },
            new()
            {
                type = "field",
                ip = ["geoip:private"],
                outboundTag = Global.DirectTag,
            },
            new()
            {
                type = "field",
                ip = ["geoip:ru"],
                outboundTag = Global.DirectTag,
            },
            new()
            {
                type = "field",
                domain = ColituDiscordFallbackDomains.ToList(),
                outboundTag = proxyRule.outboundTag,
                balancerTag = proxyRule.balancerTag,
            },
        };

        if (!_config.TunModeItem.EnableIPv6Address)
        {
            rules.Add(new()
            {
                type = "field",
                ip = ["::/0"],
                outboundTag = Global.BlockTag,
            });
        }

        RemoveExistingColituRoutingRules();
        _coreConfig.routing.rules.InsertRange(GetColituRoutingInsertIndex(), rules);
    }

    private void RemoveExistingColituRoutingRules()
    {
        _coreConfig.routing.rules.RemoveAll(rule =>
            rule.domain?.Any(domain => domain == "domain:localhost"
                || ColituXrayAssets.IsUnsafeGeositeRule(domain)
                || ColituDiscordFallbackDomains.Contains(domain, StringComparer.OrdinalIgnoreCase)) == true
            || rule.ip?.Any(ip => ip is "geoip:private" or "geoip:ru" or "::/0") == true);
    }

    private int GetColituRoutingInsertIndex()
    {
        var index = 0;
        while (index < _coreConfig.routing.rules.Count && IsInfrastructureRoutingRule(_coreConfig.routing.rules[index]))
        {
            index++;
        }

        return index;
    }

    private static bool IsInfrastructureRoutingRule(RulesItem4Ray rule)
    {
        return rule.inboundTag?.Contains("api") == true
            || rule.inboundTag?.Contains(Global.DnsTag) == true
            || rule.outboundTag == Global.DnsOutboundTag
            || rule.port == "53";
    }
}
