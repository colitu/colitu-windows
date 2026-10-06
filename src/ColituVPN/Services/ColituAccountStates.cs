using System.Text.Json;

namespace v2rayN.Services;

/// <summary>
/// Two-factor sign-in (set up on the website only). Contract: the login request carries
/// <c>X-Colitu-Features: mfa</c>; an account with 2FA answers 403
/// <c>{"error":{"code":"MFA_REQUIRED"},"mfa_token":"…","mfa_expires_in":300}</c>, and the code goes
/// to <c>POST /auth/login/mfa {"mfa_token","code"}</c>.
/// </summary>
public static class ColituMfa
{
    public const int CodeLength = 6;
    public const int MaxRecoveryCodeLength = 64;

    public sealed record Challenge(string Token, int ExpiresIn);

    public static Challenge? ParseChallenge(string? body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body ?? "");
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("error", out var error)
                || ErrorCode(error) != "MFA_REQUIRED"
                || !root.TryGetProperty("mfa_token", out var token)
                || token.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(token.GetString())
                || token.GetString()!.Length > 4096)
            {
                return null;
            }
            var expires = root.TryGetProperty("mfa_expires_in", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var seconds)
                ? Math.Clamp(seconds, 30, 3600)
                : 300;
            return new Challenge(token.GetString()!, expires);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The code as the panel expects it, or null. Authenticator codes: exactly six digits (spaces and
    /// dashes of a paste are dropped). Recovery codes: letters, digits and dashes, case kept.
    /// </summary>
    public static string? NormalizeCode(string? input, bool recoveryCode)
    {
        var text = (input ?? "").Trim();
        if (recoveryCode)
        {
            var compact = new string(text.Where(ch => !char.IsWhiteSpace(ch)).ToArray());
            return compact.Length is >= 6 and <= MaxRecoveryCodeLength && compact.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-')
                ? compact
                : null;
        }
        if (text.Any(ch => !(char.IsAsciiDigit(ch) || ch is ' ' or '-' or ' ')))
        {
            return null;
        }
        var digits = new string(text.Where(char.IsAsciiDigit).ToArray());
        return digits.Length == CodeLength ? digits : null;
    }

    /// <summary>"attempts_left" of a 401 MFA_INVALID_CODE, when the panel sends it.</summary>
    public static int? AttemptsLeft(string? body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body ?? "");
            return doc.RootElement.ValueKind == JsonValueKind.Object && ColituTrial.Int(doc.RootElement, "attempts_left") is { } left and >= 0 and < 1000
                ? left
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>What a paste into the six-digit box keeps: its first six digits.</summary>
    public static string DigitsOfPaste(string? text) => new((text ?? "").Where(char.IsAsciiDigit).Take(CodeLength).ToArray());

    internal static string? ErrorCode(JsonElement error) => error.ValueKind switch
    {
        JsonValueKind.Object when error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String => code.GetString(),
        JsonValueKind.String => error.GetString(),
        _ => null
    };
}

public sealed class ColituActiveDevice
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? LastSeenAt { get; set; }
}

/// <summary>
/// 403 <c>{"error":{"code":"DEVICE_OVER_LIMIT"},"device_limit":1,"active_devices":[{"id","name","last_seen_at"}]}</c>
/// from the config or bootstrap calls: this computer is paused because the plan allows fewer devices.
/// </summary>
public sealed class ColituDeviceOverLimit
{
    public int? DeviceLimit { get; set; }
    public List<ColituActiveDevice> ActiveDevices { get; set; } = [];

    public static ColituDeviceOverLimit? Parse(string? body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body ?? "");
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            var result = new ColituDeviceOverLimit
            {
                DeviceLimit = ColituTrial.Int(root, "device_limit") is { } limit and > 0 ? limit : null
            };
            if (root.TryGetProperty("active_devices", out var devices) && devices.ValueKind == JsonValueKind.Array)
            {
                foreach (var device in devices.EnumerateArray().Take(20))
                {
                    if (device.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }
                    result.ActiveDevices.Add(new ColituActiveDevice
                    {
                        Id = ColituTrial.Str(device, "id"),
                        Name = Clean(ColituTrial.Str(device, "name")),
                        LastSeenAt = ColituTrial.Str(device, "last_seen_at")
                    });
                }
            }
            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>"This device is paused — your plan allows {limit} device(s). Active: {name}."</summary>
    public string Describe(Loc loc)
    {
        var limit = DeviceLimit is { } value ? loc.Count("device", value) : null;
        var text = limit != null ? loc.Format("paused.body", ("limit", limit)) : loc["paused.bodyNoLimit"];
        var names = ActiveDevices
            .OrderByDescending(device => DateTimeOffset.TryParse(device.LastSeenAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var seen) ? seen : DateTimeOffset.MinValue)
            .Select(device => device.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();
        return names.Count > 0 ? $"{text} {loc.Format("paused.active", ("names", string.Join(", ", names)))}" : text;
    }

    private static string? Clean(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }
        var text = new string(name.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        return text.Length > 60 ? text[..60] + "…" : text;
    }
}

/// <summary>The trial-ending banner on Home (owner decision: trial end → free plan, one device).</summary>
public static class ColituTrial
{
    public sealed record Info(string? EndsAt, string? NextPlan, long? NextPlanTrafficBytes, int? NextDeviceLimit, int? DeviceCount);

