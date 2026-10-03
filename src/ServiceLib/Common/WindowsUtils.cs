using Microsoft.Win32;

namespace ServiceLib.Common;

internal static class WindowsUtils
{
    private static readonly string _tag = "WindowsUtils";

    public static string? RegReadValue(string path, string name, string def)
    {
        RegistryKey? regKey = null;
        try
        {
            regKey = Registry.CurrentUser.OpenSubKey(path, false);
            var value = regKey?.GetValue(name) as string;
            return value.IsNullOrEmpty() ? def : value;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
        finally
        {
            regKey?.Close();
        }
        return def;
    }

    public static void RegWriteValue(string path, string name, object value)
    {
        RegistryKey? regKey = null;
        try
        {
            regKey = Registry.CurrentUser.CreateSubKey(path);
            if (value.ToString().IsNullOrEmpty())
            {
                regKey?.DeleteValue(name, false);
            }
            else
            {
                regKey?.SetValue(name, value);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
        finally
        {
            regKey?.Close();
        }
    }

    public static async Task RemoveTunDevice()
    {
        var tunNameList = new List<string> { "wintunsingbox_tun", "xray_tun" };
        // The cores delete their adapter when they stop, so usually there is nothing to remove;
        // pnputil then only costs about a second per name on every connection attempt.
        var present = PresentInterfaceNames();
        foreach (var tunName in tunNameList)
        {
            if (present != null && !present.Any(name => tunName.EndsWith(name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            try
            {
                var sum = MD5.HashData(Encoding.UTF8.GetBytes(tunName));
                var guid = new Guid(sum);
                var pnpUtilPath = @"C:\Windows\System32\pnputil.exe";
                var arg = $$""" /remove-device  "SWD\Wintun\{{{guid}}}" """;

                // Try to remove the device
                _ = await Utils.GetCliWrapOutput(pnpUtilPath, arg);
            }
            catch (Exception ex)
            {
                Logging.SaveLog(_tag, ex);
            }
        }
    }

    /// <summary>Names of the network adapters Windows knows about, or null if they cannot be listed.</summary>
    private static List<string>? PresentInterfaceNames()
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Select(adapter => adapter.Name)
                .Where(name => name is "singbox_tun" or "xray_tun")
                .ToList();
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return null;
        }
    }
}
