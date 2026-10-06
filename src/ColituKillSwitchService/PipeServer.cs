using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Colitu.KillSwitch;

/// <summary>
/// The service's only door: \\.\pipe\Colitu.KillSwitch.v1.
///
/// - DACL: SYSTEM and BUILTIN\Administrators only (an administrator's token must be elevated for the
///   Administrators SID to count); network logons are denied explicitly. No Everyone, no
///   Authenticated Users, no interactive users: Colitu VPN always runs elevated.
/// - The client process must be ColituVPN.exe from the service's own (admin-only) folder.
/// - One length-limited request per connection, 5 s to send it, every field validated.
/// - Commands only change the Colitu WFP filters; nothing is executed, no path is opened
///   except to turn the app's split-tunnel programs into WFP app ids.
/// </summary>
internal sealed class KsPipeServer(KsEngine engine, IKsLog log, string expectedClientPath) : IDisposable
{
    private const int MaxInstances = 4;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public void Start()
    {
        _loop = Task.Run(() => ListenAsync(_stop.Token));
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // Stopping anyway.
        }
        _stop.Dispose();
    }

    internal static PipeSecurity CreateSecurity()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return security;
    }

    private NamedPipeServerStream CreateInstance(bool first) => NamedPipeServerStreamAcl.Create(
        KsProtocol.PipeName,
        PipeDirection.InOut,
        MaxInstances,
        PipeTransmissionMode.Byte,
        // The first instance must be ours: if another process already created the name, fail instead of joining it.
        PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None),
        KsProtocol.MaxMessageBytes + 4,
        KsProtocol.MaxMessageBytes + 4,
        CreateSecurity());

    private async Task ListenAsync(CancellationToken token)
    {
        NamedPipeServerStream? next = null;
        var first = true;
        while (!token.IsCancellationRequested)
        {
            try
            {
                next ??= CreateInstance(first);
                first = false;
                await next.WaitForConnectionAsync(token).ConfigureAwait(false);
                var current = next;
                // The next instance exists before this one is closed: the name never disappears, so
                // no other process can create it in between.
                next = CreateInstance(false);
                _ = Task.Run(() => ServeAsync(current, token), token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                log.Write($"Pipe listener: {ex.GetType().Name} {ex.Message}");
                next?.Dispose();
                next = null;
                try
                {
                    await Task.Delay(1000, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        next?.Dispose();
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken stopping)
    {
        await using var connection = pipe.ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var pid = KsNative.ClientProcessId(pipe.SafePipeHandle);
            var image = pid is { } id ? KsNative.ImagePath(id) : null;
            if (pid == null || !string.Equals(image, expectedClientPath, StringComparison.OrdinalIgnoreCase))
            {
                log.Write($"Refused a client that is not Colitu VPN (pid {pid?.ToString() ?? "?"})");
                await RespondAsync(pipe, new KsResponse { Ok = false, Error = KsProtocol.ErrUnauthorized }, timeout.Token).ConfigureAwait(false);
                return;
            }

            byte[] payload;
            try
            {
                payload = await KsCodec.ReadFrameAsync(pipe, timeout.Token).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                await RespondAsync(pipe, new KsResponse { Ok = false, Error = KsProtocol.ErrBadRequest }, timeout.Token).ConfigureAwait(false);
                return;
            }

            if (!KsCodec.TryDecodeRequest(payload, out var request, out _)
                || !KsValidator.TryValidate(request, out var valid, out var error))
            {
                var code = request != null && request.V != KsProtocol.Version ? KsProtocol.ErrUnsupportedVersion : KsProtocol.ErrBadRequest;
                await RespondAsync(pipe, new KsResponse { Id = SafeId(request?.Id), Ok = false, Error = code }, timeout.Token).ConfigureAwait(false);
                return;
            }

            var response = engine.Handle(valid!, (int)pid.Value);
            await RespondAsync(pipe, response, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Too slow or shutting down.
        }
        catch (IOException)
        {
            // The client went away.
        }
        catch (Exception ex)
        {
            log.Write($"Pipe client: {ex.GetType().Name} {ex.Message}");
        }
    }

    private static string? SafeId(string? id) => id is { Length: <= KsProtocol.MaxIdLength } && id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_') ? id : null;

    private static Task RespondAsync(Stream pipe, KsResponse response, CancellationToken token) =>
        KsCodec.WriteFrameAsync(pipe, KsCodec.Encode(response), token);
}
