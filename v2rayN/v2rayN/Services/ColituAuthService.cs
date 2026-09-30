using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace v2rayN.Services;

/// <summary>
/// Account, token and device handling against the Colitu panel client API
/// (<c>https://api.colitu.com/api/v1</c>). The panel contract is documented in
/// the panel repository under <c>api/client/openapi.yaml</c>.
/// </summary>
public sealed class ColituAuthService
{
    public static ColituAuthService Instance { get; } = new();
    public const string ClientPlatform = "windows";
    /// <summary>Product version stamped by the build (scripts/build-installer.ps1 passes -p:Version).</summary>
    public static readonly string ClientVersion = ReadClientVersion();
    public const string DefaultApiBaseUrl = "https://api.colitu.com/api/v1";
    public const string WebBaseUrl = "https://colitu.com";
    public const string SupportEmail = "support@colitu.com";

    /// <summary>Config formats and transports this build can run: Xray for TCP transports, sing-box for Hysteria2.</summary>
    private static readonly string[] SupportedConfigFormats = ["xray-mobile-v1"];
    private static readonly string[] SupportedProtocols = ["hysteria2", "vless-reality", "trojan", "shadowsocks"];

    // Direct to the panel: while the tunnel restarts the system proxy still points at the stopped core.
    private readonly HttpClient _httpClient = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(20) };
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly SemaphoreSlim _deviceLock = new(1, 1);
    private ColituSession _session = new();
    private string? _accessToken;
    private string? _refreshToken;
    private DateTimeOffset? _accessTokenExpiresAt;

    /// <summary>
    /// Raised when the session can no longer be refreshed (revoked, device removed,
    /// account disabled). The UI must send the user back to the sign-in screen.
    /// </summary>
    public event Action<string?>? SessionExpired;

    private ColituAuthService()
    {
        ApiBaseUrl = ResolveApiBaseUrl();
    }

    public string ApiBaseUrl { get; }
    public ColituUser? CurrentUser { get; private set; }
    public ColituSubscription? CurrentSubscription { get; private set; }

    /// <summary>Persistent installation key sent as <c>device_key</c> during registration.</summary>
    public string DeviceId => BuildStableDeviceId();

    /// <summary>Device row id issued by the panel; sent as <c>X-Device-ID</c>.</summary>
    public string? RegisteredDeviceId => _session.RegisteredDeviceId;

    public string DeviceName => BuildDeviceName();

    /// <summary>True once a stored or new session exists on this computer.</summary>
    public bool HasSession => !string.IsNullOrWhiteSpace(_refreshToken);

    /// <summary>Set when the panel rejected the session saved on this computer.</summary>
    public bool SessionWasRevoked { get; private set; }

    /// <summary>
    /// Restores the saved session. The session is only dropped when the panel
    /// definitively rejects it (revoked, reused or device removed); being offline,
    /// rate limited or hitting a server error keeps the user signed in with the
    /// last known account details until the panel is reachable again.
    /// </summary>
    public async Task<ColituStartupState> InitializeAsync()
    {
        LoadSession();
        if (string.IsNullOrWhiteSpace(_refreshToken))
        {
            return ColituStartupState.SignedOut;
        }

        // Sessions created against the retired API cannot be refreshed by the panel.
        if (!string.Equals(_session.ApiBaseUrl, ApiBaseUrl, StringComparison.OrdinalIgnoreCase))
        {
            ClearSession();
            return ColituStartupState.SignedOut;
        }

        CurrentUser = _session.CachedUser;
        CurrentSubscription = _session.CachedSubscription;
        return await ResumeAsync();
    }

    /// <summary>Re-validates the stored session against the panel (at startup and while offline).</summary>
    public async Task<ColituStartupState> ResumeAsync()
    {
        if (string.IsNullOrWhiteSpace(_refreshToken))
        {
            return ColituStartupState.SignedOut;
        }

        try
        {
            if (IsAccessTokenExpiring())
            {
                var outcome = await RefreshSingleFlightAsync(_accessToken);
                if (outcome == ColituRefreshOutcome.Terminal)
                {
                    ClearSession();
                    SessionWasRevoked = true;
                    return ColituStartupState.SignedOut;
                }
                if (outcome == ColituRefreshOutcome.Transient)
                {
                    return ColituStartupState.Offline;
                }
            }

            await EnsureDeviceRegisteredAsync();
            await LoadMeAsync();
            PendingVerification = false;
            return ColituStartupState.SignedIn;
        }
        catch (ColituApiException ex) when (ex.ErrorCode is "EMAIL_NOT_VERIFIED")
        {
            MarkVerificationPending();
            return ColituStartupState.VerificationRequired;
        }
        catch (ColituApiException ex) when (ex.Terminal || ex.ErrorCode is "SESSION_EXPIRED")
        {
            ClearSession();
            SessionWasRevoked = true;
            return ColituStartupState.SignedOut;
        }
        catch (Exception ex)
        {
            // Network failures, timeouts, 5xx and rate limits keep the user signed in.
            Logging.SaveLog("ColituAuthService.ResumeAsync", ex);
            return HasSession ? ColituStartupState.Offline : ColituStartupState.SignedOut;
        }
    }

    public async Task<ColituAuthResult> LoginAsync(string email, string password)
    {
        return await AuthenticateAsync("/auth/login", new { email = email.Trim(), password }, email.Trim(), sendCode: true);
    }

    public async Task<ColituAuthResult> RegisterAsync(string name, string email, string password)
    {
        // The panel account only carries an email address; the display name stays local.
        // Registration already e-mails the first code when verification is required.
        return await AuthenticateAsync("/auth/register", new { email = email.Trim(), password, locale = Loc.I.Language }, email.Trim(), sendCode: false);
    }

    /// <summary>E-mails a six-digit password reset code. Unknown addresses get the same answer.</summary>
    public async Task RequestPasswordResetAsync(string email)
    {
        using var response = await SendClientJsonAsync(HttpMethod.Post, "/auth/password/forgot", new { email = email.Trim(), locale = Loc.I.Language });
        await EnsureSuccessAsync(response);
    }

    /// <summary>
    /// Sets a new password with the e-mailed code. The panel signs the account
    /// out everywhere else and returns tokens for this computer.
    /// </summary>
    public async Task<ColituAuthResult> ResetPasswordAsync(string email, string code, string password)
    {
        return await AuthenticateAsync("/auth/password/reset", new { email = email.Trim(), code = code.Trim(), password }, email.Trim(), sendCode: true);
    }

    /// <summary>True while the signed-in account still has to confirm its e-mail address.</summary>
    public bool PendingVerification { get; private set; }

    /// <summary>The address the verification code was sent to.</summary>
    public string? PendingEmail => _session.PendingEmail;

    /// <summary>E-mails a new six-digit code (the panel allows one a minute).</summary>
    public async Task SendVerificationCodeAsync()
    {
        using var response = await SendAuthorizedAsync(() => JsonRequest(HttpMethod.Post, "/auth/email/send", new { locale = Loc.I.Language }), includeDevice: false);
        await EnsureSuccessAsync(response);
    }

    /// <summary>
    /// Confirms the code, then finishes what sign-in could not do before:
    /// registers this computer and loads the account (the trial starts now).
    /// </summary>
    public async Task VerifyEmailAsync(string code)
    {
        using (var response = await SendAuthorizedAsync(() => JsonRequest(HttpMethod.Post, "/auth/email/verify", new { code = code.Trim() }), includeDevice: false))
        {
            await EnsureSuccessAsync(response);
        }
        PendingVerification = false;
        _session.PendingEmail = null;
        await EnsureDeviceRegisteredAsync(force: true);
        await LoadMeAsync();
    }

    /// <summary>
    /// Checks whether the address was confirmed somewhere else (the website or
    /// another device) and, if so, finishes sign-in here. False while the
    /// account is still unconfirmed.
    /// </summary>
    public async Task<bool> TryCompleteVerificationAsync()
    {
        try
        {
            await EnsureDeviceRegisteredAsync(force: true);
        }
        catch (ColituApiException ex) when (ex.ErrorCode is "EMAIL_NOT_VERIFIED")
        {
            return false;
        }
        PendingVerification = false;
        _session.PendingEmail = null;
        PersistSession();
        await LoadMeAsync();
        return true;
    }

    private void MarkVerificationPending()
    {
        PendingVerification = true;
        PersistSession();
    }

    public async Task<ColituAccount> LoadAccountAsync()
    {
        var me = await LoadMeAsync();
        var devices = await GetAuthorizedJsonAsync<ColituDeviceListDto>("/devices") ?? new();
        var currentId = RegisteredDeviceId;
        return new ColituAccount
        {
            User = me.User,
            Subscription = me.Subscription,
            Devices = (devices.Data ?? [])
                .Where(device => device.RevokedAt == null)
                .Select(device => new ColituDevice
                {
                    Id = device.Id,
                    Name = string.IsNullOrWhiteSpace(device.Name) ? "Device" : device.Name.Trim(),
                    Platform = device.Platform,
                    Current = string.Equals(device.Id, currentId, StringComparison.OrdinalIgnoreCase),
                    LastActiveAt = device.LastSeenAt ?? device.CreatedAt
                })
                .ToList()
        };
    }

    public async Task<T?> GetAuthorizedJsonAsync<T>(string path)
    {
        using var response = await SendAuthorizedAsync(() => new HttpRequestMessage(HttpMethod.Get, BuildUri(path)));
        await EnsureSuccessAsync(response);
        return await ReadJsonAsync<T>(response);
    }

    public async Task<T?> PostAuthorizedJsonAsync<T>(string path, object body)
    {
        using var response = await SendAuthorizedAsync(() => JsonRequest(HttpMethod.Post, path, body));
        await EnsureSuccessAsync(response);
        return await ReadJsonAsync<T>(response);
    }

    public async Task<T?> PutAuthorizedJsonAsync<T>(string path, object body)
    {
        using var response = await SendAuthorizedAsync(() => JsonRequest(HttpMethod.Put, path, body));
        await EnsureSuccessAsync(response);
        return await ReadJsonAsync<T>(response);
    }

    /// <summary>Sends any request with the session's token (refreshing it once on 401) and throws on failure.</summary>
    public async Task<HttpResponseMessage> SendAuthorizedRequestAsync(Func<HttpRequestMessage> requestFactory)
    {
        var response = await SendAuthorizedAsync(requestFactory);
        try
        {
            await EnsureSuccessAsync(response);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public Uri ApiUri(string path) => BuildUri(path);

    public async Task<T?> ReadResponseJsonAsync<T>(HttpResponseMessage response) => await ReadJsonAsync<T>(response);

    public async Task PostAuthorizedAsync(string path, object body)
    {
        using var response = await SendAuthorizedAsync(() => JsonRequest(HttpMethod.Post, path, body));
        await EnsureSuccessAsync(response);
    }

    public async Task RemoveDeviceAsync(string id)
    {
        using var response = await SendAuthorizedAsync(() =>
            new HttpRequestMessage(HttpMethod.Delete, BuildUri($"/devices/{Uri.EscapeDataString(id)}")));
        await EnsureSuccessAsync(response);
        if (string.Equals(id, RegisteredDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            ExpireSession("DEVICE_REVOKED");
        }
    }

    public async Task LogoutAsync()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_refreshToken))
            {
                using var response = await SendClientJsonAsync(HttpMethod.Post, "/auth/logout", new { refresh_token = _refreshToken });
            }
        }
        catch
        {
            // Logout is best-effort; local token removal is the important part.
        }

        ClearSession();
    }

    private async Task<ColituAuthResult> AuthenticateAsync(string path, object body, string email, bool sendCode)
    {
        try
        {
            using var response = await SendClientJsonAsync(HttpMethod.Post, path, body);
            await EnsureSuccessAsync(response);
            var tokens = await ReadJsonAsync<ColituTokenDto>(response);
            if (string.IsNullOrWhiteSpace(tokens?.AccessToken) || string.IsNullOrWhiteSpace(tokens.RefreshToken))
            {
                return ColituAuthResult.Fail("Sign-in response was incomplete.");
            }

            // A new sign-in always re-registers this installation for the signed-in account.
            _session = new ColituSession { PendingEmail = email };
            PendingVerification = false;
            SaveTokens(tokens.AccessToken, tokens.RefreshToken);
            try
            {
                await EnsureDeviceRegisteredAsync(force: true);
                await LoadMeAsync();
                _session.PendingEmail = null;
                PersistSession();
            }
            catch (ColituApiException ex) when (ex.ErrorCode is "EMAIL_NOT_VERIFIED")
            {
                // Keep the tokens: the verification endpoints only need the account.
                MarkVerificationPending();
                if (sendCode)
                {
                    try
                    {
                        await SendVerificationCodeAsync();
                    }
                    catch (Exception sendError)
                    {
                        // A code sent less than a minute ago is still valid, and the
                        // verification screen offers "send again" for anything else.
                        Logging.SaveLog("ColituAuthService.SendVerificationCode", sendError);
                    }
                }
                return ColituAuthResult.Verification(email);
            }
            catch
            {
                ClearSession();
                throw;
            }

            return ColituAuthResult.Ok(CurrentUser);
        }
        catch (Exception ex)
        {
            return ColituAuthResult.Fail(UserMessage(ex));
        }
    }

    /// <summary>
    /// Registers (or refreshes) this installation on the panel. The call is
    /// idempotent: the panel reuses the device row for the same device key.
    /// </summary>
    private async Task EnsureDeviceRegisteredAsync(bool force = false)
    {
        if (!force && !string.IsNullOrWhiteSpace(_session.RegisteredDeviceId)
            && string.Equals(_session.RegisteredAppVersion, ClientVersion, StringComparison.Ordinal))
        {
            return;
        }

        await _deviceLock.WaitAsync();
        try
        {
            var body = new
            {
                device_key = DeviceId,
                name = Truncate(DeviceName, 100),
                platform = ClientPlatform,
                app_version = ClientVersion,
                os_version = Truncate(Environment.OSVersion.VersionString, 100),
                hardware_id = HardwareId,
                capabilities = new
                {
                    config_formats = SupportedConfigFormats,
                    protocols = SupportedProtocols
                }
            };
            using var response = await SendAuthorizedAsync(() => JsonRequest(HttpMethod.Post, "/devices/register", body), includeDevice: false);
            await EnsureSuccessAsync(response);
            var device = await ReadJsonAsync<ColituDeviceDto>(response);
            if (string.IsNullOrWhiteSpace(device?.Id))
            {
                throw new ColituApiException(HttpStatusCode.BadGateway, "Device registration response was incomplete.", "DEVICE_REGISTRATION_INVALID");
            }

            _session.RegisteredDeviceId = device.Id;
            _session.RegisteredAppVersion = ClientVersion;
            PersistSession();
        }
        finally
        {
            _deviceLock.Release();
        }
    }

    /// <summary>Reloads the account and plan (after a purchase, or on the periodic refresh).</summary>
    public async Task RefreshAccountAsync()
    {
        await LoadMeAsync();
    }

    private async Task<ColituMeResponse> LoadMeAsync()
    {
        var me = await GetAuthorizedJsonAsync<ColituMeDto>("/me")
            ?? throw new ColituApiException(HttpStatusCode.BadGateway, "Account response was empty.", "INVALID_USER_RESPONSE");

        ColituEntitlementDto? entitlement = null;
        try
        {
            entitlement = await GetAuthorizedJsonAsync<ColituEntitlementDto>("/me/entitlement");
        }
        catch (ColituApiException ex) when (ex.ErrorCode is "ENTITLEMENT_INACTIVE" or "ENTITLEMENT_EXPIRED")
        {
            entitlement = null;
        }

        var subscription = MapSubscription(entitlement);
        CurrentUser = new ColituUser
        {
            Id = me.Id,
            Email = me.Email,
            Name = DisplayNameFromEmail(me.Email),
            EmailVerified = true,
            DeviceLimit = subscription.DeviceLimit
        };
        CurrentSubscription = subscription;
        _session.CachedUser = CurrentUser;
        _session.CachedSubscription = subscription;
        PersistSession();
        return new ColituMeResponse { User = CurrentUser, Subscription = subscription };
    }

    internal static ColituSubscription MapSubscription(ColituEntitlementDto? entitlement)
    {
        var status = entitlement?.Status?.Trim().ToLowerInvariant() ?? "inactive";
        var expiresAt = DateTimeOffset.TryParse(entitlement?.ExpiresAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : (DateTimeOffset?)null;
        if (status is "active" or "trialing" && expiresAt is { } end && end <= DateTimeOffset.UtcNow)
        {
            status = "expired";
        }
        var active = status is "active" or "trialing";
        var limitBytes = entitlement?.Traffic?.LimitBytes;
        return new ColituSubscription
        {
            PlanName = entitlement?.Plan,
            ProductId = entitlement?.Plan,
            Provider = "colitu",
            Status = status,
            Active = active,
            Premium = active,
            PremiumAllowed = active,
            Unlimited = active && limitBytes == null,
            Tier = active ? "premium" : "free",
            DeviceLimit = entitlement?.DeviceLimit ?? entitlement?.Devices?.Limit ?? 1,
            DevicesUsed = entitlement?.Devices?.Used ?? 0,
            ExpiresAt = entitlement?.ExpiresAt,
            TrafficLimitBytes = limitBytes,
            TrafficUsedBytes = entitlement?.Traffic?.UsedBytes ?? 0,
            TrafficRemainingBytes = entitlement?.Traffic?.RemainingBytes
        };
    }

    private async Task<HttpResponseMessage> SendAuthorizedAsync(Func<HttpRequestMessage> requestFactory, bool allowRefresh = true, bool includeDevice = true)
    {
        // Refresh proactively just before the access token expires so that
        // parallel requests do not all hit 401 and race each other to refresh.
        if (allowRefresh && IsAccessTokenExpiring() && !string.IsNullOrWhiteSpace(_refreshToken))
        {
            await RefreshSingleFlightAsync(_accessToken);
        }

        var tokenUsed = _accessToken;
        var request = PrepareAuthorized(requestFactory(), tokenUsed, includeDevice);
        var response = await _httpClient.SendAsync(request);
        if (response.StatusCode != HttpStatusCode.Unauthorized || !allowRefresh)
        {
            return response;
        }

        response.Dispose();
        var outcome = await RefreshSingleFlightAsync(tokenUsed);
        if (outcome == ColituRefreshOutcome.Terminal)
        {
            ExpireSession("SESSION_EXPIRED");
            throw SessionExpiredException();
        }
        if (outcome == ColituRefreshOutcome.Transient)
        {
            throw new ColituApiException(
                HttpStatusCode.Unauthorized,
                "Could not refresh the session. Please check your connection and try again.",
                "REFRESH_FAILED");
        }

        return await _httpClient.SendAsync(PrepareAuthorized(requestFactory(), _accessToken, includeDevice));
    }

    private HttpRequestMessage PrepareAuthorized(HttpRequestMessage request, string? token, bool includeDevice)
    {
        AddClientHeaders(request, includeDevice);
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new("Bearer", token);
        }
        return request;
    }

    private enum ColituRefreshOutcome
    {
        Success,
        Transient,
        Terminal
    }

    private bool IsAccessTokenExpiring()
    {
        if (string.IsNullOrWhiteSpace(_accessToken)) return true;
        return _accessTokenExpiresAt != null
            && DateTimeOffset.UtcNow >= _accessTokenExpiresAt.Value - TimeSpan.FromSeconds(60);
    }

    /// <summary>
    /// Only one refresh runs at a time. Callers that lost the race reuse the
    /// token produced by the winning refresh instead of burning the (rotating)
    /// refresh token a second time; the panel revokes the whole token family on reuse.
    /// </summary>
    private async Task<ColituRefreshOutcome> RefreshSingleFlightAsync(string? tokenUsed)
    {
        await _refreshLock.WaitAsync();
        try
        {
            var refreshedByOtherCaller = !string.IsNullOrWhiteSpace(_accessToken)
                && !string.Equals(_accessToken, tokenUsed, StringComparison.Ordinal)
                && !IsAccessTokenExpiring();
            if (refreshedByOtherCaller)
            {
                return ColituRefreshOutcome.Success;
            }

            return await RefreshLockedAsync();
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<ColituRefreshOutcome> RefreshLockedAsync()
    {
        if (string.IsNullOrWhiteSpace(_refreshToken)) return ColituRefreshOutcome.Terminal;
        try
        {
            using var response = await SendClientJsonAsync(HttpMethod.Post, "/auth/refresh", new { refresh_token = _refreshToken }, includeDevice: true);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            {
                // Revoked, reused or device-removed refresh tokens cannot recover.
                return ColituRefreshOutcome.Terminal;
            }
            if (!response.IsSuccessStatusCode)
            {
                // Rate limits and server hiccups are retryable; keep the session.
                return ColituRefreshOutcome.Transient;
            }
            var tokens = await ReadJsonAsync<ColituTokenDto>(response);
            if (string.IsNullOrWhiteSpace(tokens?.AccessToken)) return ColituRefreshOutcome.Transient;
            SaveTokens(tokens.AccessToken, tokens.RefreshToken ?? _refreshToken!);
            return ColituRefreshOutcome.Success;
        }
        catch
        {
            return ColituRefreshOutcome.Transient;
        }
    }

    private void ExpireSession(string? reason)
    {
        ClearSession();
        SessionExpired?.Invoke(reason);
    }

    private static ColituApiException SessionExpiredException()
    {
        return new ColituApiException(
            HttpStatusCode.Unauthorized,
            Loc.I["auth.expired"],
            "SESSION_EXPIRED",
            null,
            terminal: true);
    }

    private async Task<HttpResponseMessage> SendClientJsonAsync(HttpMethod method, string path, object body, bool includeDevice = false)
    {
        var request = JsonRequest(method, path, body);
        AddClientHeaders(request, includeDevice);
        return await _httpClient.SendAsync(request);
    }

    private HttpRequestMessage JsonRequest(HttpMethod method, string path, object body)
    {
        return new HttpRequestMessage(method, BuildUri(path))
        {
            Content = JsonContent.Create(body)
        };
    }

    private void AddClientHeaders(HttpRequestMessage request, bool includeDevice)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", $"ColituVPN/{ClientVersion} (Windows)");
        if (includeDevice && !string.IsNullOrWhiteSpace(_session.RegisteredDeviceId))
        {
            request.Headers.TryAddWithoutValidation("X-Device-ID", _session.RegisteredDeviceId);
        }
    }

    private async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0)
        {
            return default;
        }
        return await response.Content.ReadFromJsonAsync<T>(_jsonOptions);
    }

    private void SaveTokens(string accessToken, string refreshToken)
    {
        _accessToken = accessToken;
        _refreshToken = refreshToken;
        _accessTokenExpiresAt = ParseJwtExpiry(accessToken);
        _session.AccessToken = Protect(accessToken);
        _session.RefreshToken = Protect(refreshToken);
        _session.ApiBaseUrl = ApiBaseUrl;
        PersistSession();
    }

    private void PersistSession()
    {
        if (string.IsNullOrWhiteSpace(_session.RefreshToken))
        {
            return;
        }
        _session.UpdatedAt = DateTimeOffset.UtcNow;
        try
        {
            // Write-then-rename so a crash or power loss never leaves a torn session file.
            var path = SessionPath();
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_session, _jsonOptions));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituAuthService.PersistSession", ex);
        }
    }

    private void LoadSession()
    {
        try
        {
            // Versions before 2.4.1 kept one session file for every Windows user; adopt
            // it only when it decrypts for this user (DPAPI throws otherwise).
            var legacy = !File.Exists(SessionPath()) && File.Exists(LegacySessionPath());
            var source = legacy ? LegacySessionPath() : SessionPath();
            if (!File.Exists(source)) return;
            _session = JsonSerializer.Deserialize<ColituSession>(File.ReadAllText(source), _jsonOptions) ?? new();
            _accessToken = Unprotect(_session.AccessToken);
            _refreshToken = Unprotect(_session.RefreshToken);
            _accessTokenExpiresAt = ParseJwtExpiry(_accessToken);
            if (legacy)
            {
                PersistSession();
                try { File.Delete(LegacySessionPath()); } catch { }
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituAuthService.LoadSession", ex);
            _session = new();
            _accessToken = null;
            _refreshToken = null;
        }
    }

    private void ClearSession()
    {
        _session = new();
        _accessToken = null;
        _refreshToken = null;
        _accessTokenExpiresAt = null;
        PendingVerification = false;
        CurrentUser = null;
        CurrentSubscription = null;
        try
        {
            if (File.Exists(SessionPath())) File.Delete(SessionPath());
        }
        catch
        {
            // Ignore local cleanup failures.
        }
    }

    private static string ResolveApiBaseUrl()
    {
#if DEBUG
        // Release builds ignore the variable: a user-level variable would send the
        // password and tokens of this elevated app to any server.
        var env = Environment.GetEnvironmentVariable("COLITU_API_BASE_URL");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim().TrimEnd('/');
#endif

        // guiConfigs is writable by administrators only.
        var configPath = Utils.GetConfigPath("colitu-api.json");
        if (File.Exists(configPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
                if (doc.RootElement.TryGetProperty("apiBaseUrl", out var value))
                {
                    var configured = value.GetString();
                    if (IsAllowedApiBaseUrl(configured)) return configured!.Trim().TrimEnd('/');
                }
            }
            catch
            {
                // Fall through to the production API.
            }
        }

        return DefaultApiBaseUrl;
    }

    /// <summary>Passwords and tokens never travel in clear text: only https (or a local mock in debug builds).</summary>
    internal static bool IsAllowedApiBaseUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }
