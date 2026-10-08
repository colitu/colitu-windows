using System.Net.Http;
using System.Net.Http.Json;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;

namespace v2rayN.Services;

public sealed class ColituUpdateService
{
    public static ColituUpdateService Instance { get; } = new();

    private static readonly int LocalVersionCode = ColituAuthService.VersionCode(ColituAuthService.ClientVersion);

    /// <summary>Folder under the install directory that holds a downloaded update (administrators only).</summary>
    public const string UpdateFolderName = "guiUpdates";

    /// <summary>
    /// Release manifest published next to the installer on the Colitu website
    /// (see docs/release.md). Debug builds can point it at a staging manifest
    /// with COLITU_UPDATE_MANIFEST_URL; release builds ignore the variable, since
    /// anything that can set a user environment variable could otherwise feed
    /// this elevated app an installer to run.
    /// </summary>
    private static string ManifestUrl =>
#if DEBUG
        Environment.GetEnvironmentVariable("COLITU_UPDATE_MANIFEST_URL")?.Trim().NullIfEmpty() ??
#endif
        $"{ColituAuthService.WebBaseUrl}/downloads/windows/latest.json";
    private static readonly string LocalVersionName = ColituAuthService.ClientVersion;
    private static readonly TimeSpan AttemptCooldown = TimeSpan.FromMinutes(30);

    private readonly HttpClient _httpClient = new(new SocketsHttpHandler { UseProxy = false, ConnectCallback = ColituPinnedHosts.ConnectAsync }) { Timeout = TimeSpan.FromSeconds(30) };
    // HttpClient.Timeout also cancels content streaming, so large packages need their own client
    // with a generous limit; otherwise slow connections abort the download after 30 seconds.
    private readonly HttpClient _downloadClient = new(new SocketsHttpHandler { UseProxy = false, ConnectCallback = ColituPinnedHosts.ConnectAsync }) { Timeout = TimeSpan.FromMinutes(30) };
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public event Action<ColituUpdateInfo>? UpdateAvailable;

    /// <summary>The installer is about 100 MB; anything far larger is not a Colitu release.</summary>
    private const long MaxPackageBytes = 512L << 20;

    /// <summary>
    /// True when the last <see cref="CheckForUpdateAsync"/> could not tell (offline, server error,
    /// bad signature), so "you have the latest version" would be a guess.
    /// </summary>
    public bool LastCheckFailed { get; private set; }

    private ColituUpdateService() { }

    public async Task<ColituUpdateInfo?> CheckForUpdateAsync(bool ignoreAttemptCache = false)
    {
        LastCheckFailed = true;
        try
        {
            ClearCompletedAttempt();
            using var request = new HttpRequestMessage(HttpMethod.Get, ManifestUrl);
            request.Headers.TryAddWithoutValidation("X-Client-Platform", ColituAuthService.ClientPlatform);
            request.Headers.TryAddWithoutValidation("X-App-Version", LocalVersionName);
            request.Headers.TryAddWithoutValidation("User-Agent", $"ColituVPN/{LocalVersionName} Windows");
            using var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;
            var payload = await response.Content.ReadFromJsonAsync<ColituVersionPayload>(_jsonOptions);
            if (payload == null) return null;
            if (!ColituUpdateSignature.Verify(payload))
            {
                Logging.SaveLog("ColituUpdateService: release manifest signature is missing or invalid; update ignored");
                return null;
            }
            LastCheckFailed = false;

            var remoteCode = payload.LatestVersionCode;
            var remoteName = payload.VersionName ?? remoteCode.ToString();
            if (!IsRemoteVersionNewer(remoteName, remoteCode)) return null;
            if (!ignoreAttemptCache && WasRecentlyAttempted(remoteCode, payload.DownloadUrl, payload.Sha256))
            {
                return null;
            }

            var info = new ColituUpdateInfo
            {
                VersionCode = remoteCode,
                CurrentVersion = LocalVersionName,
                NewVersion = remoteName,
                DownloadUrl = payload.DownloadUrl!,
                Sha256 = payload.Sha256,
                Force = payload.ForceUpdate,
                Notes = payload.ReleaseNotes ?? ""
            };
            ValidateDownloadUrl(info.DownloadUrl);
            UpdateAvailable?.Invoke(info);
            return info;
        }
        catch
        {
            LastCheckFailed = true;
            return null;
        }
    }

