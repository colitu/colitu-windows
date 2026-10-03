using System.IO;

namespace v2rayN.Views;

public partial class SetupWindow : Window
{
    public bool SetupCompleted { get; private set; }

    private static readonly string[] RequiredFiles = ["xray.exe", "wintun.dll", "geoip.dat", "geosite.dat"];

    public SetupWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RunSetupAsync();
    }

    public static bool IsSetupRequired()
    {
        var binXray = Utils.GetBinPath("xray.exe", "xray");
        return !File.Exists(binXray);
    }

    private async Task RunSetupAsync()
    {
        try
        {
            ErrorText.Visibility = Visibility.Collapsed;
            RetryButton.Visibility = Visibility.Collapsed;
            SetupProgress.Value = 0;

            var sourceDir = FindSourceDir();
            if (sourceDir == null)
            {
                ShowError("VPN core files not found.\n\nExpected folder: xray-dosyalari\\ next to the application.");
                return;
            }

            StatusText.Text = "Copying VPN core files...";
            SetupProgress.Value = 10;
            await Task.Delay(200);

            var destDir = Utils.GetBinPath("", "xray");
            Directory.CreateDirectory(destDir);

            var allFiles = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);
            int total = allFiles.Length;
            int done = 0;

            foreach (var srcFile in allFiles)
            {
                var relative = Path.GetRelativePath(sourceDir, srcFile);
                var destFile = Path.Combine(destDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);

                StatusText.Text = $"Copying {Path.GetFileName(srcFile)}...";
                File.Copy(srcFile, destFile, overwrite: true);

                done++;
                SetupProgress.Value = 10 + (int)(done * 85.0 / total);
                await Task.Delay(40);
            }

            SetupProgress.Value = 100;
            StatusText.Text = "Setup complete!";
            TitleText.Text = "Ready to go";
            SubText.Text = "Colitu VPN is set up and ready.";
            await Task.Delay(800);

            SetupCompleted = true;
            Close();
        }
        catch (Exception ex)
        {
            ShowError($"Setup failed: {ex.Message}");
        }
    }

    private static string? FindSourceDir()
    {
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? AppDomain.CurrentDomain.BaseDirectory;

        // Only the folder the installer put next to the app. Parent folders are not searched:
        // anyone can create C:\xray-dosyalari, and whatever is copied from there runs as administrator.
        var candidates = new[]
        {
            Path.Combine(exeDir, "xray-dosyalari"),
#if DEBUG
            // Development runs from v2rayN\bin\Debug\...
            Path.Combine(exeDir, "..", "..", "..", "..", "..", "xray-dosyalari"),
#endif
        };

        foreach (var dir in candidates)
        {
            var normalized = Path.GetFullPath(dir);
            if (Directory.Exists(normalized) && File.Exists(Path.Combine(normalized, "xray.exe")))
                return normalized;
        }
        return null;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        RetryButton.Visibility = Visibility.Visible;
        StatusText.Text = "Setup failed.";
    }

    private async void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        await RunSetupAsync();
    }
}