#if DEBUG
        if (uri.IsLoopback) return true;
#endif
        return uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo);
    }

    private static string BuildStableDeviceId()
    {
        var machineGuid = ReadWindowsMachineGuid();
        var userSid = ReadWindowsUserSid();
        var seed = string.Join("|", new[]
        {
            "colitu-windows-device-v1",
            machineGuid,
            userSid,
            Environment.MachineName,
            Environment.UserName
        }.Where(value => !string.IsNullOrWhiteSpace(value)));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return $"win-stable-{Convert.ToHexString(hash).ToLowerInvariant()[..32]}";
    }

    /// <summary>
    /// A hash of the Windows installation (MachineGuid), shared by every user of
    /// this PC. The panel uses it only to stop the free trial being claimed again
    /// from the same computer; the raw id never leaves the device.
    /// </summary>
    private static string HardwareId
    {
        get
        {
            var machine = ReadWindowsMachineGuid();
            var seed = string.IsNullOrWhiteSpace(machine) ? BuildStableDeviceId() : machine.Trim().ToLowerInvariant();
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes("colitu-hardware-v1|" + seed));
            return "win-" + Convert.ToHexString(hash).ToLowerInvariant();
        }
    }

    private static string BuildDeviceName()
    {
        var machine = string.IsNullOrWhiteSpace(Environment.MachineName) ? "Windows PC" : Environment.MachineName.Trim();
        var user = string.IsNullOrWhiteSpace(Environment.UserName) ? null : Environment.UserName.Trim();
        return string.IsNullOrWhiteSpace(user) ? machine : $"{machine} ({user})";
    }

    private static string DisplayNameFromEmail(string? email)
    {
        var local = (email ?? "").Split('@')[0].Trim();
        return local.Length == 0 ? "Colitu user" : local;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private static string? ReadWindowsMachineGuid()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid")?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadWindowsUserSid()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            return WindowsIdentity.GetCurrent()?.User?.Value;
        }
        catch
        {
            return null;
        }
    }

    private Uri BuildUri(string path)
    {
        return new Uri($"{ApiBaseUrl}{path}");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync();
        string? code = null;
        string? message = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object)
                {
                    code = error.TryGetProperty("code", out var codeElement) ? codeElement.GetString() : null;
                    message = error.TryGetProperty("message", out var messageElement) ? messageElement.GetString() : null;
                }
                else if (error.ValueKind == JsonValueKind.String)
                {
                    code = error.GetString();
                }
            }
        }
        catch (JsonException)
        {
            // Non-JSON error bodies (proxies, gateways) fall back to the status code below.
        }

        var terminal = code is "AUTH_REFRESH_REUSED" or "DEVICE_REVOKED" or "DEVICE_TOKEN_MISMATCH";
        int? retryAfter = response.Headers.RetryAfter?.Delta is { } delta ? (int)delta.TotalSeconds : null;
        throw new ColituApiException(response.StatusCode, FriendlyMessage(code, message, response.StatusCode), code, retryAfter, terminal);
    }

    internal static string FriendlyMessage(string? code, string? message, HttpStatusCode status)
    {
        var loc = Loc.I;
        return code switch
        {
            "AUTH_INVALID_CREDENTIALS" => loc["err.credentials"],
            "INVALID_REGISTRATION" => loc["err.registration"],
            "RATE_LIMITED" => loc["err.rateLimited"],
            "DEVICE_LIMIT_REACHED" or "DEVICE_LIMIT_EXCEEDED" => loc["err.deviceLimit"],
            "REGION_NOT_SUPPORTED" => loc["err.region"],
            "TRIAL_ALREADY_USED" => loc["err.trialUsed"],
            "EMAIL_NOT_VERIFIED" => loc["err.notVerified"],
            "VERIFICATION_CODE_INVALID" => loc["verify.err.invalid"],
            "VERIFICATION_CODE_EXPIRED" => loc["verify.err.expired"],
            "VERIFICATION_RATE_LIMITED" => loc["verify.err.wait"],
            "AUTH_INVALID_PASSWORD" => loc["auth.err.password"],
            "EMAIL_DELIVERY_UNAVAILABLE" => loc["verify.err.mail"],
            "SUPPORT_FILE_TOO_LARGE" or "SUPPORT_FILE_TYPE" => loc["support.err.file"],
            "SUPPORT_UNAVAILABLE" => loc["support.err.unavailable"],
            "SUPPORT_CONVERSATION_CLOSED" => loc["support.closed"],
            "SUPPORT_INVALID_INPUT" => loc["support.err.subject"],
            "ENTITLEMENT_INACTIVE" or "ENTITLEMENT_EXPIRED" => loc["err.noPlan"],
            "QUOTA_EXCEEDED" => loc["err.quota"],
            "DEVICE_REVOKED" or "DEVICE_TOKEN_MISMATCH" or "AUTH_REFRESH_REUSED" or "AUTH_TOKEN_EXPIRED" => loc["auth.expired"],
            "NO_HEALTHY_NODES" or "CONFIG_NOT_AVAILABLE" or "INVALID_PREFERENCE" => loc["err.noServers"],
            _ when (int)status >= 500 => loc["err.network"],
            _ when !string.IsNullOrWhiteSpace(message) => char.ToUpperInvariant(message[0]) + message[1..] + ".",
            _ => loc["err.generic"]
        };
    }

    private static string UserMessage(Exception ex)
    {
        return ex is ColituApiException apiException
            ? apiException.Message
            : Loc.I["err.network"];
    }

    private static string Protect(string value)
    {
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    private static string? Unprotect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var bytes = ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }

    private static DateTimeOffset? ParseJwtExpiry(string? token)
    {
        try
        {
            var parts = token?.Split('.');
            if (parts is not { Length: 3 }) return null;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            return doc.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string SessionPath() => ColituHardening.UserConfigPath("colitu-auth-session.json");

    private static string LegacySessionPath() => Utils.GetConfigPath("colitu-auth-session.json");

    private static string ReadClientVersion()
    {
        var assembly = System.Reflection.Assembly.GetEntryAssembly() ?? typeof(ColituAuthService).Assembly;
        var informational = assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;
        var version = (informational ?? assembly.GetName().Version?.ToString(3) ?? "").Split('+')[0].Trim();
        return System.Version.TryParse(version, out var parsed) ? parsed.ToString(3) : "2.1.0";
    }

    /// <summary>major*100 + minor*10 + patch, the scheme of latest.json's latestVersionCode.</summary>
    public static int VersionCode(string version)
    {
        return System.Version.TryParse(version, out var parsed)
            ? parsed.Major * 100 + parsed.Minor * 10 + Math.Max(0, parsed.Build)
            : 0;
    }
}

public sealed class ColituAuthResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public ColituUser? User { get; init; }
    public bool RequiresEmailVerification { get; init; }
    public bool? VerificationEmailSent { get; init; }
    public string? Message { get; init; }

    public static ColituAuthResult Ok(ColituUser? user, bool? verificationEmailSent = null, string? message = null) => new()
    {
        Success = true,
        User = user,
        VerificationEmailSent = verificationEmailSent,
        Message = message
    };
    public static ColituAuthResult Fail(string error) => new() { Success = false, Error = error };
    public static ColituAuthResult Verification(string email) => new() { Success = true, RequiresEmailVerification = true, Message = email };
}