    /// <summary>Reads ends_at, next_plan (a name or an object), next_device_limit and the device count, tolerating any shape.</summary>
    public static Info Parse(IReadOnlyDictionary<string, JsonElement>? extra)
    {
        if (extra == null)
        {
            return new Info(null, null, null, null, null);
        }
        string? endsAt = null;
        string? nextPlan = null;
        long? nextTraffic = null;
        int? nextDevices = null;
        int? deviceCount = null;
        foreach (var (key, value) in extra)
        {
            switch (key)
            {
                case "ends_at" or "trial_ends_at":
                    endsAt = value.ValueKind == JsonValueKind.String ? value.GetString() : endsAt;
                    break;
                case "next_plan":
                    if (value.ValueKind == JsonValueKind.String)
                    {
                        nextPlan = value.GetString();
                    }
                    else if (value.ValueKind == JsonValueKind.Object)
                    {
                        nextPlan = Str(value, "name") ?? Str(value, "id") ?? Str(value, "plan");
                        nextTraffic = Long(value, "traffic_limit_bytes") ?? Long(value, "limit_bytes") ?? Long(value, "monthly_limit_bytes")
                            ?? (Long(value, "traffic_gb") ?? Long(value, "monthly_gb")) * (1L << 30);
                        nextDevices ??= Int(value, "device_limit");
                    }
                    break;
                case "next_device_limit":
                    nextDevices = AsInt(value) ?? nextDevices;
                    break;
                case "next_traffic_limit_bytes":
                    nextTraffic = AsLong(value) ?? nextTraffic;
                    break;
                case "device_count" or "devices_count":
                    deviceCount = AsInt(value) ?? deviceCount;
                    break;
            }
        }
        return new Info(endsAt, string.IsNullOrWhiteSpace(nextPlan) ? null : nextPlan.Trim(), nextTraffic is > 0 ? nextTraffic : null,
            nextDevices is > 0 ? nextDevices : null, deviceCount is >= 0 ? deviceCount : null);
    }

    /// <summary>
    /// The banner text, or null when there is nothing to say: no trial, more than 3 days left,
    /// already over, or dismissed today (<paramref name="dismissedOn"/>, local yyyy-MM-dd).
    /// </summary>
    public static string? BannerText(ColituSubscription? subscription, DateTimeOffset now, string? dismissedOn, Loc loc)
    {
        // A trial, or any plan the panel says turns into the free plan at ends_at.
        var endingToFree = string.Equals(subscription?.Status, "trialing", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subscription?.NextPlan, "free", StringComparison.OrdinalIgnoreCase);
        if (subscription == null || !endingToFree
            || !DateTimeOffset.TryParse(subscription.EndsAt ?? subscription.ExpiresAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var end))
        {
            return null;
        }
        var left = end - now;
        if (left <= TimeSpan.Zero || left > TimeSpan.FromDays(3))
        {
            return null;
        }
        if (dismissedOn != null && dismissedOn == now.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
        {
            return null;
        }

        var days = Math.Max(1, (long)Math.Ceiling(left.TotalDays));
        var text = loc.Format("trial.ends", ("days", loc.Count("day", days)));

        var plan = subscription.NextPlan == null || string.Equals(subscription.NextPlan, "free", StringComparison.OrdinalIgnoreCase)
            ? loc["trial.freePlan"]
            : subscription.NextPlan;
        var details = new List<string>();
        if (subscription.NextPlanTrafficBytes is { } bytes and > 0)
        {
            var gb = bytes / (double)(1L << 30);
            details.Add(loc.Format("trial.gbMonth", ("gb", gb.ToString(gb % 1 == 0 ? "0" : "0.#", loc.Culture))));
        }
        if (subscription.NextDeviceLimit is { } limit and > 0)
        {
            details.Add(loc.Count("device", limit));
        }
        text += " " + loc.Format(details.Count > 0 ? "trial.moveDetails" : "trial.move", ("plan", plan), ("details", string.Join(", ", details)));

        // Only when more devices are on the account than the next plan allows.
        if (subscription.NextDeviceLimit is { } nextLimit && subscription.DeviceCount is { } count && count > nextLimit)
        {
            text += "; " + loc["trial.paused"];
        }
        return text + ".";
    }

    internal static string? Str(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static int? Int(JsonElement parent, string name) => parent.TryGetProperty(name, out var value) ? AsInt(value) : null;

    private static long? Long(JsonElement parent, string name) => parent.TryGetProperty(name, out var value) ? AsLong(value) : null;

    private static int? AsInt(JsonElement value) => AsLong(value) is { } number and >= int.MinValue and <= int.MaxValue ? (int)number : null;

    private static long? AsLong(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetInt64(out var number) => number,
        JsonValueKind.Number when value.TryGetDouble(out var real) && real is >= 0 and < 1e18 => (long)real,
        JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null
    };
}
