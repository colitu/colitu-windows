using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace v2rayN.Services;

/// <summary>
/// In-app live support against the panel's <c>/api/v1/support</c> API:
/// conversations, messages with attachments, automatic diagnostics and the
/// unread counter that drives the nav badge and tray notification.
/// </summary>
public sealed partial class ColituSupportService
{
    public static ColituSupportService Instance { get; } = new();

    public const int MaxFileBytes = 10 << 20;
    public const int MaxFilesPerMessage = 5;

    /// <summary>Largest attachment the app downloads (support files are at most 10 MB; replies from the team a bit more).</summary>
    private const long MaxDownloadBytes = 50L << 20;

    /// <summary>File types the panel stores (it re-checks the content itself).</summary>
    public static readonly string[] AllowedExtensions = [".png", ".jpg", ".jpeg", ".webp", ".gif", ".pdf", ".txt", ".log", ".zip", ".json", ".gz"];

    private readonly ColituAuthService _auth = ColituAuthService.Instance;
    private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    private ColituSupportService() { }

    public async Task<List<ColituSupportConversation>> ListAsync()
    {
        using var response = await _auth.SendAuthorizedRequestAsync(() => new HttpRequestMessage(HttpMethod.Get, _auth.ApiUri("/support/conversations")));
        return (await _auth.ReadResponseJsonAsync<Envelope<List<ColituSupportConversation>>>(response))?.Data ?? [];
    }

    public async Task<ColituSupportThread> ThreadAsync(string id)
    {
        using var response = await _auth.SendAuthorizedRequestAsync(() => new HttpRequestMessage(HttpMethod.Get, _auth.ApiUri($"/support/conversations/{Uri.EscapeDataString(id)}")));
        return (await _auth.ReadResponseJsonAsync<Envelope<ColituSupportThread>>(response))?.Data ?? new();
    }

    public async Task<int> UnreadAsync()
    {
        using var response = await _auth.SendAuthorizedRequestAsync(() => new HttpRequestMessage(HttpMethod.Get, _auth.ApiUri("/support/unread")));
        return (await _auth.ReadResponseJsonAsync<Envelope<UnreadDto>>(response))?.Data?.Unread ?? 0;
    }

    public async Task<ColituSupportConversation?> CreateAsync(string subject, string message, IReadOnlyList<string> files, ColituSupportDiagnostics? diagnostics)
    {
        var payload = new { subject, message, locale = Loc.I.Language, diagnostics };
        using var response = await _auth.SendAuthorizedRequestAsync(() => Multipart(HttpMethod.Post, "/support/conversations", payload, files), transfer: files.Count > 0);
        return (await _auth.ReadResponseJsonAsync<Envelope<ColituSupportConversation>>(response))?.Data;
    }

    public async Task<ColituSupportMessage?> ReplyAsync(string conversationId, string body, IReadOnlyList<string> files)
    {
        using var response = await _auth.SendAuthorizedRequestAsync(() => Multipart(HttpMethod.Post, $"/support/conversations/{Uri.EscapeDataString(conversationId)}/messages", new { body }, files), transfer: files.Count > 0);
        return (await _auth.ReadResponseJsonAsync<Envelope<ColituSupportMessage>>(response))?.Data;
    }

    /// <summary>
    /// Where opened attachments go: under the app's admin-only guiTemps, never the user's
    /// %TEMP%. Non-elevated processes of the same user can change %TEMP%, and the elevated
    /// app creating, writing and recursively deleting there could be redirected with a
    /// junction to any folder. Each download folder also lets the signed-in user read, so
    /// their own (non-elevated) Explorer can still open the file; see <see cref="DownloadAsync"/>.
    /// </summary>
    internal static string DownloadRoot => Path.Combine(Utils.GetTempPath(), "support");

