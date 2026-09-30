namespace ServiceLib.Common;

public sealed record ColituXrayAssetCheckResult(
    bool Success,
    string AssetDirectory,
    List<string> MissingFiles,
    string? Message = null);

public static class ColituXrayAssets
{
    public const string ColituSubId = "colitu-api";
    private static readonly string[] RequiredDataFiles = ["geoip.dat", "geosite.dat"];
    private static readonly HashSet<string> UnsafeGeositeCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ru",
        "discord",
    };

    public static string GetAssetDirectory()
    {
        return Utils.GetBinPath("", ECoreType.Xray.ToString());
    }

    public static ColituXrayAssetCheckResult EnsureDataFiles()
    {
        var assetDirectory = GetAssetDirectory();
        Directory.CreateDirectory(assetDirectory);

        CopyMissingDataFiles(assetDirectory);

        var missing = RequiredDataFiles
            .Where(file => !File.Exists(Path.Combine(assetDirectory, file)))
            .ToList();

        if (missing.Count > 0)
        {
            var message = $"VPN routing data files are missing: {string.Join(", ", missing)}. Please run setup or reinstall Colitu VPN.";
            Logging.SaveLog(message);
            return new(false, assetDirectory, missing, message);
        }

        return new(true, assetDirectory, [], null);
    }

    public static bool IsColituContext(CoreConfigContext context)
    {
        if (string.Equals(context.Node?.Subid, ColituSubId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return context.AllProxiesMap.Values.Any(node =>
            string.Equals(node.Subid, ColituSubId, StringComparison.OrdinalIgnoreCase));
    }

    public static string SanitizeGeneratedXrayConfig(string configJson)
    {
        if (configJson.IsNullOrEmpty())
        {
            return configJson;
        }

        try
        {
            if (JsonNode.Parse(configJson) is not JsonObject root)
            {
                return configJson;
            }

            var removedCount = 0;
            removedCount += SanitizeRoutingRules(root["routing"]?["rules"] as JsonArray);
            removedCount += SanitizeDnsServers(root["dns"]?["servers"] as JsonArray);

            if (removedCount > 0)
            {
                Logging.SaveLog($"Colitu Xray config sanitized. Removed unsafe geosite rule count={removedCount}");
            }

            return JsonUtils.Serialize(root);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("Failed to sanitize Colitu Xray config", ex);
            return configJson;
        }
    }

    private static int SanitizeRoutingRules(JsonArray? rules)
    {
        if (rules is null)
        {
            return 0;
        }

        var removedCount = 0;
        for (var i = rules.Count - 1; i >= 0; i--)
        {
            if (rules[i] is not JsonObject rule)
            {
                continue;
            }

            removedCount += RemoveUnsafeDomains(rule["domain"] as JsonArray);
            if (rule["domain"] is JsonArray domains && domains.Count == 0)
            {
                rule.Remove("domain");
            }

            if (HasAnyRoutingMatcher(rule))
            {
                continue;
            }

            rules.RemoveAt(i);
            removedCount++;
        }

        return removedCount;
    }

    private static int SanitizeDnsServers(JsonArray? servers)
    {
        if (servers is null)
        {
            return 0;
        }

        var removedCount = 0;
        foreach (var server in servers.OfType<JsonObject>())
        {
            removedCount += RemoveUnsafeDomains(server["domains"] as JsonArray);
            if (server["domains"] is JsonArray domains && domains.Count == 0)
            {
                server.Remove("domains");
            }
        }

        return removedCount;
    }

    private static int RemoveUnsafeDomains(JsonArray? domains)
    {
        if (domains is null)
        {
            return 0;
        }

        var removedCount = 0;
        for (var i = domains.Count - 1; i >= 0; i--)
        {
            if (domains[i] is JsonValue value
                && value.TryGetValue<string>(out var domain)
                && IsUnsafeGeositeRule(domain))
            {
                domains.RemoveAt(i);
                removedCount++;
            }
        }

        return removedCount;
    }

    public static bool IsUnsafeGeositeRule(string? domain)
    {
        if (domain.IsNullOrEmpty() || !domain.StartsWith(Global.GeoSitePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var code = domain[Global.GeoSitePrefix.Length..];
        return UnsafeGeositeCodes.Contains(code);
    }

    private static bool HasAnyRoutingMatcher(JsonObject rule)
    {
        return HasValue(rule, "port")
            || HasValue(rule, "network")
            || HasArrayValue(rule, "inboundTag")
            || HasArrayValue(rule, "ip")
            || HasArrayValue(rule, "domain")
            || HasArrayValue(rule, "protocol")
            || HasArrayValue(rule, "process");
    }

    private static bool HasValue(JsonObject rule, string key)
    {
        return rule[key] is JsonValue value
            && value.TryGetValue<string>(out var text)
            && text.IsNotEmpty();
    }

    private static bool HasArrayValue(JsonObject rule, string key)
    {
        return rule[key] is JsonArray array && array.Count > 0;
    }

    private static void CopyMissingDataFiles(string assetDirectory)
    {
        var sourceDirectory = FindSourceDirectory();
        if (sourceDirectory is null)
        {
            return;
        }

        foreach (var file in RequiredDataFiles)
        {
            var destination = Path.Combine(assetDirectory, file);
            if (File.Exists(destination))
            {
                continue;
            }

            var source = Path.Combine(sourceDirectory, file);
            if (!File.Exists(source))
            {
                continue;
            }

            try
            {
                File.Copy(source, destination, overwrite: true);
                Logging.SaveLog($"Copied Xray routing data file: {file} -> {assetDirectory}");
            }
            catch (Exception ex)
            {
                Logging.SaveLog($"Failed to copy Xray routing data file: {file}", ex);
            }
        }
    }

    private static string? FindSourceDirectory()
    {
        var startupPath = Utils.StartupPath();
        var baseDirectory = Utils.GetBaseDirectory();
        var candidates = new[]
        {
            Path.Combine(startupPath, "xray-dosyalari"),
            Path.Combine(baseDirectory, "xray-dosyalari"),
            Path.Combine(baseDirectory, "..", "xray-dosyalari"),
            Path.Combine(baseDirectory, "..", "..", "xray-dosyalari"),
            Path.Combine(baseDirectory, "..", "..", "..", "xray-dosyalari"),
            Path.Combine(baseDirectory, "..", "..", "..", "..", "xray-dosyalari"),
            Path.Combine(baseDirectory, "..", "..", "..", "..", "..", "xray-dosyalari"),
            Path.Combine(baseDirectory, "..", "..", "..", "..", "..", "..", "xray-dosyalari"),
        };

        foreach (var candidate in candidates)
        {
            var normalized = Path.GetFullPath(candidate);
            if (Directory.Exists(normalized) && RequiredDataFiles.All(file => File.Exists(Path.Combine(normalized, file))))
            {
                return normalized;
            }
        }

        return null;
    }
}
