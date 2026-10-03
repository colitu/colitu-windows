namespace v2rayN.Services;

/// <summary>
/// Opens links and folders for the user. The app runs elevated, so anything it
/// shell-executes would inherit administrator rights; links are therefore
/// handed to Explorer, which opens them in the user's normal (non-elevated)
/// session, and only web and mail links are accepted, never programs or files.
/// </summary>
public static class ColituShell
{
    /// <summary>True for absolute https:// links (and mailto: when <paramref name="allowMail"/>).</summary>
    internal static bool IsSafeLink(string? url, bool allowMail = true)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }
        if (allowMail && uri.Scheme == Uri.UriSchemeMailto)
        {
            return true;
        }
        return uri.Scheme == Uri.UriSchemeHttps
            && !string.IsNullOrEmpty(uri.Host)
            && string.IsNullOrEmpty(uri.UserInfo)
            && !uri.IsUnc;
    }

    public static bool OpenUrl(string? url, bool allowMail = true)
    {
        if (!IsSafeLink(url, allowMail))
        {
            Logging.SaveLog($"ColituShell.OpenUrl: refused link with an unsupported scheme or form ({Truncate(url)})");
            return false;
        }
        return StartViaExplorer(new Uri(url!.Trim()).AbsoluteUri);
    }

    /// <summary>Shows one of the app's own folders (logs) in Explorer.</summary>
    public static bool OpenFolder(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(Utils.StartupPath());
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(full))
        {
            return false;
        }
        return StartViaExplorer(full);
    }

    private static bool StartViaExplorer(string target)
    {
        try
        {
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            var startInfo = new ProcessStartInfo(explorer) { UseShellExecute = false };
            startInfo.ArgumentList.Add(target);
            using var _ = Process.Start(startInfo);
            return true;
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituShell.StartViaExplorer", ex);
            return false;
        }
    }

    private static string Truncate(string? value) => value == null ? "" : value.Length <= 80 ? value : value[..80];
}
