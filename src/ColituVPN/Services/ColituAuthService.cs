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
    /// <summary>Two-factor authentication is set up here (never in the apps).</summary>
    public const string SecuritySettingsUrl = "https://colitu.com/account/security";
    public const string PricingUrl = "https://colitu.com/pricing";

    /// <summary>Config formats and transports this build can run: Xray for TCP transports, sing-box for Hysteria2.</summary>
    private static readonly string[] SupportedConfigFormats = ["xray-mobile-v1"];
    private static readonly string[] SupportedProtocols = ["hysteria2", "vless-reality", "vless-xhttp", "trojan", "shadowsocks"];

    // Direct to the panel: while the tunnel restarts the system proxy still points at the stopped core.
    private HttpClient _httpClient = CreateHttpClient(TimeSpan.FromSeconds(20));
    // Support attachments (up to 5 x 10 MB) need more than 20 s on a slow uplink.
    private HttpClient _transferClient = CreateHttpClient(TimeSpan.FromMinutes(10));
    private readonly object _persistLock = new();
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly SemaphoreSlim _deviceLock = new(1, 1);
    private ColituSession _session = new();
    private string? _accessToken;
    private string? _refreshToken;
    private DateTimeOffset? _accessTokenExpiresAt;

    /// <summary>
    /// Bumped by every sign-out. A request or token refresh that started under an older
    /// session must neither save tokens (the session would come back after sign-out) nor
    /// expire the session that replaced it (a new sign-in would be wiped).
    /// </summary>
    private long _sessionGeneration;

    /// <summary>
    /// Raised when the session can no longer be refreshed (revoked, device removed,
    /// account disabled). The UI must send the user back to the sign-in screen.
    /// </summary>
    public event Action<string?>? SessionExpired;

    private ColituAuthService()
    {
        _apiOverride = ResolveApiBaseOverride();
    }

    /// <summary>A developer/staging base (config file or debug environment): used alone, no failover and no list refresh.</summary>
    private readonly string? _apiOverride;

    /// <summary>The API base the next request starts with (the one that last worked, from the signed list or the built-in bases).</summary>
    public string ApiBaseUrl => _apiOverride ?? ColituEndpointList.Instance.Bases()[0];

    /// <summary>Hosts the app talks to: they must stay reachable under the kill switch, failover targets included.</summary>
    public IEnumerable<string> PinnedUrls()
    {
        if (_apiOverride != null)
        {
            return [_apiOverride, WebBaseUrl];
        }
        return ColituEndpointList.Instance.PinnedUrls().Append(WebBaseUrl);
    }

    private bool IsKnownSessionBase(string? baseUrl)
    {
        if (_apiOverride != null)
        {
            return string.Equals(baseUrl, _apiOverride, StringComparison.OrdinalIgnoreCase);
        }
        return baseUrl != null && ColituEndpointList.Instance.KnownBases()
            .Contains(baseUrl.TrimEnd('/'), StringComparer.OrdinalIgnoreCase);
    }
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
        // Signed endpoint list: refreshed in the background, never delaying startup.
        if (_apiOverride == null)
        {
            ColituEndpointList.Instance.StartBackgroundRefresh();
        }
        LoadSession();
        if (string.IsNullOrWhiteSpace(_refreshToken))
        {
            return ColituStartupState.SignedOut;
        }

        // Sessions created against the retired API cannot be refreshed by the panel.
        if (!IsKnownSessionBase(_session.ApiBaseUrl))
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

        var generation = Interlocked.Read(ref _sessionGeneration);
        try
        {
            if (IsAccessTokenExpiring())
            {
                var outcome = await RefreshSingleFlightAsync(_accessToken);
                if (generation != Interlocked.Read(ref _sessionGeneration))
                {
                    // Signed out (or into another account) meanwhile: nothing to resume.
                    return HasSession ? ColituStartupState.Offline : ColituStartupState.SignedOut;
                }
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
        catch (ColituApiException ex) when (ex.ErrorCode is "DEVICE_OVER_LIMIT")
        {
            return ColituStartupState.DevicePaused;
        }
        catch (ColituApiException ex) when (ex.ErrorCode is "SIGNED_OUT" || generation != Interlocked.Read(ref _sessionGeneration))
        {
            // The session was replaced while this ran; leave the new one alone.
            return HasSession ? ColituStartupState.Offline : ColituStartupState.SignedOut;
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

    /// <summary>Tells the panel this build can show the two-factor code step (contract: X-Colitu-Features).</summary>
    public const string FeaturesHeader = "X-Colitu-Features";
    public const string FeaturesValue = "mfa";

    public async Task<ColituAuthResult> LoginAsync(string email, string password)
    {
        return await AuthenticateAsync("/auth/login", new { email = email.Trim(), password }, email.Trim(), sendCode: true, mfaCapable: true);
    }

    /// <summary>
    /// Second step of a sign-in with two-factor authentication: the 6-digit code from the
    /// authenticator app, or a recovery code. Success answers like a password sign-in.
    /// </summary>
    public async Task<ColituAuthResult> LoginMfaAsync(string mfaToken, string email, string code, bool recoveryCode)
    {
        var normalized = ColituMfa.NormalizeCode(code, recoveryCode);
        if (normalized == null)
        {
            return ColituAuthResult.Fail(Loc.I[recoveryCode ? "mfa.err.recoveryFormat" : "mfa.err.format"], "MFA_INVALID_CODE");
        }
        return await AuthenticateAsync("/auth/login/mfa", new { mfa_token = mfaToken, code = normalized }, email, sendCode: true, mfaCapable: true);
    }

    /// <summary>This device was paused by the plan's device limit (403 DEVICE_OVER_LIMIT); null otherwise.</summary>
    public ColituDeviceOverLimit? DevicePaused { get; internal set; }

    /// <summary>Activates another paused device of the account (from the device list).</summary>
    public async Task ActivateDeviceAsync(string id)
    {
        using var response = await SendAuthorizedAsync(() => JsonRequest(HttpMethod.Post, $"/devices/{Uri.EscapeDataString(id)}/activate", new { }));
        await EnsureSuccessAsync(response);
    }

    /// <summary>
    /// "Use this device instead": makes this computer the account's active device (another one is
    /// paused in its place), then reloads the account.
    /// </summary>
    public async Task ActivateThisDeviceAsync()
    {
        var id = RegisteredDeviceId;
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ColituApiException(HttpStatusCode.Conflict, Loc.I["paused.noDeviceId"], "DEVICE_NOT_REGISTERED");
        }
        using (var response = await SendAuthorizedAsync(() => JsonRequest(HttpMethod.Post, $"/devices/{Uri.EscapeDataString(id)}/activate", new { })))
        {
            await EnsureSuccessAsync(response);
        }
        DevicePaused = null;
        await LoadMeAsync();
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
        return await AuthenticateAsync("/auth/password/reset", new { email = email.Trim(), code = code.Trim(), password }, email.Trim(), sendCode: true, mfaCapable: true);
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
                    // Paused by the plan's device limit: still signed in, but no VPN until activated.
                    Paused = device.SuspendedAt != null,
                    PausedReason = device.SuspendedReason,
                    LastActiveAt = device.LastSeenAt ?? device.CreatedAt
                })
                .ToList()
        };
    }

    private static HttpClient CreateHttpClient(TimeSpan timeout) => new(new SocketsHttpHandler
    {
        UseProxy = false,
        // The API never redirects; a redirect would resend a password or refresh token
        // (307/308 keep the body) and the device id to another address.
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(8),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        // The kill switch blocks the system DNS lookup: dial the API's pinned addresses.
        ConnectCallback = ColituPinnedHosts.ConnectAsync
    })
    {
        Timeout = timeout,
        // Responses are read into memory: a broken or hostile answer can't grow without bound.
        MaxResponseContentBufferSize = 64L << 20
    };

    /// <summary>
    /// The tunnel just came up or went down. Keep-alive connections opened over the old route
    /// (through the TUN adapter, or around it) are dead now, and reusing one hangs until the
    /// 20 s timeout: start the next requests on fresh connections.
    /// </summary>
    public void ResetConnections()
    {
        var old = Interlocked.Exchange(ref _httpClient, CreateHttpClient(TimeSpan.FromSeconds(20)));
        // Requests already in flight keep the old client; dispose it once they are done.
        _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ => old.Dispose(), TaskScheduler.Default);
        var oldTransfer = Interlocked.Exchange(ref _transferClient, CreateHttpClient(TimeSpan.FromMinutes(10)));
        _ = Task.Delay(TimeSpan.FromMinutes(11)).ContinueWith(_ => oldTransfer.Dispose(), TaskScheduler.Default);
    }

    public async Task<T?> GetAuthorizedJsonAsync<T>(string path, CancellationToken token = default)
    {
        using var response = await SendAuthorizedAsync(() => new HttpRequestMessage(HttpMethod.Get, BuildUri(path)), token: token);
        await EnsureSuccessAsync(response);
        return await ReadJsonAsync<T>(response);
    }

    public async Task<T?> PostAuthorizedJsonAsync<T>(string path, object body)
    {
        using var response = await SendAuthorizedAsync(() => JsonRequest(HttpMethod.Post, path, body));
        await EnsureSuccessAsync(response);
        return await ReadJsonAsync<T>(response);
    }

    public async Task<T?> PutAuthorizedJsonAsync<T>(string path, object body, CancellationToken token = default)
    {
        using var response = await SendAuthorizedAsync(() => JsonRequest(HttpMethod.Put, path, body), token: token);
        await EnsureSuccessAsync(response);
        return await ReadJsonAsync<T>(response);
    }

    /// <summary>Sends any request with the session's token (refreshing it once on 401) and throws on failure.</summary>
    public async Task<HttpResponseMessage> SendAuthorizedRequestAsync(Func<HttpRequestMessage> requestFactory, bool transfer = false)
    {
        var response = await SendAuthorizedAsync(requestFactory, transfer: transfer);
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

    private async Task<ColituAuthResult> AuthenticateAsync(string path, object body, string email, bool sendCode, bool mfaCapable = false)
    {
        try
        {
            using var response = await SendClientJsonAsync(HttpMethod.Post, path, body, mfaCapable: mfaCapable);
            if (mfaCapable && response.StatusCode == HttpStatusCode.Forbidden
                && ColituMfa.ParseChallenge(await response.Content.ReadAsStringAsync()) is { } challenge)
            {
                // The password was right; the account wants the code from the authenticator app.
                return ColituAuthResult.Mfa(email, challenge.Token, challenge.ExpiresIn, challenge.Method);
            }
            await EnsureSuccessAsync(response);
            var tokens = await ReadJsonAsync<ColituTokenDto>(response);
            if (string.IsNullOrWhiteSpace(tokens?.AccessToken) || string.IsNullOrWhiteSpace(tokens.RefreshToken))
            {
                return ColituAuthResult.Fail(Loc.I["err.generic"]);
            }

            // A new sign-in always re-registers this installation for the signed-in account.
            // Requests and refreshes still running for the previous session must not touch it.
            Interlocked.Increment(ref _sessionGeneration);
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
            catch (ColituApiException ex) when (ex.ErrorCode is "DEVICE_OVER_LIMIT")
            {
                // Signed in, but the plan's device limit pauses this computer: keep the session so
                // the paused screen can offer "Use this device instead".
                _session.PendingEmail = null;
                PersistSession();
                return ColituAuthResult.Paused(email, ex.OverLimit);
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
            return ColituAuthResult.Fail(UserMessage(ex), (ex as ColituApiException)?.ErrorCode);
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
            var generation = Interlocked.Read(ref _sessionGeneration);
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
            ThrowIfSessionChanged(generation);

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
        var generation = Interlocked.Read(ref _sessionGeneration);
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

        // Signed out while the account was loading: don't show (or save) it again.
        ThrowIfSessionChanged(generation);
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
        var trial = ColituTrial.Parse(entitlement?.Extra);
        return new ColituSubscription
        {
            EndsAt = trial.EndsAt ?? (status == "trialing" ? entitlement?.ExpiresAt : null),
            NextPlan = trial.NextPlan,
            NextPlanTrafficBytes = trial.NextPlanTrafficBytes,
            NextDeviceLimit = trial.NextDeviceLimit,
            DeviceCount = entitlement?.Devices?.Registered ?? trial.DeviceCount ?? entitlement?.Devices?.Used,
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
            DevicesUsed = entitlement?.Devices?.Active ?? entitlement?.Devices?.Used ?? 0,
            ExpiresAt = entitlement?.ExpiresAt,
            TrafficLimitBytes = limitBytes,
            TrafficUsedBytes = entitlement?.Traffic?.UsedBytes ?? 0,
            TrafficRemainingBytes = entitlement?.Traffic?.RemainingBytes
        };
    }

    private async Task<HttpResponseMessage> SendAuthorizedAsync(Func<HttpRequestMessage> requestFactory, bool allowRefresh = true, bool includeDevice = true, CancellationToken token = default, bool transfer = false)
    {
        var http = transfer ? _transferClient : _httpClient;
        // Refresh proactively just before the access token expires so that
        // parallel requests do not all hit 401 and race each other to refresh.
        if (allowRefresh && IsAccessTokenExpiring() && !string.IsNullOrWhiteSpace(_refreshToken))
        {
            // The caller's deadline covers the refresh too (an automatic reconnect waits at most
            // 3 s for the panel); the shared refresh itself keeps running for the other callers.
            await RefreshSingleFlightAsync(_accessToken).WaitAsync(token);
        }

        var generation = Interlocked.Read(ref _sessionGeneration);
        if (string.IsNullOrWhiteSpace(_accessToken) && string.IsNullOrWhiteSpace(_refreshToken))
        {
            // Signed out (a poll that was already running): don't send an anonymous request
            // whose 401 would end in "session expired".
            throw new ColituApiException(HttpStatusCode.Unauthorized, Loc.I["auth.expired"], "SIGNED_OUT");
        }

        var tokenUsed = _accessToken;
        var response = await SendApiAsync(http, () => PrepareAuthorized(requestFactory(), tokenUsed, includeDevice), token);
        if (response.StatusCode != HttpStatusCode.Unauthorized || !allowRefresh)
        {
            return response;
        }

        response.Dispose();
        var outcome = await RefreshSingleFlightAsync(tokenUsed).WaitAsync(token);
        if (generation != Interlocked.Read(ref _sessionGeneration))
        {
            throw new ColituApiException(HttpStatusCode.Unauthorized, Loc.I["auth.expired"], "SIGNED_OUT");
        }
        if (outcome == ColituRefreshOutcome.Terminal)
        {
            ExpireSession("SESSION_EXPIRED");
            throw SessionExpiredException();
        }
        if (outcome == ColituRefreshOutcome.Transient)
        {
            throw new ColituApiException(
                HttpStatusCode.Unauthorized,
                Loc.I["err.refresh"],
                "REFRESH_FAILED");
        }

        var refreshedToken = _accessToken;
        return await SendApiAsync(http, () => PrepareAuthorized(requestFactory(), refreshedToken, includeDevice), token);
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
        var generation = Interlocked.Read(ref _sessionGeneration);
        try
        {
            using var response = await SendClientJsonAsync(HttpMethod.Post, "/auth/refresh", new { refresh_token = _refreshToken }, includeDevice: true);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                // Revoked or reused refresh tokens cannot recover.
                return ColituRefreshOutcome.Terminal;
            }
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            {
                // A removed device can't recover either, but a 403/404 page from a proxy, CDN
                // or captive portal must not sign the user out.
                return await ErrorCodeAsync(response) is "DEVICE_REVOKED" or "DEVICE_NOT_FOUND" or "DEVICE_TOKEN_MISMATCH" or "AUTH_REFRESH_REUSED"
                    ? ColituRefreshOutcome.Terminal
                    : ColituRefreshOutcome.Transient;
            }
            if (!response.IsSuccessStatusCode)
            {
                // Rate limits and server hiccups are retryable; keep the session.
                return ColituRefreshOutcome.Transient;
            }
            var tokens = await ReadJsonAsync<ColituTokenDto>(response);
            if (string.IsNullOrWhiteSpace(tokens?.AccessToken)) return ColituRefreshOutcome.Transient;
            // Signed out while the refresh was in flight: don't bring the session back.
            if (generation != Interlocked.Read(ref _sessionGeneration)) return ColituRefreshOutcome.Transient;
            SaveTokens(tokens.AccessToken, tokens.RefreshToken ?? _refreshToken!);
            return ColituRefreshOutcome.Success;
        }
        catch
        {
            return ColituRefreshOutcome.Transient;
        }
    }

    private void ThrowIfSessionChanged(long generation)
    {
        if (generation != Interlocked.Read(ref _sessionGeneration))
        {
            throw new ColituApiException(HttpStatusCode.Unauthorized, Loc.I["auth.expired"], "SIGNED_OUT");
        }
    }

    /// <summary>
    /// Drops the saved session after the panel ended it with a non-401 answer (403
    /// DEVICE_REVOKED on a regular request): the tokens must not stay on disk.
    /// </summary>
    public void DiscardSession()
    {
        if (HasSession || PendingVerification)
        {
            ClearSession();
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

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.String
                ? code.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<HttpResponseMessage> SendClientJsonAsync(HttpMethod method, string path, object body, bool includeDevice = false, bool mfaCapable = false)
    {
        return await SendApiAsync(_httpClient, () =>
        {
            var request = JsonRequest(method, path, body);
            AddClientHeaders(request, includeDevice);
            if (mfaCapable)
            {
                request.Headers.TryAddWithoutValidation(FeaturesHeader, FeaturesValue);
            }
            return request;
        }, CancellationToken.None);
    }

    /// <summary>
    /// Sends an API request. With the signed endpoint list it goes to the base that last worked and
    /// moves to the next one only after a network-level failure; any HTTP response ends the loop.
    /// A developer override base is used alone.
    /// </summary>
    private async Task<HttpResponseMessage> SendApiAsync(HttpClient http, Func<HttpRequestMessage> requestFactory, CancellationToken token)
    {
        if (_apiOverride != null)
        {
            return await http.SendAsync(requestFactory(), token);
        }
        var list = ColituEndpointList.Instance;
        var known = list.KnownBases();
        var isGet = true;
        return await ColituEndpointList.SendWithFailoverAsync(
            list.Bases(),
            baseUrl =>
            {
                var request = requestFactory();
                isGet = request.Method == HttpMethod.Get;
                if (request.RequestUri != null)
                {
                    request.RequestUri = ColituEndpointList.Rebase(request.RequestUri, known, baseUrl);
                }
                return http.SendAsync(request, token);
            },
            ex => ColituEndpointList.ShouldFailover(ex, isGet),
            list.RememberWorking,
            token);
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
        // The stable base, not whichever mirror answered: a failover must not sign the user out.
        _session.ApiBaseUrl = _apiOverride ?? DefaultApiBaseUrl;
        PersistSession();
    }

    private void PersistSession()
    {
        if (string.IsNullOrWhiteSpace(_session.RefreshToken))
        {
            return;
        }
        // The watchdog (thread pool) and the UI can save at the same time; a lost write could
        // leave an already rotated refresh token on disk, which the panel treats as reuse.
        lock (_persistLock)
        {
            _session.UpdatedAt = DateTimeOffset.UtcNow;
            var path = SessionPath();
            var temp = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                // Write-then-rename so a crash or power loss never leaves a torn session file.
                File.WriteAllText(temp, JsonSerializer.Serialize(_session, _jsonOptions));
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex)
            {
                Logging.SaveLog("ColituAuthService.PersistSession", ex);
                try { File.Delete(temp); } catch { }
            }
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
        Interlocked.Increment(ref _sessionGeneration);
        _session = new();
        _accessToken = null;
        _refreshToken = null;
        _accessTokenExpiresAt = null;
        PendingVerification = false;
        CurrentUser = null;
        CurrentSubscription = null;
        DevicePaused = null;
        lock (_persistLock)
        {
            for (var attempt = 0; attempt < 3 && File.Exists(SessionPath()); attempt++)
            {
                try
                {
                    File.Delete(SessionPath());
                }
                catch
                {
                    // Locked (an antivirus scan): try again, then overwrite the tokens instead.
                    Thread.Sleep(100);
                }
            }
            try
            {
                if (File.Exists(SessionPath())) File.WriteAllText(SessionPath(), "{}");
            }
            catch
            {
                // Nothing more to do.
            }
        }
        // Cached connection settings carry this account's server credentials.
        ColituVpnService.DeleteConfigCache();
        ColituSupportService.DeleteDownloads();
    }

    /// <summary>The developer/staging override of the API base, null in normal use.</summary>
    private static string? ResolveApiBaseOverride()
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

        return null;
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
                    code = error.TryGetProperty("code", out var codeElement) && codeElement.ValueKind == JsonValueKind.String ? codeElement.GetString() : null;
                    message = error.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String ? messageElement.GetString() : null;
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
        ColituDeviceOverLimit? overLimit = null;
        var attemptsLeft = code == "MFA_INVALID_CODE" ? ColituMfa.AttemptsLeft(body) : null;
        if (code == "DEVICE_OVER_LIMIT")
        {
            overLimit = ColituDeviceOverLimit.Parse(body) ?? new ColituDeviceOverLimit();
            Instance.DevicePaused = overLimit;
        }
        var friendly = FriendlyMessage(code, message, response.StatusCode);
        if (attemptsLeft is { } left)
        {
            friendly += " " + Loc.I.Format("mfa.attemptsLeft", ("n", left));
        }
        throw new ColituApiException(response.StatusCode, friendly, code, retryAfter, terminal)
        {
            OverLimit = overLimit
        };
    }

    internal static string FriendlyMessage(string? code, string? message, HttpStatusCode status)
    {
        var loc = Loc.I;
        return code switch
        {
            "AUTH_INVALID_CREDENTIALS" => loc["err.credentials"],
            "INVALID_REGISTRATION" => loc["err.registration"],
            "DISPOSABLE_EMAIL" => loc["err.disposableEmail"],
            "PASSWORD_BREACHED" => loc["err.passwordBreached"],
            "SIGNUP_IP_LIMIT" => loc["err.signupIpLimit"],
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
            "MFA_INVALID_CODE" => loc["mfa.err.invalid"],
            "MFA_TOKEN_EXPIRED" => loc["mfa.err.expired"],
            "MFA_REQUIRED_UPDATE_APP" or "MFA_REQUIRED" => loc["mfa.err.update"],
            "DEVICE_OVER_LIMIT" => loc["paused.short"],
            "DEVICE_REVOKED" or "DEVICE_TOKEN_MISMATCH" or "AUTH_REFRESH_REUSED" or "AUTH_TOKEN_EXPIRED" => loc["auth.expired"],
            "NO_HEALTHY_NODES" or "CONFIG_NOT_AVAILABLE" or "INVALID_PREFERENCE" => loc["err.noServers"],
            _ when (int)status >= 500 => loc["err.network"],
            // The panel's own text is English only: shown only for codes the app does not know yet.
            _ when !string.IsNullOrWhiteSpace(message) => char.ToUpperInvariant(message[0]) + message[1..] + (message.EndsWith('.') ? "" : "."),
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
        // "2.5.5+commit" or "2.5.5-beta": a suffix used to fail the parse and report 2.1.0, which
        // the updater then took for a very old version.
        var version = (informational ?? "").Split('+', '-')[0].Trim();
        if (System.Version.TryParse(version, out var parsed))
        {
            return parsed.ToString(3);
        }
        return assembly.GetName().Version is { } assemblyVersion ? assemblyVersion.ToString(3) : "2.1.0";
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
    /// <summary>The panel's error code of a failure (MFA_INVALID_CODE, MFA_TOKEN_EXPIRED, ...).</summary>
    public string? ErrorCode { get; init; }
    /// <summary>Password accepted; the two-factor code step comes next with this token.</summary>
    public string? MfaToken { get; init; }
    public int MfaExpiresIn { get; init; }
    /// <summary>"totp" (authenticator app) or "email" (code mailed after an unfamiliar-country sign-in).</summary>
    public string MfaMethod { get; init; } = ColituMfa.MethodTotp;
    public bool MfaByEmail => string.Equals(MfaMethod, ColituMfa.MethodEmail, StringComparison.Ordinal);
    public bool RequiresMfa => MfaToken != null;
    /// <summary>Signed in, but the plan's device limit pauses this computer.</summary>
    public ColituDeviceOverLimit? DevicePaused { get; init; }
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
    public static ColituAuthResult Fail(string error, string? code = null) => new() { Success = false, Error = error, ErrorCode = code };
    public static ColituAuthResult Mfa(string email, string token, int expiresIn, string method = ColituMfa.MethodTotp) => new() { Success = false, MfaToken = token, MfaExpiresIn = expiresIn, MfaMethod = method, Message = email };
    public static ColituAuthResult Paused(string email, ColituDeviceOverLimit? info) => new() { Success = true, DevicePaused = info ?? new ColituDeviceOverLimit(), Message = email };
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
    /// <summary>When the trial (or the current plan period) ends.</summary>
    public string? EndsAt { get; set; }
    /// <summary>The plan the account moves to then ("free" after the trial).</summary>
    public string? NextPlan { get; set; }
    public long? NextPlanTrafficBytes { get; set; }
    public int? NextDeviceLimit { get; set; }
    /// <summary>Devices on the account.</summary>
    public int? DeviceCount { get; set; }
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
    public bool Paused { get; set; }
    public string? PausedReason { get; set; }
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

    /// <summary>Details of a 403 DEVICE_OVER_LIMIT (the device limit and the devices still active).</summary>
    public ColituDeviceOverLimit? OverLimit { get; init; }
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
    VerificationRequired,
    /// <summary>Signed in, but the plan's device limit pauses this computer (403 DEVICE_OVER_LIMIT).</summary>
    DevicePaused
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
    [JsonPropertyName("suspended_at")] public string? SuspendedAt { get; set; }
    [JsonPropertyName("suspended_reason")] public string? SuspendedReason { get; set; }
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
    /// <summary>Fields read tolerantly (ends_at, next_plan, next_device_limit, device_count): see ColituTrial.Parse.</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
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
    [JsonPropertyName("active")] public int? Active { get; set; }
    [JsonPropertyName("suspended")] public int? Suspended { get; set; }
    [JsonPropertyName("registered")] public int? Registered { get; set; }
}
