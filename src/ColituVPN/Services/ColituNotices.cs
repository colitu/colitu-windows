using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace v2rayN.Services;

/// <summary>One in-app notice from <c>GET /client/notices</c> (usage warnings, campaigns).</summary>
public sealed class ColituNotice
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("level")] public string Level { get; set; } = ColituNotices.LevelInfo;
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("body")] public string Body { get; set; } = "";
    [JsonPropertyName("button")] public string? Button { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("push")] public bool Push { get; set; }
    [JsonPropertyName("expires_at")] public string? ExpiresAt { get; set; }

    /// <summary>The call-to-action is shown only with both a label and a safe link.</summary>
    [JsonIgnore] public bool HasButton => !string.IsNullOrWhiteSpace(Button) && !string.IsNullOrWhiteSpace(Url);
}

internal sealed class ColituNoticeListDto
{
    [JsonPropertyName("notices")] public List<ColituNotice>? Notices { get; set; }
}

/// <summary>The pure parts of the notice banner: parsing, picking the next notice, paths, pruning.</summary>
public static class ColituNotices
{
    public const string LevelInfo = "info";
    public const string LevelPromo = "promo";
    public const string LevelWarning = "warning";
    public const string LevelCritical = "critical";

    public const string EventSeen = "seen";
    public const string EventClicked = "clicked";
    public const string EventDismissed = "dismissed";

    /// <summary>At most one fetch per this long while the app runs.</summary>
    public static readonly TimeSpan MinFetchInterval = TimeSpan.FromMinutes(15);

    /// <summary>Remembered ids older than this are forgotten.</summary>
    public static readonly TimeSpan MaxIdAge = TimeSpan.FromDays(90);

    public const int MaxIds = 200;

    private const int MaxTitle = 200;
    private const int MaxBody = 1000;
    private const int MaxButton = 60;
    private const int MaxNotices = 20;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The panel's <c>lang</c> parameter: tr, en or ru.</summary>
    public static string FetchPath(string? language)
    {
        var lang = (language ?? "").Trim().ToLowerInvariant();
        if (lang is not ("tr" or "en" or "ru"))
        {
            lang = "en";
        }
        return $"/client/notices?lang={lang}";
    }

    /// <summary>Notice ids contain ':' ("usage_80:1790812800"), so they are escaped.</summary>
    public static string EventPath(string id) => $"/client/notices/{Uri.EscapeDataString(id)}/events";

    /// <summary>Reads a response body; anything unreadable is "no notices".</summary>
    public static List<ColituNotice> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }
        try
        {
            var dto = JsonSerializer.Deserialize<ColituNoticeListDto>(json, JsonOptions);
            return Sanitize(dto?.Notices);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Drops notices without an id or text, clips the texts, falls back to the neutral
    /// level and removes links that are not plain https (they are never opened).
    /// </summary>
    public static List<ColituNotice> Sanitize(IEnumerable<ColituNotice?>? notices)
    {
        var result = new List<ColituNotice>();
        if (notices == null)
        {
            return result;
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var notice in notices)
        {
            if (notice == null || result.Count >= MaxNotices)
            {
                continue;
            }
            var id = (notice.Id ?? "").Trim();
            var title = Clip(notice.Title, MaxTitle);
            var body = Clip(notice.Body, MaxBody);
            if (id.Length == 0 || id.Length > 200 || (title.Length == 0 && body.Length == 0) || !ids.Add(id))
            {
                continue;
            }
            var url = notice.Url?.Trim();
            var safe = ColituShell.IsSafeLink(url, allowMail: false);
            var button = safe ? Clip(notice.Button, MaxButton) : "";
            result.Add(new ColituNotice
            {
                Id = id,
                Kind = notice.Kind,
                Level = NormalizeLevel(notice.Level),
                Title = title,
                Body = body,
                Button = button.Length > 0 ? button : null,
                Url = safe ? url : null,
                Push = notice.Push,
                ExpiresAt = notice.ExpiresAt
            });
        }
        return result;
    }

    public static string NormalizeLevel(string? level)
    {
        return (level ?? "").Trim().ToLowerInvariant() switch
        {
            LevelPromo => LevelPromo,
            LevelWarning => LevelWarning,
            LevelCritical => LevelCritical,
            _ => LevelInfo
        };
    }

    /// <summary>The first notice that was not dismissed and has not expired; null when none.</summary>
    public static ColituNotice? PickNext(IEnumerable<ColituNotice>? notices, Func<string, bool> isDismissed, DateTimeOffset now)
    {
        foreach (var notice in notices ?? [])
        {
            if (!string.IsNullOrEmpty(notice.Id) && !isDismissed(notice.Id) && !IsExpired(notice, now))
            {
                return notice;
            }
        }
        return null;
    }

    public static bool IsExpired(ColituNotice notice, DateTimeOffset now)
    {
        return !string.IsNullOrWhiteSpace(notice.ExpiresAt)
            && DateTimeOffset.TryParse(notice.ExpiresAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var end)
            && end <= now;
    }

    /// <summary>Forgets ids older than 90 days, then the oldest ones beyond 200 entries.</summary>
    public static void Prune(Dictionary<string, DateTimeOffset> ids, DateTimeOffset now)
    {
        foreach (var key in ids.Where(pair => now - pair.Value > MaxIdAge).Select(pair => pair.Key).ToList())
        {
            ids.Remove(key);
        }
        if (ids.Count <= MaxIds)
        {
            return;
        }
        foreach (var key in ids.OrderBy(pair => pair.Value).Take(ids.Count - MaxIds).Select(pair => pair.Key).ToList())
        {
            ids.Remove(key);
        }
    }

    private static string Clip(string? value, int max)
    {
        var text = (value ?? "").Trim();
        return text.Length <= max ? text : text[..max];
    }
}