    /// <summary>Downloads an attachment into the temp folder and returns its path.</summary>
    public async Task<string> DownloadAsync(ColituSupportAttachment attachment)
    {
        using var response = await _auth.SendAuthorizedRequestAsync(() => new HttpRequestMessage(HttpMethod.Get, _auth.ApiUri($"/support/attachments/{Uri.EscapeDataString(attachment.Id)}")), transfer: true);
        if (response.Content.Headers.ContentLength > MaxDownloadBytes)
        {
            throw new InvalidOperationException("Attachment is too large.");
        }
        var path = Path.Combine(CreateDownloadFolder(), AttachmentFileName(attachment));
        try
        {
            await using var source = await response.Content.ReadAsStreamAsync();
            // CreateNew in a new folder with a random name: never overwrites anything.
            await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            // The server's size header is not trusted: stop writing past the limit.
            var buffer = new byte[81920];
            long written = 0;
            int read;
            while ((read = await source.ReadAsync(buffer)) > 0)
            {
                written += read;
                if (written > MaxDownloadBytes)
                {
                    throw new InvalidOperationException("Attachment is too large.");
                }
                await file.WriteAsync(buffer.AsMemory(0, read));
            }
            await file.DisposeAsync();
            // Mark of the Web: SmartScreen and Office Protected View treat the file (and what is
            // extracted from a .zip) as downloaded from the internet.
            try
            {
                await File.WriteAllTextAsync(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
            }
            catch (IOException)
            {
                // A file system without alternate data streams (FAT): the file still opens.
            }
        }
        catch
        {
            try { File.Delete(path); } catch { }
            throw;
        }
        return path;
    }

    /// <summary>
    /// The server's file name without folders or invalid characters, prefixed with a cleaned
    /// id; a type the panel never stores (.exe, .lnk, .hta...) gets ".download" appended so a
    /// double-click in Explorer can't run it.
    /// </summary>
    internal static string AttachmentFileName(ColituSupportAttachment attachment)
    {
        var name = string.Concat(Path.GetFileName(attachment.FileName ?? "file").Split(Path.GetInvalidFileNameChars())).Trim().TrimEnd('.');
        if (name.Length == 0) name = "file";
        if (name.Length > 120) name = name[^120..];
        if (!AllowedExtensions.Contains(Path.GetExtension(name).ToLowerInvariant()))
        {
            name += ".download";
        }
        // The id comes from the server: only letters, digits and '-' reach the file name.
        var idPart = new string((attachment.Id ?? "").Where(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-').Take(8).ToArray());
        return $"{(idPart.Length == 0 ? "att" : idPart)}-{name}";
    }

    private static string CreateDownloadFolder()
    {
        var root = DownloadRoot;
        // guiTemps is administrators and SYSTEM only (ColituHardening), so is this folder.
        Directory.CreateDirectory(root);
        var folder = new DirectoryInfo(Path.Combine(root, Guid.NewGuid().ToString("N")[..12]));
        folder.Create(DownloadFolderSecurity());
        return folder.FullName;
    }

    /// <summary>Administrators and SYSTEM, plus read for the signed-in user (not for other users of the PC).</summary>
    private static DirectorySecurity DownloadFolderSecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        }
        if (WindowsIdentity.GetCurrent().User is { } user)
        {
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        }
        return security;
    }

    /// <summary>Removes downloaded attachments (on sign-out: they belong to the account).</summary>
    internal static void DeleteDownloads()
    {
        try
        {
            // Only the admin-only folder is deleted recursively. The %TEMP%\ColituSupport folder of
            // earlier versions is left alone: a recursive delete there by the elevated app could be
            // redirected by any process of the user.
            var root = DownloadRoot;
            if (!Directory.Exists(root)) return;
            Directory.Delete(root, recursive: true);
        }
        catch
        {
            // Best effort: a file may still be open in another program.
        }
    }

    public async Task<byte[]> DownloadBytesAsync(string attachmentId)
    {
        using var response = await _auth.SendAuthorizedRequestAsync(() => new HttpRequestMessage(HttpMethod.Get, _auth.ApiUri($"/support/attachments/{Uri.EscapeDataString(attachmentId)}")), transfer: true);
        if (response.Content.Headers.ContentLength > MaxDownloadBytes)
        {
            throw new InvalidOperationException("Attachment is too large.");
        }
        // Same limit as DownloadAsync, enforced while reading: the size header is not trusted.
        await using var source = await response.Content.ReadAsStreamAsync();
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer)) > 0)
        {
            if (memory.Length + read > MaxDownloadBytes)
            {
                throw new InvalidOperationException("Attachment is too large.");
            }
            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }

    /// <summary>Returns a localized reason when a file cannot be attached, otherwise null.</summary>
    public static string? CheckFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > MaxFileBytes || !AllowedExtensions.Contains(info.Extension.ToLowerInvariant()))
            {
                return Loc.I["support.err.file"];
            }
            return null;
        }
        catch
        {
            return Loc.I["support.err.file"];
        }
    }

    private HttpRequestMessage Multipart(HttpMethod method, string path, object payload, IReadOnlyList<string> files)
    {
        if (files.Count == 0)
        {
            return new HttpRequestMessage(method, _auth.ApiUri(path))
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
        }

        var form = new MultipartFormDataContent();
        form.Add(new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), "payload");
        foreach (var file in files)
        {
            // Checked again: the file may have grown (a log) or gone since it was picked.
            if (CheckFile(file) is { } problem)
            {
                throw new ColituApiException(System.Net.HttpStatusCode.BadRequest, $"{Path.GetFileName(file)}: {problem}", "SUPPORT_FILE_TYPE");
            }
            var content = new ByteArrayContent(File.ReadAllBytes(file));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(content, "file", Path.GetFileName(file));
        }
        return new HttpRequestMessage(method, _auth.ApiUri(path)) { Content = form };
    }

    /// <summary>
    /// What support needs to reproduce a problem: versions, connection mode and
    /// state, the last error and the tail of today's log. Credentials inside
    /// share links are masked, and core lines about user traffic are removed
    /// with other host names masked (<see cref="ColituLogPrivacy"/>), so the
    /// sites a user visited never reach the panel.
    /// </summary>
    public static ColituSupportDiagnostics CollectDiagnostics()
    {
        var vpn = ColituVpnService.Instance;
        var errors = new List<string>();
        if (!string.IsNullOrWhiteSpace(vpn.LastError))
        {
            errors.Add(Redact(vpn.LastError!));
        }
        var server = vpn.ConnectedServer;
        return new ColituSupportDiagnostics
        {
            Platform = ColituAuthService.ClientPlatform,
            AppVersion = ColituAuthService.ClientVersion,
            Os = $"{Environment.OSVersion.VersionString} ({(Environment.Is64BitOperatingSystem ? "x64" : "x86")})",
            Device = "Windows PC",
            Network = vpn.Preferences.IsTunMode ? "tun" : "proxy",
            Server = server == null ? (vpn.IsAutoSelection ? "auto" : vpn.SavedServerId) : $"{server.Country ?? server.CountryCode} {server.City} ({server.Id})".Trim(),
            Protocol = vpn.ConnectedProtocol,
            Connected = vpn.Status == ColituVpnStatus.Connected,
            LastErrors = errors,
            Logs = ReadLogTail()
        };
    }

    private static string? ReadLogTail(int maxChars = 24_000)
    {
        try
        {
            var latest = new DirectoryInfo(Utils.GetLogPath())
                .EnumerateFiles("*.txt")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault();
            if (latest == null)
            {
                return null;
            }
            using var stream = new FileStream(latest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > maxChars * 2)
            {
                stream.Seek(-maxChars * 2, SeekOrigin.End);
            }
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd();
            if (text.Length > maxChars)
            {
                text = text[^maxChars..];
            }
            var firstLine = text.IndexOf('\n');
            if (firstLine > 0 && firstLine < 400)
            {
                text = text[(firstLine + 1)..];
            }
            return Redact(text);
        }
        catch
        {
            return null;
        }
    }

    public static string Redact(string text)
    {
        // E-mails first: host masking would turn user@example.com into user@<host>.
        text = Email().Replace(text, "***@***");
        text = ColituLogPrivacy.StripTraffic(text);
        text = Base64Link().Replace(text, "$1***");
        text = ShareLinkSecret().Replace(text, "$1***@");
        text = BearerToken().Replace(text, "Bearer ***");
        text = BasicAuth().Replace(text, "Basic ***");
        text = Jwt().Replace(text, "***");
        text = UrlCredentials().Replace(text, "$1***@");
        text = QuerySecret().Replace(text, "$1***");
        return JsonSecret().Replace(text, "$1\"***\"");
    }

    [GeneratedRegex(@"((?:vless|vmess|trojan|hysteria2|hy2|ss|tuic|socks|socks5)://)[^@\s/]+@", RegexOptions.IgnoreCase)]
    private static partial Regex ShareLinkSecret();

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9\-_.=]+", RegexOptions.IgnoreCase)]
    private static partial Regex BearerToken();

    [GeneratedRegex(@"Basic\s+[A-Za-z0-9+/=]{8,}", RegexOptions.IgnoreCase)]
    private static partial Regex BasicAuth();

    /// <summary>Bare JSON Web Tokens (access tokens) wherever they appear.</summary>
    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]*")]
    private static partial Regex Jwt();

    /// <summary>vmess:// and legacy ss:// links that are base64 as a whole (credentials included, no '@').</summary>
    [GeneratedRegex(@"((?:vmess|ss)://)[A-Za-z0-9+/=_-]{16,}", RegexOptions.IgnoreCase)]
    private static partial Regex Base64Link();

    [GeneratedRegex(@"(""(?:password|uuid|id|auth|auth_str|psk|token|access_token|refresh_token|private_key|privateKey|publicKey|shortId|short_id)""\s*:\s*)""[^""]*""", RegexOptions.IgnoreCase)]
    private static partial Regex JsonSecret();

    /// <summary>user:password@ in http(s) URLs.</summary>
    [GeneratedRegex(@"(https?://)[^@\s/]+:[^@\s/]*@", RegexOptions.IgnoreCase)]
    private static partial Regex UrlCredentials();

    /// <summary>token=, key=, password=, pbk=, sid= ... in links and query strings.</summary>
    [GeneratedRegex(@"(\b(?:password|pass|pwd|token|access_token|refresh_token|key|auth|code|sig|pbk|sid|uuid)=)[^&\s""]+", RegexOptions.IgnoreCase)]
    private static partial Regex QuerySecret();

    /// <summary>E-mail addresses; not the user part of a link (vless://id@server), which ShareLinkSecret masks.</summary>
    [GeneratedRegex(@"(?<![/\w.%+-])[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}\b")]
    private static partial Regex Email();

    private sealed class Envelope<T>
    {
        [JsonPropertyName("data")] public T? Data { get; set; }
    }

    private sealed class UnreadDto
    {
        [JsonPropertyName("unread")] public int Unread { get; set; }
    }
}

