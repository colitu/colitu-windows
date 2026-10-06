using System.ServiceProcess;

namespace Colitu.KillSwitch;

/// <summary>
/// Colitu VPN kill-switch service (LocalSystem, automatic start).
///
/// ColituKillSwitchService.exe              run as the Windows service (started by the SCM)
/// ColituKillSwitchService.exe --remove-all remove every Colitu WFP object (the uninstaller, as administrator)
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var directory = AppContext.BaseDirectory;
        if (args.Length == 1 && args[0] == "--remove-all")
        {
            return RemoveAll(directory);
        }
        if (args.Length != 0 || Environment.UserInteractive)
        {
            Console.Error.WriteLine("This is the Colitu VPN kill-switch service. It is installed and started by the Colitu VPN installer.");
            return 2;
        }

        ServiceBase.Run(new KsWindowsService(directory));
        return 0;
    }

    private static int RemoveAll(string directory)
    {
        try
        {
            var data = KsPaths.DataDirectory();
            var log = new KsFileLog(Path.Combine(data, "service.log"));
            var engine = new KsEngine(new WfpFirewall(), new KsFileStateStore(Path.Combine(data, "state.json")), new KsWindowsSystem(), log, KsInstallLayout.FromDirectory(directory));
            engine.Handle(new KsValidRequest(KsProtocol.CmdDisarm, null, "Colitu VPN uninstalled", null, Purge: true), Environment.ProcessId);
            return 0;
        }
        catch (Exception ex)
        {
            // The data folder may already be gone: remove the firewall objects regardless.
            try
            {
                new WfpFirewall().RemoveAll();
                return 0;
            }
            catch
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }
    }
}

internal sealed class KsWindowsService : ServiceBase
{
    private readonly string _directory;
    private KsPipeServer? _server;

    public KsWindowsService(string directory)
    {
        _directory = directory;
        ServiceName = KsProtocol.ServiceName;
        CanStop = true;
        CanShutdown = false;
        CanPauseAndContinue = false;
        AutoLog = false;
    }

    protected override void OnStart(string[] args)
    {
        var data = KsPaths.DataDirectory();
        var log = new KsFileLog(Path.Combine(data, "service.log"));
        var layout = KsInstallLayout.FromDirectory(_directory);
        var engine = new KsEngine(new WfpFirewall(), new KsFileStateStore(Path.Combine(data, "state.json")), new KsWindowsSystem(), log, layout);
        engine.OnServiceStart();
        _server = new KsPipeServer(engine, log, layout.AppPath);
        _server.Start();
        log.Write($"Service started (protocol v{KsProtocol.Version}, armed={engine.Armed})");
    }

    /// <summary>Stopping the service (an upgrade) leaves the filters in place: still closed if armed.</summary>
    protected override void OnStop()
    {
        _server?.Dispose();
        _server = null;
    }
}
