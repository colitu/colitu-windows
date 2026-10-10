using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace v2rayN.Services;

/// <summary>The accepted content of a signed endpoint list.</summary>
public sealed record ColituEndpointData(long Version, IReadOnlyList<string> Api, IReadOnlyList<string> Web, IReadOnlyList<string> Lists);

/// <summary>
/// API failover through a signed endpoint list (spec: panel repo deploy/endpoints/APP_SPEC.md).
/// The list names the API bases, web origins and list URLs; it is accepted only when its ECDSA P-256
/// signature verifies against the key built into the app and its version is newer than the stored
/// one. The last accepted list and the API base that last worked are stored next to the app's other
/// settings; the built-in values are the fallback. A request goes to the base that last worked
/// first and moves on only after a network-level failure (never after any HTTP response).
/// </summary>
public sealed class ColituEndpointList
{
    public const string KeyId = "e1";

    /// <summary>
    /// Mirror origins (https), given by the build as the "ColituMirrors" assembly metadata (a
    /// comma-separated list). They are not in the public source: release builds set the
    /// ColituMirrors environment variable. Builds without them use only the main domain.
    /// </summary>
    public static readonly IReadOnlyList<string> BuiltInMirrors = ParseMirrors(
        typeof(ColituEndpointList).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "ColituMirrors")?.Value);

    /// <summary>API bases, in order, used when no list was accepted yet.</summary>
    public static readonly IReadOnlyList<string> BuiltInApi = BuildApi(BuiltInMirrors);

    /// <summary>Where the signed list is fetched from, in order, until a list was accepted.</summary>
    public static readonly IReadOnlyList<string> BuiltInLists = BuildLists(BuiltInMirrors);

    /// <summary>Keeps only https:// origins of a comma-separated list and trims the trailing slash.</summary>
    internal static IReadOnlyList<string> ParseMirrors(string? value) =>
        (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(IsPlainHttpsUrl)
            .Select(origin => origin.TrimEnd('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    internal static IReadOnlyList<string> BuildApi(IEnumerable<string> mirrors) =>
        new[] { ColituAuthService.DefaultApiBaseUrl }.Concat(mirrors.Select(origin => origin + "/capi/v1")).ToList();

    internal static IReadOnlyList<string> BuildLists(IEnumerable<string> mirrors) =>
        new[] { "https://colitu.com/downloads/endpoints.json" }.Concat(mirrors.Select(origin => origin + "/downloads/endpoints.json")).ToList();

    private const string PublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEhv55BVmvEisYIRhkejn+4Leuf0KW
        6jrrRvSL4cu4W09jUwc6HTIcq+YUSG1kJ5AF7qK7PlBtf+xRMTQMGM+xPg==
        -----END PUBLIC KEY-----
        """;

    public static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(10);
    private const int MaxListBytes = 64 * 1024;

    private static readonly Lazy<ColituEndpointList> LazyInstance = new(() => new ColituEndpointList(StatePath()));
    public static ColituEndpointList Instance => LazyInstance.Value;

    private readonly object _gate = new();
    private readonly string? _statePath;
    private readonly IReadOnlyList<string> _builtInApi;
    private readonly IReadOnlyList<string> _builtInLists;
    private ColituEndpointData? _data;
    private string? _raw;
    private string? _lastWorked;
    private int _refreshStarted;
    private HttpClient? _http;

    /// <param name="statePath">File that keeps the accepted list and the working base; null keeps nothing on disk.</param>
    /// <param name="mirrors">Mirror origins; null takes the ones embedded by the build.</param>
    internal ColituEndpointList(string? statePath, IEnumerable<string>? mirrors = null)
    {
        _statePath = statePath;
        var origins = mirrors == null ? BuiltInMirrors : ParseMirrors(string.Join(",", mirrors));
        _builtInApi = mirrors == null ? BuiltInApi : BuildApi(origins);
        _builtInLists = mirrors == null ? BuiltInLists : BuildLists(origins);
        Load();
    }

    private static string StatePath() => ColituHardening.UserConfigPath("colitu-endpoints.json");

    /// <summary>Version of the stored list, null when none.</summary>
    public long? StoredVersion
    {
        get { lock (_gate) return _data?.Version; }
    }

    // ---- verification ----------------------------------------------------------------------

    /// <summary>
    /// Checks a signed list (key id, signature over the decoded payload bytes, payload shape,
    /// version newer than <paramref name="storedVersion"/>). Returns null and a reason when it is not acceptable.
    /// </summary>
    public static ColituEndpointData? Verify(string? raw, long? storedVersion, out string reason)
    {
        reason = "";
        try
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                reason = "empty";
                return null;
            }
            using var envelope = JsonDocument.Parse(raw);
            var root = envelope.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryString(root, "key_id", out var keyId)
                || !TryString(root, "payload", out var payloadText)
                || !TryString(root, "signature", out var signatureText))
            {
                reason = "malformed envelope";
                return null;
            }
            if (!string.Equals(keyId, KeyId, StringComparison.Ordinal))
            {
                reason = "unknown key id";
                return null;
            }

            var payload = Convert.FromBase64String(payloadText!);
            var signature = Convert.FromBase64String(signatureText!);
            using (var ecdsa = ECDsa.Create())
            {
                ecdsa.ImportFromPem(PublicKeyPem);
                if (!ecdsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
                {
                    reason = "bad signature";
                    return null;
                }
            }

            using var doc = JsonDocument.Parse(payload);
            var body = doc.RootElement;
            if (body.ValueKind != JsonValueKind.Object
                || !body.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number
                || !schema.TryGetInt64(out var schemaValue) || schemaValue != 1
                || !body.TryGetProperty("version", out var versionElement) || versionElement.ValueKind != JsonValueKind.Number
                || !versionElement.TryGetInt64(out var version))
            {
                reason = "bad payload";
                return null;
            }
            var api = ReadUrls(body, "api", trimSlash: true);
            var web = ReadUrls(body, "web", trimSlash: true);
            var lists = ReadUrls(body, "lists", trimSlash: false);
            if (api == null || web == null || lists == null)
            {
                reason = "bad url arrays";
                return null;
            }
            if (storedVersion is { } stored && version <= stored)
            {
                reason = "not newer than the stored list";
                return null;
            }
            return new ColituEndpointData(version, api, web, lists);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or CryptographicException or ArgumentException or InvalidOperationException)
        {
            reason = ex.GetType().Name;
            return null;
        }
    }

    private static bool TryString(JsonElement root, string name, out string? value)
    {
        value = null;
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        value = element.GetString();
        return value != null;
    }

    private static List<string>? ReadUrls(JsonElement body, string name, bool trimSlash)
    {
        if (!body.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var result = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } text || !IsPlainHttpsUrl(text))
            {
                return null;
            }
            result.Add(trimSlash ? text.TrimEnd('/') : text);
        }
        return result.Count == 0 ? null : result;
    }

    /// <summary>https:// URL without user info, query or fragment.</summary>
    internal static bool IsPlainHttpsUrl(string text)
    {
        if (text.Contains('?') || text.Contains('#') || text.Any(char.IsWhiteSpace)
            || !Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return false;
        }
        return uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo) && !string.IsNullOrEmpty(uri.Host);
    }

    // ---- ordering --------------------------------------------------------------------------

    /// <summary>The base that last worked first, then the others in list order, without duplicates.</summary>
    public static IReadOnlyList<string> Order(IEnumerable<string> bases, string? lastWorked)
    {
        var list = new List<string>();
        foreach (var item in bases)
        {
            var normalized = Normalize(item);
            if (normalized.Length > 0 && !list.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(normalized);
            }
        }
        var last = lastWorked == null ? null : Normalize(lastWorked);
        var index = last == null ? -1 : list.FindIndex(item => string.Equals(item, last, StringComparison.OrdinalIgnoreCase));
        if (index > 0)
        {
            var first = list[index];
            list.RemoveAt(index);
            list.Insert(0, first);
        }
        return list;
    }

    private static string Normalize(string? value) => (value ?? "").Trim().TrimEnd('/');

    /// <summary>The API bases for the next request: the accepted list's, else the built-in ones.</summary>
    public IReadOnlyList<string> Bases()
    {
        lock (_gate)
        {
            return Order(_data?.Api ?? _builtInApi, _lastWorked);
        }
    }

    /// <summary>Every API base the app may talk to (built-in and accepted list), in no particular order.</summary>
    public IReadOnlyList<string> KnownBases()
    {
        lock (_gate)
        {
            return Order(_builtInApi.Concat(_data?.Api ?? []), null);
        }
    }

    /// <summary>The URLs the list is fetched from: the accepted list's, else the built-in ones.</summary>
    public IReadOnlyList<string> ListUrls()
    {
        lock (_gate)
        {
            return (_data?.Lists ?? _builtInLists).ToList();
        }
    }

    /// <summary>All API bases and list URLs (built-in and accepted): their hosts must stay reachable under the kill switch.</summary>
    public IEnumerable<string> PinnedUrls()
    {
        lock (_gate)
        {
            return _builtInApi.Concat(_builtInLists).Concat(_data?.Api ?? []).Concat(_data?.Lists ?? []).ToList();
        }
    }

    // ---- state -----------------------------------------------------------------------------

    /// <summary>Stores a signed list when it is acceptable and newer than the one held.</summary>
    public bool TryAccept(string? raw)
    {
        var data = Verify(raw, StoredVersion, out var reason);
        if (data == null)
        {
            Log($"endpoint list ignored: {reason}");
            return false;
        }
        lock (_gate)
        {
            // A parallel refresh may have stored a newer one in the meantime.
            if (_data != null && data.Version <= _data.Version)
            {
                return false;
            }
            _data = data;
            _raw = raw;
            Save();
        }
        Log($"endpoint list {data.Version} accepted");
        return true;
    }

    /// <summary>Remembers the base that answered, so the next request starts there.</summary>
    public void RememberWorking(string baseUrl)
    {
        var normalized = Normalize(baseUrl);
        lock (_gate)
        {
            if (string.Equals(_lastWorked, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            _lastWorked = normalized;
            Save();
        }
    }

    private void Load()
    {
        if (_statePath == null || !File.Exists(_statePath))
        {
            return;
        }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_statePath));
            var root = doc.RootElement;
            // Verified again on load: the stored text is not trusted just because it is on disk.
            if (TryString(root, "raw", out var raw) && Verify(raw, null, out _) is { } data)
            {
                _data = data;
                _raw = raw;
            }
            if (TryString(root, "last_base", out var last) && !string.IsNullOrWhiteSpace(last))
            {
                _lastWorked = Normalize(last);
            }
        }
        catch
        {
            // Unreadable state: start from the built-in values.
        }
    }

    private void Save()
    {
        if (_statePath == null)
        {
            return;
        }
        try
        {
            var directory = Path.GetDirectoryName(_statePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            var json = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["raw"] = _raw,
                ["version"] = _data?.Version,
                ["last_base"] = _lastWorked
            });
            var temp = _statePath + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            File.Move(temp, _statePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log($"could not save endpoint state: {ex.Message}");
        }
    }

    // ---- refresh ---------------------------------------------------------------------------

    /// <summary>Starts the background refresh: now (never delaying startup), then every 6 hours. Once per process.</summary>
    public void StartBackgroundRefresh()
    {
        if (Interlocked.Exchange(ref _refreshStarted, 1) != 0)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    await RefreshAsync(CancellationToken.None);
                }
                catch
                {
                    // Silent: the built-in or last accepted list stays in use.
                }
                await Task.Delay(RefreshInterval);
            }
        });
    }

    /// <summary>Fetches each list URL in order (10 s each) and stops at the first file that is accepted.</summary>
    public async Task<bool> RefreshAsync(CancellationToken token, Func<string, CancellationToken, Task<string?>>? fetch = null)
    {
        fetch ??= FetchAsync;
        foreach (var url in ListUrls())
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(FetchTimeout);
                var text = await fetch(url, timeout.Token);
                if (text != null && TryAccept(text))
                {
                    return true;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Network errors are silent.
            }
        }
        return false;
    }

    private async Task<string?> FetchAsync(string url, CancellationToken token)
    {
        var http = _http ??= new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            ConnectCallback = ColituPinnedHosts.ConnectAsync
        })
        {
            Timeout = FetchTimeout,
            MaxResponseContentBufferSize = MaxListBytes
        };
        using var response = await http.GetAsync(url, token);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(token) : null;
    }

    // ---- failover --------------------------------------------------------------------------

    /// <summary>
    /// True when <paramref name="ex"/> is a network-level failure before any HTTP response (DNS,
    /// refused, timeout, TLS handshake, reset or closed). For a non-GET request only failures that
    /// happen while connecting count: once the body may have been sent it is not sent again.
    /// </summary>
    public static bool ShouldFailover(Exception ex, bool isGet)
    {
        switch (ex)
        {
            case TaskCanceledException:
                // The client timeout (the caller's own cancellation is handled before).
                return isGet;
            case HttpRequestException http:
                return IsConnectPhase(http) || isGet;
            case SocketException or AuthenticationException or IOException:
                return isGet;
            default:
                return false;
        }
    }

    private static bool IsConnectPhase(HttpRequestException ex)
    {
        if (ex.HttpRequestError is HttpRequestError.NameResolutionError
            or HttpRequestError.ConnectionError
            or HttpRequestError.SecureConnectionError
            or HttpRequestError.ProxyTunnelError)
        {
            return true;
        }
        for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
        {
            if (inner is AuthenticationException)
            {
                return true;
            }
            if (inner is SocketException socket && socket.SocketErrorCode is SocketError.ConnectionRefused
                or SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData
                or SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.TimedOut)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Sends through <paramref name="bases"/> in order, each at most once. A network-level failure
    /// (per <paramref name="shouldFailover"/>) moves to the next base; any response, also 4xx/5xx,
    /// is returned and its base reported through <paramref name="onWorked"/>. The last error is thrown
    /// when no base answered.
    /// </summary>
    public static async Task<HttpResponseMessage> SendWithFailoverAsync(
        IReadOnlyList<string> bases,
        Func<string, Task<HttpResponseMessage>> send,
        Func<Exception, bool> shouldFailover,
        Action<string>? onWorked,
        CancellationToken token)
    {
        Exception? last = null;
        for (var round = 0; round < 2; round++)
        {
            var namesFailed = true;
            for (var i = 0; i < bases.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var response = await send(bases[i]);
                    onWorked?.Invoke(bases[i]);
                    return response;
                }
                catch (Exception ex) when (!token.IsCancellationRequested && shouldFailover(ex))
                {
                    last = ex;
                    namesFailed &= IsNameResolutionFailure(ex);
                    Log($"API base {HostOf(bases[i])} failed ({ex.GetType().Name}); trying the next one");
                }
            }
            // Windows could not resolve any of them (just after a kill switch was released, or a
            // broken resolver): look the names up over DoH from this app, pin them, try once more.
            if (round > 0 || bases.Count == 0 || !namesFailed)
            {
                break;
            }
            Log("No API name resolved; resolving them over DoH and trying again");
            await ColituPinnedHosts.RefreshAsync(bases.Select(HostOf), token);
        }
        throw last ?? new HttpRequestException("No API base configured");
    }

    private static bool IsNameResolutionFailure(Exception ex)
    {
        if (ex is HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError })
        {
            return true;
        }
        for (var inner = ex.InnerException ?? (ex as SocketException as Exception); inner != null; inner = inner.InnerException)
        {
            if (inner is SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData })
            {
                return true;
            }
        }
        return ex is SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData };
    }

    /// <summary>Replaces the known API base at the start of <paramref name="uri"/> with <paramref name="target"/>; other URLs stay.</summary>
    public static Uri Rebase(Uri uri, IEnumerable<string> knownBases, string target)
    {
        var text = uri.AbsoluteUri;
        foreach (var known in knownBases)
        {
            var prefix = Normalize(known);
            if (prefix.Length == 0 || !text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var rest = text[prefix.Length..];
            if (rest.Length == 0 || rest[0] is '/' or '?')
            {
                return new Uri(Normalize(target) + rest);
            }
        }
        return uri;
    }

    private static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    private static void Log(string message)
    {
        try
        {
            Logging.SaveLog($"ColituEndpointList | {message}");
        }
        catch
        {
            // Logging must never break a request.
        }
    }
}