public sealed class ColituAccount
{
    public ColituUser? User { get; init; }
    public ColituSubscription? Subscription { get; init; }
    public List<ColituDevice> Devices { get; init; } = [];
}

public sealed class ColituUser
{
    public string? Id { get; set; }
    public string? Email { get; set; }
    public string? Name { get; set; }
    public string? Phone { get; set; }
    public bool EmailVerified { get; set; } = true;
    public int DeviceLimit { get; set; } = 1;
}

public sealed class ColituSubscription
{
    public string? PlanName { get; set; }
    public string? ProductId { get; set; }
    public string? Provider { get; set; }
    public string? Status { get; set; }
    public bool Active { get; set; }
    public bool Premium { get; set; }
    public bool PremiumAllowed { get; set; }
    public bool Unlimited { get; set; }
    public string? Tier { get; set; }
    public ColituFreeQuota? FreeQuota { get; set; }
    public long DailyLimitBytes { get; set; }
    public int DeviceLimit { get; set; } = 1;
    public int DevicesUsed { get; set; }
    public string? ExpiresAt { get; set; }
    public long? TrafficLimitBytes { get; set; }
    public long TrafficUsedBytes { get; set; }
    public long? TrafficRemainingBytes { get; set; }
}

public sealed class ColituFreeQuota
{
    public long LimitBytes { get; set; }
    public long RemainingBytes { get; set; }
}

