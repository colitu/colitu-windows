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

        var exePathKey = Utils.GetMd5(Utils.GetExePath());

        var rebootas = (e.Args ?? Array.Empty<string>()).Any(t => t == Global.RebootAs);
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

        AppManager.Instance.InitComponents();

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
