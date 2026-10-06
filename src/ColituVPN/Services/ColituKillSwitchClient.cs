using System.IO.Pipes;
using Colitu.KillSwitch;

namespace v2rayN.Services;

/// <summary>
/// Talks to the Colitu kill-switch service over its named pipe. One request per connection;
/// before anything is sent the pipe's server must be ColituKillSwitchService.exe from this
/// app's own (admin-only) folder, so a process squatting the pipe name gets nothing and can't
/// pretend the switch is armed.
/// </summary>
public sealed class ColituKillSwitchClient
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(8);

    private readonly string _expectedServer;

    public ColituKillSwitchClient(string? appDirectory = null)
    {
        _expectedServer = Path.Combine(appDirectory ?? AppContext.BaseDirectory, KsProtocol.ServiceExeName);
    }

    /// <summary>The service is shipped next to the app (installed builds; not a development build).</summary>
    public bool ServiceInstalled => File.Exists(_expectedServer);

    /// <exception cref="ColituKillSwitchUnavailableException">The service is not installed, not running or not genuine.</exception>
    public async Task<KsResponse> SendAsync(KsRequest request, CancellationToken token = default)
    {
        request.V = KsProtocol.Version;
        request.Id ??= Guid.NewGuid().ToString("N");
        if (!KsValidator.TryValidate(request, out _, out var invalid))
        {
            // Never send what the service would refuse anyway; a bug here must be loud in the log.
            throw new ArgumentException($"Invalid kill-switch request: {invalid}");
        }

        using var pipe = new NamedPipeClientStream(".", KsProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            connect.CancelAfter(ConnectTimeout);
            try
            {
                await pipe.ConnectAsync(connect.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException or UnauthorizedAccessException)
            {
                throw new ColituKillSwitchUnavailableException($"service not reachable ({ex.GetType().Name})");
            }
        }

        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverPid)
            || !string.Equals(ImagePath(serverPid), _expectedServer, StringComparison.OrdinalIgnoreCase))
        {
            throw new ColituKillSwitchUnavailableException("the pipe is not served by the Colitu kill-switch service");
        }

        using var reply = CancellationTokenSource.CreateLinkedTokenSource(token);
        reply.CancelAfter(ReplyTimeout);
        try
        {
            await KsCodec.WriteFrameAsync(pipe, KsCodec.Encode(request), reply.Token);
            var payload = await KsCodec.ReadFrameAsync(pipe, reply.Token);
            if (!KsCodec.TryDecodeResponse(payload, out var response) || response == null
                || (response.Id != null && response.Id != request.Id))
            {
                throw new ColituKillSwitchUnavailableException("unreadable answer from the service");
            }
            return response;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException)
        {
            throw new ColituKillSwitchUnavailableException($"no answer from the service ({ex.GetType().Name})");
        }
    }

    public Task<KsResponse> ArmAsync(KsArm arm, string reason, CancellationToken token = default) =>
        SendAsync(new KsRequest { Cmd = KsProtocol.CmdArm, Arm = arm, Reason = Shorten(reason) }, token);

    public Task<KsResponse> DisarmAsync(string reason, bool purge = false, CancellationToken token = default) =>
        SendAsync(new KsRequest { Cmd = KsProtocol.CmdDisarm, Reason = Shorten(reason), Purge = purge }, token);

    public Task<KsResponse> StatusAsync(CancellationToken token = default) =>
        SendAsync(new KsRequest { Cmd = KsProtocol.CmdStatus }, token);

    internal static string Shorten(string reason)
    {
        var clean = new string((reason ?? "").Where(ch => !char.IsControl(ch)).ToArray());
        return clean.Length <= KsProtocol.MaxReasonLength ? clean : clean[..KsProtocol.MaxReasonLength];
    }

    private static string? ImagePath(uint pid)
    {
        var handle = OpenProcess(0x1000, false, pid);
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafeHandle pipe, out uint serverProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}

public sealed class ColituKillSwitchUnavailableException(string message) : Exception(message);