public sealed class ColituDevice
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public bool Current { get; set; }
    public string? Platform { get; set; }
    public string? LastActiveAt { get; set; }
}

public sealed class ColituApiException(HttpStatusCode statusCode, string message, string? errorCode = null) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string? ErrorCode { get; } = errorCode;
    public int? RetryAfterSeconds { get; }

    /// <summary>True when the API marked the failure unrecoverable (re-login required).</summary>
    public bool Terminal { get; }

    public ColituApiException(HttpStatusCode statusCode, string message, string? errorCode, int? retryAfterSeconds, bool terminal = false) : this(statusCode, message, errorCode)
    {
        RetryAfterSeconds = retryAfterSeconds;
        Terminal = terminal;
    }
}

internal sealed class ColituMeResponse
{
    public ColituUser? User { get; set; }
    public ColituSubscription? Subscription { get; set; }
}

public enum ColituStartupState
{
    SignedOut,
    SignedIn,
    /// <summary>Signed in, but the panel could not be reached; cached account details are shown.</summary>
    Offline,
    /// <summary>Signed in, but the e-mail address must be confirmed before this computer can be registered.</summary>
    VerificationRequired
}

internal sealed class ColituSession
{
    public ColituUser? CachedUser { get; set; }
    public ColituSubscription? CachedSubscription { get; set; }
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public string? RegisteredDeviceId { get; set; }
    public string? RegisteredAppVersion { get; set; }
    public string? ApiBaseUrl { get; set; }
    public string? PendingEmail { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

// ── Panel wire formats (snake_case) ─────────────────────────────────────────

internal sealed class ColituTokenDto
{
    [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
    [JsonPropertyName("expires_in")] public long ExpiresIn { get; set; }
}

internal sealed class ColituMeDto
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
}

internal sealed class ColituDeviceDto
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("platform")] public string? Platform { get; set; }
    [JsonPropertyName("app_version")] public string? AppVersion { get; set; }
    [JsonPropertyName("created_at")] public string? CreatedAt { get; set; }
    [JsonPropertyName("last_seen_at")] public string? LastSeenAt { get; set; }
    [JsonPropertyName("revoked_at")] public string? RevokedAt { get; set; }
}

