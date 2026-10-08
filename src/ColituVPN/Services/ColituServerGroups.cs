namespace v2rayN.Services;

/// <summary>Servers of one country in the locations list (a lone server stays a plain row).</summary>
public sealed class ColituServerGroup
{
    /// <summary>Normalised country code (UK → GB); falls back to the country name, then to the server id.</summary>
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public string? CountryCode { get; init; }
    public IReadOnlyList<ColituVpnServer> Servers { get; init; } = [];
    /// <summary>A country with one server is shown as a normal row, with no header.</summary>
    public bool IsSingle => Servers.Count == 1;
}

/// <summary>Pure grouping of the location list by country; the view only renders the result.</summary>
public static class ColituServerGroups
{
    public static string KeyOf(ColituVpnServer server)
    {
        var code = (server.CountryCode ?? "").Trim().ToUpperInvariant();
        if (code == "UK") code = "GB";
        if (code.Length > 0) return code;
        var country = (server.Country ?? "").Trim().ToUpperInvariant();
        return country.Length > 0 ? "name:" + country : "id:" + (server.Id ?? server.Name ?? "");
    }

    /// <summary>
    /// Sorts by country name, then city (or name), and groups by country. With <paramref name="flat"/>
    /// (a search is active) every server is its own group, in the same order, so no match is hidden.
    /// </summary>
    public static List<ColituServerGroup> Build(IEnumerable<ColituVpnServer> servers, Func<string?, string?> countryName, StringComparer comparer, bool flat)
    {
        var sorted = servers
            .OrderBy(s => countryName(s.CountryCode) ?? s.Country ?? "", comparer)
            .ThenBy(s => s.City ?? s.Name ?? "", comparer)
            .ToList();

        if (flat)
        {
            return sorted.Select(s => Make(KeyOf(s), [s], countryName)).ToList();
        }

        var order = new List<string>();
        var byKey = new Dictionary<string, List<ColituVpnServer>>(StringComparer.Ordinal);
        foreach (var server in sorted)
        {
            var key = KeyOf(server);
            if (!byKey.TryGetValue(key, out var list))
            {
                byKey[key] = list = [];
                order.Add(key);
            }
            list.Add(server);
        }
        return order.Select(key => Make(key, byKey[key], countryName)).ToList();
    }

    private static ColituServerGroup Make(string key, List<ColituVpnServer> servers, Func<string?, string?> countryName)
    {
        var first = servers[0];
        return new ColituServerGroup
        {
            Key = key,
            CountryCode = first.CountryCode,
            Name = countryName(first.CountryCode) ?? first.Country ?? first.Name ?? "",
            Servers = servers
        };
    }

    /// <summary>The lowest measured ping (above 0) among the servers, or null when none is measured.</summary>
    public static int? BestPing(IEnumerable<ColituVpnServer> servers, Func<ColituVpnServer, int?> ping)
    {
        int? best = null;
        foreach (var server in servers)
        {
            if (ping(server) is > 0 and var ms && (best == null || ms < best))
            {
                best = ms;
            }
        }
        return best;
    }
}