/// <summary>
/// The ids of notices this user already saw or closed, kept in a small per-user
/// file next to the other Colitu state. A broken or missing file is an empty set.
/// </summary>
public sealed class ColituNoticeStore
{
    public static ColituNoticeStore Instance { get; } = new(() => ColituHardening.UserConfigPath("colitu-notices.json"));

    private sealed class State
    {
        [JsonPropertyName("dismissed")] public Dictionary<string, DateTimeOffset> Dismissed { get; set; } = [];
        [JsonPropertyName("seen")] public Dictionary<string, DateTimeOffset> Seen { get; set; } = [];
    }

    private readonly Func<string> _path;
    private readonly object _gate = new();
    private State? _state;

    public ColituNoticeStore(Func<string> path)
    {
        _path = path;
    }

    public bool IsDismissed(string id)
    {
        lock (_gate)
        {
            return Load().Dismissed.ContainsKey(id);
        }
    }

    /// <summary>Marks the id as shown; true only the first time (then "seen" is reported).</summary>
    public bool MarkSeen(string id, DateTimeOffset now)
    {
        lock (_gate)
        {
            var state = Load();
            if (state.Seen.ContainsKey(id))
            {
                return false;
            }
            state.Seen[id] = now;
            Save(state, now);
            return true;
        }
    }

    public void MarkDismissed(string id, DateTimeOffset now)
    {
        lock (_gate)
        {
            var state = Load();
            state.Dismissed[id] = now;
            Save(state, now);
        }
    }

    private State Load()
    {
        if (_state != null)
        {
            return _state;
        }
        try
        {
            var path = _path();
            _state = File.Exists(path) ? JsonSerializer.Deserialize<State>(File.ReadAllText(path)) : null;
        }
        catch
        {
            _state = null;
        }
        _state ??= new State();
        _state.Dismissed ??= [];
        _state.Seen ??= [];
        ColituNotices.Prune(_state.Dismissed, DateTimeOffset.UtcNow);
        ColituNotices.Prune(_state.Seen, DateTimeOffset.UtcNow);
        return _state;
    }

    private void Save(State state, DateTimeOffset now)
    {
        ColituNotices.Prune(state.Dismissed, now);
        ColituNotices.Prune(state.Seen, now);
        try
        {
            var path = _path();
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state));
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            // Not persisted: the notice may come back after a restart, nothing worse.
        }
    }
}

/// <summary>Talks to the panel's notice endpoints. Errors never reach the user.</summary>
public sealed class ColituNoticeService
{
    public static ColituNoticeService Instance { get; } = new();

    private ColituNoticeService() { }

    /// <summary>
    /// The notices for the app's language: an empty list when the panel has none (or is too old to
    /// know the endpoint, 404), null when the request failed and the last answer should stay.
    /// </summary>
    public async Task<List<ColituNotice>?> FetchAsync(string language, CancellationToken token = default)
    {
        try
        {
            var dto = await ColituAuthService.Instance.GetAuthorizedJsonAsync<ColituNoticeListDto>(ColituNotices.FetchPath(language), token);
            return ColituNotices.Sanitize(dto?.Notices);
        }
        catch (ColituApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituNoticeService.FetchAsync", ex);
            return null;
        }
    }

    /// <summary>Reports seen, clicked or dismissed. Fire and forget: a failure is only logged.</summary>
    public async Task SendEventAsync(string id, string kind)
    {
        try
        {
            await ColituAuthService.Instance.PostAuthorizedAsync(ColituNotices.EventPath(id), new { @event = kind });
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituNoticeService.SendEventAsync", ex);
        }
    }
}