internal sealed class ColituDeviceListDto
{
    [JsonPropertyName("data")] public List<ColituDeviceDto>? Data { get; set; }
}

internal sealed class ColituEntitlementDto
{
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("plan")] public string? Plan { get; set; }
    [JsonPropertyName("expires_at")] public string? ExpiresAt { get; set; }
    [JsonPropertyName("device_limit")] public int? DeviceLimit { get; set; }
    [JsonPropertyName("traffic")] public ColituTrafficDto? Traffic { get; set; }
    [JsonPropertyName("devices")] public ColituDeviceCountDto? Devices { get; set; }
}

internal sealed class ColituTrafficDto
{
    [JsonPropertyName("upload_bytes")] public long UploadBytes { get; set; }
    [JsonPropertyName("download_bytes")] public long DownloadBytes { get; set; }
    [JsonPropertyName("used_bytes")] public long UsedBytes { get; set; }
    [JsonPropertyName("limit_bytes")] public long? LimitBytes { get; set; }
    [JsonPropertyName("remaining_bytes")] public long? RemainingBytes { get; set; }
    [JsonPropertyName("period_start")] public string? PeriodStart { get; set; }
    [JsonPropertyName("period_end")] public string? PeriodEnd { get; set; }
}

internal sealed class ColituDeviceCountDto
{
    [JsonPropertyName("used")] public int Used { get; set; }
    [JsonPropertyName("limit")] public int? Limit { get; set; }
}
