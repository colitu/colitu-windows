using System.Security.AccessControl;
using System.Security.Principal;

namespace v2rayN.Services;

/// <summary>
/// Local data protection. The app keeps its data next to the executable in
/// Program Files, where every Windows user can read by default; the folders
/// that hold sessions, generated core configs (server credentials) and logs
/// are restricted to administrators and SYSTEM, which is what the app runs as.
/// </summary>
public static class ColituHardening
{
    /// <summary>Folders that may contain credentials, tokens or connection details.</summary>
    private static readonly string[] DataFolders = ["guiConfigs", "binConfigs", "guiLogs", "guiTemps", "guiBackups", ColituUpdateService.UpdateFolderName];

    private static readonly TimeSpan LogRetention = TimeSpan.FromDays(7);

    // Written once the logs of versions that recorded visited sites are gone.
    private const string LogPurgeMarker = "colitu-log-privacy-v1";

    public static void HardenDataFolders()
    {
        if (!Utils.IsWindows() || !Utils.IsAdministrator())
        {
            return;
        }
        foreach (var name in DataFolders)
        {
            try
            {
                var path = Path.Combine(Utils.StartupPath(), name);
                Directory.CreateDirectory(path);
                new DirectoryInfo(path).SetAccessControl(AdminOnlySecurity());
            }
            catch (Exception ex)
            {
                Logging.SaveLog($"ColituHardening.HardenDataFolders {name}", ex);
            }
        }
    }

    /// <summary>Creates an empty folder that only administrators and SYSTEM can read or change.</summary>
    public static string CreateProtectedDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        var info = new DirectoryInfo(path);
        if (OperatingSystem.IsWindows())
        {
            info.Create(AdminOnlySecurity());
        }
        else
        {
            info.Create();
        }
        return path;
    }

    /// <summary>
    /// Log hygiene, run before logging starts: logs of earlier versions
    /// recorded every DNS lookup (the sites a user visited), so they are
    /// removed once; afterwards logs older than a week are deleted.
    /// </summary>
    public static void CleanLogs()
    {
        try
        {
            var folder = Path.Combine(Utils.StartupPath(), "guiLogs");
            if (!Directory.Exists(folder))
            {
                return;
            }
            var marker = Path.Combine(Utils.StartupPath(), "guiConfigs", LogPurgeMarker);
            var purgeAll = !File.Exists(marker);
            var cutoff = DateTime.UtcNow - LogRetention;
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                if (purgeAll || file.LastWriteTimeUtc < cutoff)
                {
                    try { file.Delete(); } catch { }
                }
            }
            if (purgeAll)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
                File.WriteAllText(marker, DateTimeOffset.UtcNow.ToString("O"));
            }
        }
        catch
        {
            // Logging is not set up yet; a failed cleanup must not stop the app.
        }
    }

    /// <summary>
    /// Per Windows user file in guiConfigs. Sessions and cached connection
    /// settings are DPAPI-protected for one user; with a shared file every
    /// user of the PC signing in would sign the others out.
    /// </summary>
    public static string UserConfigPath(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        return Utils.GetConfigPath($"{stem}-{UserKey.Value}{extension}");
    }

    private static readonly Lazy<string> UserKey = new(() =>
    {
        string? sid = null;
        try
        {
            sid = WindowsIdentity.GetCurrent().User?.Value;
        }
        catch
        {
            // Fall back to the account name below.
        }
        var seed = sid ?? Environment.UserName;
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("colitu-user-v1|" + seed));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    });

    /// <summary>
    /// True when the app runs as a different Windows account than the one signed in to this
    /// session: a standard user approved the UAC prompt with an administrator's credentials.
    /// Per-user settings (the system proxy) then land in the administrator's profile.
    /// </summary>
    public static bool ElevatedAsAnotherUser => ElevatedAsAnotherUserLazy.Value;

    private static readonly Lazy<bool> ElevatedAsAnotherUserLazy = new(() =>
    {
        try
        {
            var sessionUser = SessionAccount();
            var processSid = WindowsIdentity.GetCurrent().User;
            if (sessionUser == null || processSid == null)
            {
                return false;
            }
            // Compared by SID: account names come in several spellings (Microsoft and Entra
            // accounts); a name that can't be resolved counts as "same user" (exception below).
            var sessionSid = (SecurityIdentifier)new NTAccount(sessionUser).Translate(typeof(SecurityIdentifier));
            return sessionSid != processSid;
        }
        catch
        {
            return false;
        }
    });

    /// <summary>DOMAIN\user signed in to the session this process runs in, or null.</summary>
    private static string? SessionAccount()
    {
        string? Query(int infoClass)
        {
            if (!WTSQuerySessionInformationW(IntPtr.Zero, WtsCurrentSession, infoClass, out var buffer, out _) || buffer == IntPtr.Zero)
            {
                return null;
            }
            try
            {
                return Marshal.PtrToStringUni(buffer);
            }
            finally
            {
                WTSFreeMemory(buffer);
            }
        }

        var user = Query(WtsUserName);
        var domain = Query(WtsDomainName);
        return string.IsNullOrEmpty(user) ? null : string.IsNullOrEmpty(domain) ? user : $"{domain}\\{user}";
    }

    private const int WtsCurrentSession = -1;
    private const int WtsUserName = 5;
    private const int WtsDomainName = 7;

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    private static DirectorySecurity AdminOnlySecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        }
        return security;
    }
}