public sealed class ColituSupportDiagnostics
{
    [JsonPropertyName("device")] public string? Device { get; set; }
    [JsonPropertyName("os")] public string? Os { get; set; }
    [JsonPropertyName("app_version")] public string? AppVersion { get; set; }
    [JsonPropertyName("platform")] public string? Platform { get; set; }
    [JsonPropertyName("network")] public string? Network { get; set; }
    [JsonPropertyName("server")] public string? Server { get; set; }
    [JsonPropertyName("protocol")] public string? Protocol { get; set; }
    [JsonPropertyName("connected")] public bool? Connected { get; set; }
    [JsonPropertyName("last_errors")] public List<string>? LastErrors { get; set; }
    [JsonPropertyName("logs")] public string? Logs { get; set; }
}

public sealed class ColituSupportConversation
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("subject")] public string? Subject { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("unread")] public int Unread { get; set; }
    [JsonPropertyName("last_message")] public string? LastMessage { get; set; }
    [JsonPropertyName("last_message_at")] public DateTimeOffset LastMessageAt { get; set; }
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ColituSupportThread
{
    [JsonPropertyName("conversation")] public ColituSupportConversation? Conversation { get; set; }
    [JsonPropertyName("messages")] public List<ColituSupportMessage>? Messages { get; set; }
}

public sealed class ColituSupportMessage
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("sender")] public string? Sender { get; set; }
    [JsonPropertyName("admin_name")] public string? AdminName { get; set; }
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; set; }
    [JsonPropertyName("attachments")] public List<ColituSupportAttachment>? Attachments { get; set; }
}

public sealed class ColituSupportAttachment
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("file_name")] public string? FileName { get; set; }
    [JsonPropertyName("content_type")] public string? ContentType { get; set; }
    [JsonPropertyName("size_bytes")] public long Size { get; set; }
    [JsonPropertyName("is_image")] public bool IsImage { get; set; }
}