    public async Task DownloadUpdateAsync(ColituUpdateInfo info, IProgress<ColituDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await DownloadUpdateOnceAsync(info, progress, cancellationToken);
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts
                && !cancellationToken.IsCancellationRequested
                && ex is HttpRequestException or IOException or TaskCanceledException)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), cancellationToken);
            }
        }
    }

    private async Task DownloadUpdateOnceAsync(ColituUpdateInfo info, IProgress<ColituDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        ValidateDownloadUrl(info.DownloadUrl);
        if (string.IsNullOrWhiteSpace(info.Sha256))
        {
            throw new InvalidOperationException("The release manifest does not carry a SHA256 for the update.");
        }
        // The installer and its helper script run elevated, so they are kept in a
        // fresh folder that only administrators can write, never in %TEMP%.
        var updateDir = ColituHardening.CreateProtectedDirectory(Path.Combine(Utils.StartupPath(), UpdateFolderName));
        var destPath = Path.Combine(updateDir, $"ColituVPN-new-{info.VersionCode}.exe");

        using var response = await _downloadClient.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1;
        if (total > MaxPackageBytes)
        {
            throw new InvalidOperationException("The update package is larger than any Colitu release.");
        }
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var dest = File.Create(destPath))
        {
            var buffer = new byte[65536];
            long downloaded = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                downloaded += read;
                // The size header is not trusted: stop filling the disk past the limit.
                if (downloaded > MaxPackageBytes)
                {
                    throw new InvalidOperationException("The update package is larger than any Colitu release.");
                }
                await dest.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                if (total > 0)
                {
                    progress?.Report(new ColituDownloadProgress
                    {
                        TotalBytes = total,
                        DownloadedBytes = downloaded,
                        Percent = (int)(downloaded * 100 / total)
                    });
                }
            }
        }

        VerifySha256(destPath, info.Sha256);
        ValidateDownloadedVersion(destPath, info.NewVersion);

        info.LocalPath = destPath;
    }

    public void LaunchUpdaterAndExit(ColituUpdateInfo info)
    {
        if (string.IsNullOrWhiteSpace(info.LocalPath) || !File.Exists(info.LocalPath))
        {
            throw new InvalidOperationException("Update file not downloaded.");
        }

        // Assembly.Location is empty in the single-file build.
        var exePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ColituVPN.exe");
        var tempDir = Path.GetDirectoryName(info.LocalPath)!;
        var scriptPath = Path.Combine(tempDir, "apply-update.ps1");
        var logPath = Path.Combine(tempDir, "apply-update.log");
        var currentPid = Environment.ProcessId;
        var packageKind = IsInstallerPackage(info.LocalPath, info.DownloadUrl) ? "installer" : "portable-exe";
        SaveUpdateState(new ColituUpdateState
        {
            AttemptedVersionCode = info.VersionCode,
            AttemptedVersionName = info.NewVersion,
            DownloadUrl = info.DownloadUrl,
            Sha256 = info.Sha256,
            AttemptedAt = DateTimeOffset.Now
        });

        var scriptContent = """
            param(
              [int]$ParentPid,
              [string]$Source,
              [string]$Target,
              [string]$ExpectedSha256,
              [string]$LogPath,
              [string]$PackageKind
            )

            $ErrorActionPreference = "Continue"
            $appProcessName = [System.IO.Path]::GetFileNameWithoutExtension($Target)

            function Write-UpdateLog([string]$Message) {
              try { Add-Content -LiteralPath $LogPath -Value "$(Get-Date -Format o) $Message" -Encoding UTF8 } catch {}
            }

            function Stop-AppProcesses {
              try { Get-Process -Id $ParentPid -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue } catch {}
              try { Get-Process -Name $appProcessName -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue } catch {}
            }

            function Start-App([string]$Path) {
              if (-not $Path -or -not (Test-Path -LiteralPath $Path)) { return $false }
              for ($attempt = 1; $attempt -le 5; $attempt++) {
                try {
                  Start-Process -FilePath $Path -WorkingDirectory (Split-Path -Parent $Path) -ErrorAction Stop | Out-Null
                  Write-UpdateLog "App started: $Path"
                  return $true
                } catch {
                  Write-UpdateLog "App start attempt $attempt failed: $($_.Exception.Message)"
                  Start-Sleep -Seconds 2
                }
              }
              return $false
            }

            function Resolve-InstalledApp {
              $exeName = [System.IO.Path]::GetFileName($Target)
              foreach ($hive in @("HKLM:\SOFTWARE", "HKLM:\SOFTWARE\WOW6432Node")) {
                try {
                  $key = "$hive\Microsoft\Windows\CurrentVersion\Uninstall\{B7B06C4E-1D5A-482F-A781-0F6C6C8A2B67}_is1"
                  $location = (Get-ItemProperty -Path $key -ErrorAction Stop).InstallLocation
                  if ($location) {
                    $exe = Join-Path $location $exeName
                    if (Test-Path -LiteralPath $exe) { return $exe }
                  }
                } catch {}
              }
              if (Test-Path -LiteralPath $Target) { return $Target }
              $fallback = Join-Path ${env:ProgramFiles} "Colitu VPN\ColituVPN.exe"
              if (Test-Path -LiteralPath $fallback) { return $fallback }
              return $null
            }

            function Show-UpdateError([string]$Message) {
              try {
                Add-Type -AssemblyName PresentationFramework
                [System.Windows.MessageBox]::Show("$Message`n`nLog: $LogPath", "Colitu VPN Update", "OK", "Error") | Out-Null
              } catch {}
            }

            Write-UpdateLog "Updater started. parent=$ParentPid source=$Source target=$Target kind=$PackageKind"

            try {
              Wait-Process -Id $ParentPid -Timeout 60 -ErrorAction SilentlyContinue
            } catch {
              Write-UpdateLog "Wait-Process failed: $($_.Exception.Message)"
            }
            Stop-AppProcesses
            Start-Sleep -Milliseconds 500

            if ($ExpectedSha256) {
              $actualSource = (Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash
              if ($actualSource -ne $ExpectedSha256.Trim().ToUpperInvariant()) {
                Write-UpdateLog "Downloaded package SHA256 mismatch before start. expected=$ExpectedSha256 actual=$actualSource"
                Remove-Item -LiteralPath $Source -Force -ErrorAction SilentlyContinue
                Start-App (Resolve-InstalledApp) | Out-Null
                Show-UpdateError "Colitu VPN update was rejected because the downloaded file changed."
                exit 1
              }
            }

            if ($PackageKind -eq "installer") {
              $installed = $false
              for ($attempt = 1; $attempt -le 3; $attempt++) {
                try {
                  Write-UpdateLog "Starting installer (attempt $attempt)."
                  $setupArgs = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS"
                  $setup = Start-Process -FilePath $Source -ArgumentList $setupArgs -Wait -PassThru -ErrorAction Stop
                  if ($setup.ExitCode -eq 0) { $installed = $true; break }
                  Write-UpdateLog "Installer exited with code $($setup.ExitCode)."
                } catch {
                  Write-UpdateLog "Installer attempt $attempt failed: $($_.Exception.Message)"
                }
                Stop-AppProcesses
                Start-Sleep -Seconds 3
              }

              $appPath = Resolve-InstalledApp
              if ($installed) {
                Write-UpdateLog "Installer finished. Restarting app: $appPath"
                if (Start-App $appPath) {
                  Remove-Item -LiteralPath $Source -Force -ErrorAction SilentlyContinue
                  Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
                  exit 0
                }
                Show-UpdateError "Colitu VPN was updated but could not be reopened automatically. Please start it manually."
                exit 1
              }

              Write-UpdateLog "Installer failed after all attempts. Restarting previous version."
              Start-App $appPath | Out-Null
              Show-UpdateError "Colitu VPN update could not be completed. Please run the downloaded setup manually."
              exit 1
            }

            for ($i = 1; $i -le 40; $i++) {
              try {
                Copy-Item -LiteralPath $Source -Destination $Target -Force -ErrorAction Stop

                if ($ExpectedSha256) {
                  $actual = (Get-FileHash -LiteralPath $Target -Algorithm SHA256).Hash
                  if ($actual -ne $ExpectedSha256.Trim().ToUpperInvariant()) {
                    throw "Copied file SHA256 mismatch. expected=$ExpectedSha256 actual=$actual"
                  }
                }

                Write-UpdateLog "Copy succeeded. Starting updated app."
                if (-not (Start-App $Target)) {
                  throw "Updated app could not be started."
                }
                Remove-Item -LiteralPath $Source -Force -ErrorAction SilentlyContinue
                Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
                exit 0
              } catch {
                Write-UpdateLog "Copy attempt $i failed: $($_.Exception.Message)"
                Stop-AppProcesses
                Start-Sleep -Seconds 1
              }
            }

            Write-UpdateLog "Update failed after all retries. Restarting previous version."
            Start-App $Target | Out-Null
            Show-UpdateError "Colitu VPN update could not be applied. Please restart the app and try again."
            exit 1
            """;

        File.WriteAllText(scriptPath, scriptContent, new UTF8Encoding(false));
        try { File.Delete(logPath); } catch { }

        // By full path: the app runs elevated, and a bare name is also looked up in the
        // current directory, which may be user-writable.
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var startInfo = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NoLogo");
        // Default client machines ship with a Restricted execution policy that silently
        // refuses "-File" scripts; without Bypass the app exits and never updates or reopens.
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("-ParentPid");
        startInfo.ArgumentList.Add(currentPid.ToString());
        startInfo.ArgumentList.Add("-Source");
        startInfo.ArgumentList.Add(info.LocalPath);
        startInfo.ArgumentList.Add("-Target");
        startInfo.ArgumentList.Add(exePath);
        startInfo.ArgumentList.Add("-ExpectedSha256");
        startInfo.ArgumentList.Add(info.Sha256 ?? "");
        startInfo.ArgumentList.Add("-LogPath");
        startInfo.ArgumentList.Add(logPath);
        startInfo.ArgumentList.Add("-PackageKind");
        startInfo.ArgumentList.Add(packageKind);

        var updater = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Update helper could not be started.");
        // The helper waits up to 60s for this process to exit, so an immediate exit means
        // it crashed before doing anything; keep the app open and surface the failure.
        if (updater.WaitForExit(1000))
        {
            throw new InvalidOperationException($"Update helper exited early (code {updater.ExitCode}). See log: {logPath}");
        }

        Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
    }

    private static void ValidateDownloadedVersion(string filePath, string expectedVersion)
    {
        if (string.IsNullOrWhiteSpace(expectedVersion)) return;
        try
        {
            var versionInfo = FileVersionInfo.GetVersionInfo(filePath);
            var actual = FirstNonEmpty(versionInfo.ProductVersion, versionInfo.FileVersion);
            if (string.IsNullOrWhiteSpace(actual)) return;

            if (!SameVersion(actual, expectedVersion))
            {
                File.Delete(filePath);
                throw new InvalidOperationException($"Downloaded file version is {actual}, but API announced {expectedVersion}. Update link probably points to an old build.");
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch
        {
            // Some installers do not expose product version metadata. SHA256 is still authoritative when provided.
        }
    }

    /// <summary>
    /// "2.5.4" matches "2.5.4" and "2.5.4.0" but not "2.5.40" (a prefix test would accept it).
    /// </summary>
    internal static bool SameVersion(string actual, string expected)
    {
        var a = actual.Split('+')[0].Trim();
        var e = expected.Split('+')[0].Trim();
        if (Version.TryParse(a, out var actualVersion) && Version.TryParse(e, out var expectedVersion))
        {
            return actualVersion.Major == expectedVersion.Major
                && actualVersion.Minor == expectedVersion.Minor
                && Math.Max(0, actualVersion.Build) == Math.Max(0, expectedVersion.Build)
                && Math.Max(0, actualVersion.Revision) == Math.Max(0, expectedVersion.Revision);
        }
        return string.Equals(a, e, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInstallerPackage(string filePath, string? downloadUrl)
    {
        var names = Path.GetFileName(filePath);
        if (Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri))
        {
            names += $" {Path.GetFileName(uri.LocalPath)}";
        }

        if (names.Contains("setup", StringComparison.OrdinalIgnoreCase) ||
            names.Contains("installer", StringComparison.OrdinalIgnoreCase) ||
            names.Contains("install", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            var versionInfo = FileVersionInfo.GetVersionInfo(filePath);
            var description = FirstNonEmpty(versionInfo.FileDescription, versionInfo.ProductName, versionInfo.Comments);
            return description.Contains("installer", StringComparison.OrdinalIgnoreCase) ||
                   description.Contains("setup", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void VerifySha256(string filePath, string expectedHex)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = sha.ComputeHash(stream);
        var actual = Convert.ToHexString(hash);
        if (!string.Equals(actual, expectedHex.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(filePath);
            throw new InvalidOperationException("SHA256 verification failed. The downloaded file may be corrupted.");
        }
    }

    private static void ValidateDownloadUrl(string downloadUrl)
    {
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("Update download URL is invalid.");
        }
#if DEBUG
        // Local mock servers during development.
        if (IPAddressIsLoopback(uri.Host))
        {
            return;
        }
#endif
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Update download URL must use HTTPS.");
        }
        if (!TrustedDownloadHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Update download host is not trusted.");
        }
    }

    // Fixed list: the API address can be configured, so it must not widen where installers come from.
    private static readonly string[] TrustedDownloadHosts = ["colitu.com", "www.colitu.com", "api.colitu.com"];

    private static bool IPAddressIsLoopback(string host)
    {
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase);
    }

    private static bool WasRecentlyAttempted(int versionCode, string? downloadUrl, string? sha256)
    {
        var state = LoadUpdateState();
        if (state?.AttemptedVersionCode != versionCode) return false;
        if (!string.Equals(state.DownloadUrl ?? "", downloadUrl ?? "", StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(state.Sha256 ?? "", sha256 ?? "", StringComparison.OrdinalIgnoreCase)) return false;
        return DateTimeOffset.Now - state.AttemptedAt < AttemptCooldown;
    }

    private static void ClearCompletedAttempt()
    {
        var state = LoadUpdateState();
        if (state == null) return;
        if (LocalVersionCode >= state.AttemptedVersionCode || DateTimeOffset.Now - state.AttemptedAt >= AttemptCooldown)
        {
            try { File.Delete(StatePath()); } catch { }
        }
    }

    private static ColituUpdateState? LoadUpdateState()
    {
        try
        {
            return File.Exists(StatePath())
                ? JsonSerializer.Deserialize<ColituUpdateState>(File.ReadAllText(StatePath()))
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void SaveUpdateState(ColituUpdateState state)
    {
        try
        {
            File.WriteAllText(StatePath(), JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
    }

    private static bool IsRemoteVersionNewer(string? remoteVersionName, int remoteVersionCode)
    {
        var comparison = CompareSemanticVersions(remoteVersionName, LocalVersionName);
        if (comparison.HasValue)
        {
            return comparison.Value > 0;
        }

        return remoteVersionCode > LocalVersionCode;
    }

    private static int? CompareSemanticVersions(string? left, string? right)
    {
        var leftParts = ParseVersionParts(left);
        var rightParts = ParseVersionParts(right);
        if (leftParts == null || rightParts == null) return null;

        var length = Math.Max(leftParts.Length, rightParts.Length);
        for (var i = 0; i < length; i++)
        {
            var l = i < leftParts.Length ? leftParts[i] : 0;
            var r = i < rightParts.Length ? rightParts[i] : 0;
            if (l != r) return l.CompareTo(r);
        }

        return 0;
    }

    private static int[]? ParseVersionParts(string? version)
    {
        var clean = (version ?? "").Trim();
        if (clean.StartsWith("v", StringComparison.OrdinalIgnoreCase)) clean = clean[1..];
        clean = clean.Split(['+', '-'], 2)[0];
        var parts = clean.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts.Any(part => !int.TryParse(part, out _))) return null;
        return parts.Select(int.Parse).ToArray();
    }

    private static string StatePath() => Utils.GetConfigPath("colitu-update-state.json");
}

public sealed class ColituUpdateInfo
{
    public int VersionCode { get; set; }
    public string CurrentVersion { get; set; } = "";
    public string NewVersion { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public string? Sha256 { get; set; }
    public bool Force { get; set; }
    public string Notes { get; set; } = "";
    public string? LocalPath { get; set; }
}

public sealed class ColituDownloadProgress
{
    public long TotalBytes { get; init; }
    public long DownloadedBytes { get; init; }
    public int Percent { get; init; }
}

internal sealed class ColituVersionPayload
{
    public int LatestVersionCode { get; set; }
    public string? VersionName { get; set; }
    public string? DownloadUrl { get; set; }
    public string? Sha256 { get; set; }
    public bool ForceUpdate { get; set; }
    public string? ReleaseNotes { get; set; }
    /// <summary>Base64 ECDSA P-256 signature over <see cref="ColituUpdateSignature.Message"/>.</summary>
    public string? Signature { get; set; }
}

/// <summary>
/// The release manifest is signed offline (scripts/build-installer.ps1) with a
/// key that never leaves the release machine. The app runs every update
/// elevated, so HTTPS alone is not enough: a compromised website must not be
/// able to hand every client an installer.
/// </summary>
internal static class ColituUpdateSignature
{
    private const string PublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEb7h8TtW4ekewQccnpdJo2i0fsJ28
        9gl8IkgEiNIAJvkcXryv7AZUf9O4qZboDzW7Jg2rYpnGJrVkxa1HDRmk8Q==
        -----END PUBLIC KEY-----
        """;

    /// <summary>The exact text the release script signs; changing any field breaks the signature.</summary>
    internal static string Message(ColituVersionPayload payload) => string.Join("\n",
        "colitu-windows-update-v1",
        payload.LatestVersionCode.ToString(CultureInfo.InvariantCulture),
        payload.VersionName ?? "",
        payload.DownloadUrl ?? "",
        (payload.Sha256 ?? "").Trim().ToLowerInvariant(),
        payload.ForceUpdate ? "true" : "false");

    internal static bool Verify(ColituVersionPayload payload, string publicKeyPem = PublicKeyPem)
    {
        if (string.IsNullOrWhiteSpace(payload.Signature)
            || string.IsNullOrWhiteSpace(payload.Sha256)
            || string.IsNullOrWhiteSpace(payload.DownloadUrl))
        {
            return false;
        }
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);
            return key.VerifyData(Encoding.UTF8.GetBytes(Message(payload)), Convert.FromBase64String(payload.Signature.Trim()), HashAlgorithmName.SHA256);
        }
        catch
        {
            return false;
        }
    }
}

public sealed class ColituUpdateState
{
    public int AttemptedVersionCode { get; set; }
    public string AttemptedVersionName { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public string? Sha256 { get; set; }
    public DateTimeOffset AttemptedAt { get; set; }
}
