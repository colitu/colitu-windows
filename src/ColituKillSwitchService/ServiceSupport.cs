using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace Colitu.KillSwitch;

internal sealed class KsWindowsSystem : IKsSystem
{
    public DateTimeOffset Now => DateTimeOffset.UtcNow;

    public DateTimeOffset BootTime => DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    public DateTimeOffset? ProcessStartTime(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited ? null : new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>A small rotating log next to the state file; no addresses, only what the switch did.</summary>
internal sealed class KsFileLog(string path) : IKsLog
{
    private const long MaxBytes = 256 * 1024;
    private readonly object _gate = new();

    public void Write(string message)
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                {
                    File.Move(path, path + ".1", overwrite: true);
                }
                File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {message}{Environment.NewLine}", Encoding.UTF8);
            }
            catch
            {
                // Logging never stops the kill switch.
            }
        }
    }
}

internal static class KsPaths
{
    private const int Attempts = 3;

    /// <summary>%ProgramData%\Colitu VPN\KillSwitch, owned by SYSTEM with an ACL for SYSTEM and administrators only.</summary>
    public static string DataDirectory()
    {
        var parent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Colitu VPN");
        var directory = Path.Combine(parent, "KillSwitch");
        EnsureTrusted(parent);
        EnsureTrusted(directory);
        return directory;
    }

    // Standard users may create folders under %ProgramData%. A folder someone
    // else created keeps them as owner (WRITE_DAC) and may be a junction or hold
    // planted hard links, so SYSTEM's log/state writes could land elsewhere.
    // Such a folder is never reused: it is deleted and created again.
    private static void EnsureTrusted(string path)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var info = new DirectoryInfo(path);
            if (info.Exists && !IsTrusted(info))
            {
                // On a reparse point Delete() removes only the link, never the target.
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) info.Delete();
                else info.Delete(recursive: true);
                info.Refresh();
            }
            var created = !info.Exists;
            if (created)
            {
                info.Create(Security());
                info.Refresh();
            }
            info.SetAccessControl(Security());
            info.Refresh();
            // Re-check after setting owner and ACL: a folder that appeared (or
            // filled) between our delete and create is someone else's.
            if (IsTrusted(info) && (!created || !info.EnumerateFileSystemInfos().Any()))
            {
                return;
            }
        }
        throw new UnauthorizedAccessException($"Could not take ownership of {path}.");
    }

    private static bool IsTrusted(DirectoryInfo info)
    {
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return false;
        }
        var owner = info.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        return owner is not null && (owner.IsWellKnown(WellKnownSidType.LocalSystemSid) || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));
    }

    private static DirectorySecurity Security()
    {
        var security = new DirectorySecurity();
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }
        return security;
    }
}

internal static class KsNative
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>Full Win32 path of a running process's executable, or null.</summary>
    public static string? ImagePath(uint pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            var buffer = new StringBuilder(1024);
            var size = (uint)buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, (int)size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public static uint? ClientProcessId(SafeHandle pipe) => GetNamedPipeClientProcessId(pipe, out var pid) ? pid : null;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafeHandle pipe, out uint clientProcessId);
}
