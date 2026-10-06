namespace v2rayN;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    public static EventWaitHandle ProgramStarted;

    public App()
    {
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
    }

    /// <summary>
    /// Open only one process
    /// </summary>
    /// <param name="e"></param>
    protected override void OnStartup(StartupEventArgs e)
    {
        if (Utils.IsWindows() && !Utils.IsAdministrator())
        {
            if (ProcUtils.RebootAsAdmin())
            {
                Environment.Exit(0);
                return;
            }

            UI.Show("Colitu VPN needs administrator permission to start the VPN core. Please run the app as administrator.");
            Environment.Exit(1);
            return;
        }

        // Any user process can set this variable (HKCU\Environment); the elevated app and its
        // cores must not inherit it.
        Environment.SetEnvironmentVariable(Global.LocalAppData, null);

        var args = e.Args ?? Array.Empty<string>();
        if (args.Any(t => t == UninstallCleanupArg))
        {
            // Run by the uninstaller after it has stopped the app.
            RunUninstallCleanup();
            Environment.Exit(0);
            return;
        }

        // Before logging starts: removes logs of versions that recorded visited sites.
        Services.ColituHardening.CleanLogs();

        var exePathKey = Utils.GetMd5(Utils.GetExePath());

        var rebootas = args.Any(t => t == Global.RebootAs);
        ProgramStarted = new EventWaitHandle(false, EventResetMode.AutoReset, exePathKey, out var bCreatedNew);
        if (!rebootas && !bCreatedNew)
        {
            ProgramStarted.Set();
            Environment.Exit(0);
            return;
        }

        if (!AppManager.Instance.InitApp())
        {
            UI.Show($"Loading GUI configuration file is abnormal,please restart the application{Environment.NewLine}加载GUI配置文件异常,请重启应用");
            Environment.Exit(0);
            return;
        }

        Services.ColituHardening.HardenDataFolders();
        AppManager.Instance.InitComponents();

        // Sign-out or shutdown while connected: hand the system proxy back before Windows ends the process.
        SessionEnding += (_, _) => Services.ColituVpnService.Instance.CleanupForSessionEnd();

        RxAppBuilder.CreateReactiveUIBuilder()
            .WithWpf()
            .BuildApp();

        base.OnStartup(e);
        _ = StartColituShellAsync();
    }

    private async Task StartColituShellAsync()
    {
        try
        {
            if (Views.SetupWindow.IsSetupRequired())
            {
                var setup = new Views.SetupWindow();
                setup.ShowDialog();
                if (!setup.SetupCompleted)
                {
                    Shutdown();
                    return;
                }
            }

            // One window hosts sign-in and the app; it restores the saved session itself.
            var window = new Views.ColituMainWindow();
            MainWindow = window;
            window.Show();

            // A second launch signals this handle: bring the running window forward.
            ThreadPool.RegisterWaitForSingleObject(ProgramStarted, (_, _) => Dispatcher.BeginInvoke(window.BringToFront), null, -1, false);
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            Logging.SaveLog("StartColituShellAsync", ex);
            UI.Show("Colitu VPN interface could not be opened. Please restart the app and try again.");
            Shutdown();
        }
    }

    public const string UninstallCleanupArg = "--colitu-cleanup";

    /// <summary>
    /// Undoes what the app changed in Windows: the system proxy (only when it
    /// still points at Colitu's local port, so a user's own proxy is kept) and
    /// the launch-at-sign-in task. Without this an uninstall while connected
    /// leaves every browser pointing at a proxy that no longer exists.
    /// </summary>
    private static void RunUninstallCleanup()
    {
        // First, and whatever else fails: the kill switch's persistent firewall filters would keep
        // this computer offline after Colitu is gone.
        Services.ColituVpnService.RemoveKillSwitchForUninstall();
        try
        {
            if (!AppManager.Instance.InitApp())
            {
                return;
            }
            var config = AppManager.Instance.Config;
            var port = AppManager.Instance.GetLocalPort(EInboundProtocol.socks);
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
            {
                var enabled = key?.GetValue("ProxyEnable") is int value && value == 1;
                var server = key?.GetValue("ProxyServer") as string ?? "";
                if (enabled && (server.Contains($"{Global.Loopback}:{port}") || server.Contains($"localhost:{port}", StringComparison.OrdinalIgnoreCase)))
                {
                    ServiceLib.Handler.SysProxy.ProxySettingWindows.UnsetProxy();
                    Logging.SaveLog("Uninstall cleanup: system proxy removed");
                }
            }
            config.GuiItem.AutoRun = false;
            AutoStartupHandler.UpdateTask(config).Wait(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            Logging.SaveLog("RunUninstallCleanup", ex);
        }
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logging.SaveLog("App_DispatcherUnhandledException", e.Exception);
        e.Handled = true;
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject != null)
        {
            Logging.SaveLog("CurrentDomain_UnhandledException", (Exception)e.ExceptionObject);
        }
    }

    private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Logging.SaveLog("TaskScheduler_UnobservedTaskException", e.Exception);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Logging.SaveLog("OnExit");
        base.OnExit(e);
        Process.GetCurrentProcess().Kill();
    }
}
