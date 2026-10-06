using System.Text.Json;
using System.Text.Json.Serialization;

namespace Colitu.KillSwitch;

/// <summary>What the service remembers between runs (and reboots), next to the persistent WFP filters.</summary>
public sealed class KsState
{
    public bool Armed { get; set; }
    public DateTimeOffset? ArmedAt { get; set; }
    /// <summary>When Windows had booted at the time of arming (to tell a reboot from a service restart).</summary>
    public DateTimeOffset? ArmedBootTime { get; set; }
    public string? Reason { get; set; }
    public int? OwnerPid { get; set; }
    public DateTimeOffset? OwnerStartTime { get; set; }
    /// <summary>The last validated arm request, so a service restart can restore exactly these filters.</summary>
    public KsArm? Arm { get; set; }
    public string? LastDisarmReason { get; set; }
    public DateTimeOffset? LastDisarmAt { get; set; }
}

public interface IKsStateStore
{
    /// <summary>Null when nothing was ever saved (or the file is unreadable).</summary>
    KsState? Load();

    void Save(KsState state);
}

public interface IKsSystem
{
    DateTimeOffset Now { get; }

    DateTimeOffset BootTime { get; }

    /// <summary>Start time of the process when it is still running, else null.</summary>
    DateTimeOffset? ProcessStartTime(int pid);
}

public interface IKsLog
{
    void Write(string message);
}

/// <summary>
/// The kill switch's state machine. Disarmed ⇄ Armed; arming again replaces the allow list
/// atomically. Failing closed: nothing but an explicit disarm (or a reboot the app will not
/// recover from) opens the internet again — not the app crashing, not the core dying, not this
/// service restarting.
/// </summary>
public sealed class KsEngine
{
    private static readonly TimeSpan BootTolerance = TimeSpan.FromMinutes(2);

    private readonly IKsFirewall _firewall;
    private readonly IKsStateStore _store;
    private readonly IKsSystem _system;
    private readonly IKsLog _log;
    private readonly KsInstallLayout _layout;
    private readonly object _gate = new();
    private KsState _state = new();

    public KsEngine(IKsFirewall firewall, IKsStateStore store, IKsSystem system, IKsLog log, KsInstallLayout layout)
    {
        _firewall = firewall;
        _store = store;
        _system = system;
        _log = log;
        _layout = layout;
    }

    public bool Armed
    {
        get
        {
            lock (_gate)
            {
                return _state.Armed;
            }
        }
    }

    /// <summary>
    /// Service start (boot, crash restart, upgrade). Restores what the saved state says, so
    /// filters deleted behind the service's back come back, and decides what a reboot means.
    /// </summary>
    public void OnServiceStart()
    {
        lock (_gate)
        {
            var saved = _store.Load();
            int existing;
            try
            {
                existing = _firewall.CountFilters();
            }
            catch (Exception ex)
            {
                _log.Write($"Could not read the firewall: {ex.Message}");
                existing = 0;
            }

            if (saved == null)
            {
                _state = new KsState();
                if (existing > 0)
                {
                    // Filters without a record (the state file was lost): keep blocking and let
                    // the app decide on its next start.
                    _state.Armed = true;
                    _state.ArmedAt = _system.Now;
                    _state.ArmedBootTime = _system.BootTime;
                    _state.Reason = "previous state unknown";
                    Persist();
                    _log.Write($"Start: {existing} filters without saved state; staying closed");
                }
                return;
            }

            _state = saved;
            if (!_state.Armed)
            {
                if (existing > 0)
                {
                    // Only possible when something else left them (a disarm always removes the filters first).
                    _log.Write($"Start: disarmed but {existing} stray filters found; removing them");
                    TryRemoveFilters();
                }
                return;
            }

            var rebooted = _state.ArmedBootTime is { } armedBoot && _system.BootTime - armedBoot > BootTolerance;
            if (rebooted && _state.Arm?.KeepAfterReboot != true)
            {
                // The app does not start at sign-in or does not reconnect by itself: nobody would come
                // to explain the blocked internet, so the reboot ends the protection.
                DisarmLocked("Windows restarted and Colitu is not set to reconnect at sign-in", purge: false);
                return;
            }

            if (_state.Arm != null && KsValidator.TryValidateArm(_state.Arm, out var arm, out _))
            {
                try
                {
                    var added = _firewall.Apply(KsPlanBuilder.Build(arm!, _layout));
                    _log.Write($"Start: armed state restored ({added} filters{(rebooted ? ", after a reboot" : "")})");
                }
                catch (Exception ex)
                {
                    _log.Write($"Start: could not restore the filters: {ex.Message}");
                }
            }
            else if (existing == 0)
            {
                _log.Write("Start: armed without a usable allow list and no filters; nothing to restore");
            }
        }
    }

    public KsResponse Handle(KsValidRequest request, int clientPid)
    {
        lock (_gate)
        {
            try
            {
                switch (request.Cmd)
                {
                    case KsProtocol.CmdArm:
                        ArmLocked(request, clientPid);
                        break;
                    case KsProtocol.CmdDisarm:
                        DisarmLocked(request.Reason.Length > 0 ? request.Reason : "disarmed by the app", request.Purge);
                        break;
                }
                return new KsResponse { Id = request.Id, Ok = true, Status = StatusLocked() };
            }
            catch (KsFirewallException ex)
            {
                _log.Write($"{request.Cmd} failed: {ex.Message}");
                return new KsResponse { Id = request.Id, Ok = false, Error = KsProtocol.ErrFirewall, Status = StatusLocked() };
            }
        }
    }

    public KsStatus Status()
    {
        lock (_gate)
        {
            return StatusLocked();
        }
    }

    private void ArmLocked(KsValidRequest request, int clientPid)
    {
        var arm = request.Arm!;
        var wasArmed = _state.Armed;
        // Record the intent first: if the service dies half way, its restart re-applies (fails closed).
        _state.Armed = true;
        _state.ArmedAt = wasArmed ? _state.ArmedAt ?? _system.Now : _system.Now;
        _state.ArmedBootTime = _system.BootTime;
        _state.Reason = request.Reason.Length > 0 ? request.Reason : "armed by the app";
        _state.OwnerPid = clientPid;
        _state.OwnerStartTime = _system.ProcessStartTime(clientPid);
        _state.Arm = ToWire(arm);
        Persist();
        try
        {
            var added = _firewall.Apply(KsPlanBuilder.Build(arm, _layout));
            _log.Write($"{(wasArmed ? "Re-armed" : "Armed")} ({_state.Reason}): {added} filters, split={arm.SplitMode}, cores={(arm.CoreFullAccess ? "full" : $"{arm.Endpoints.Count} endpoints")}, lan={arm.AllowLan}");
        }
        catch
        {
            if (!wasArmed)
            {
                // Nothing was blocking before and nothing could be added: say so instead of claiming protection.
                _state.Armed = false;
                _state.Arm = null;
                Persist();
                TryRemoveFilters();
            }
            throw;
        }
    }

    private void DisarmLocked(string reason, bool purge)
    {
        // Filters first, then the record: a crash in between leaves "armed", which restores them.
        if (purge)
        {
            _firewall.RemoveAll();
        }
        else
        {
            _firewall.RemoveFilters();
        }
        var wasArmed = _state.Armed;
        _state.Armed = false;
        _state.Arm = null;
        _state.OwnerPid = null;
        _state.OwnerStartTime = null;
        _state.LastDisarmReason = reason;
        _state.LastDisarmAt = _system.Now;
        Persist();
        if (wasArmed || purge)
        {
            _log.Write($"Disarmed ({reason}){(purge ? ", provider and sublayer removed" : "")}");
        }
    }

    private KsStatus StatusLocked()
    {
        var ownerAlive = _state.OwnerPid is { } pid
            && _system.ProcessStartTime(pid) is { } started
            && (_state.OwnerStartTime == null || (started - _state.OwnerStartTime.Value).Duration() < TimeSpan.FromSeconds(1));
        int filters;
        try
        {
            filters = _firewall.CountFilters();
        }
        catch
        {
            filters = -1;
        }
        return new KsStatus
        {
            Armed = _state.Armed,
            ArmedAt = _state.ArmedAt,
            Reason = _state.Reason,
            OwnerPid = _state.OwnerPid,
            OwnerAlive = _state.Armed && ownerAlive,
            KeepAfterReboot = _state.Arm?.KeepAfterReboot == true,
            ArmedBeforeBoot = _state.Armed && _state.ArmedBootTime is { } boot && _system.BootTime - boot > BootTolerance,
            SplitMode = _state.Arm?.SplitMode,
            Filters = filters,
            LastDisarmReason = _state.LastDisarmReason,
            LastDisarmAt = _state.LastDisarmAt
        };
    }

    private void TryRemoveFilters()
    {
        try
        {
            _firewall.RemoveFilters();
        }
        catch (Exception ex)
        {
            _log.Write($"Removing filters failed: {ex.Message}");
        }
    }

    private void Persist()
    {
        try
        {
            _store.Save(_state);
        }
        catch (Exception ex)
        {
            _log.Write($"Saving the state failed: {ex.Message}");
        }
    }

    private static KsArm ToWire(KsValidArm arm) => new()
    {
        AllowLan = arm.AllowLan,
        CoreAccess = arm.CoreFullAccess ? KsProtocol.CoreAccessFull : KsProtocol.CoreAccessEndpoints,
        Endpoints = arm.Endpoints.Select(endpoint => new KsEndpoint
        {
            Ip = endpoint.Address.ToString(),
            Port = endpoint.Port,
            Proto = endpoint.Proto switch { KsProto.Tcp => "tcp", KsProto.Udp => "udp", _ => "any" }
        }).ToList(),
        SplitMode = arm.SplitMode switch { KsSplitMode.Bypass => KsProtocol.SplitBypass, KsSplitMode.Only => KsProtocol.SplitOnly, _ => KsProtocol.SplitOff },
        Apps = arm.Apps.ToList(),
        Networks = arm.Networks.Select(network => network.ToString()).ToList(),
        KeepAfterReboot = arm.KeepAfterReboot
    };
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(KsState))]
internal sealed partial class KsStateJsonContext : JsonSerializerContext;

/// <summary>State file in %ProgramData%\Colitu VPN\KillSwitch, readable and writable by SYSTEM and administrators only.</summary>
public sealed class KsFileStateStore(string path) : IKsStateStore
{
    public KsState? Load()
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), KsStateJsonContext.Default.KsState) : null;
        }
        catch
        {
            return null;
        }
    }

    public void Save(KsState state)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, KsStateJsonContext.Default.KsState));
        File.Move(temp, path, overwrite: true);
    }
}
