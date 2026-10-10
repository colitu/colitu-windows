namespace v2rayN.Services;

/// <summary>What the mid-session watcher does after a round.</summary>
public enum ColituWatchAction
{
    /// <summary>Nothing to do.</summary>
    None,
    /// <summary>Checks failed but the device itself is offline: count nothing, wait for the network.</summary>
    Offline,
    /// <summary>The primary is dead and the warm spare carries the traffic: no reconnect; the spare leads the next connect.</summary>
    SpareCarries,
    /// <summary>The primary is dead and there is no spare: the usual recovery (restart, next transport).</summary>
    PrimaryDead,
    /// <summary>Both paths are dead (or the primary died behind a dead spare): reconnect.</summary>
    Reconnect
}

/// <summary>
/// Adaptive Connect 2.0 mid-session watcher (every transport, not only Hysteria2). Every 5 s for
/// the first 90 s after the connect, then every 30 s. With a warm spare each round checks the
/// primary alone (the <c>colitu-verify</c> inbound) and the normal path; 3 misses while the device
/// is online make the primary dead. Primary dead with the normal path working is no reason to
/// reconnect: the spare carries the traffic. Pure state, unit tested.
/// </summary>
public sealed class ColituTunnelWatch
{
    public static readonly TimeSpan FastInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan SlowInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan FastPhase = TimeSpan.FromSeconds(90);
    public const int MissesForDead = 3;
    /// <summary>With a dead spare nothing could replace, the primary is given up sooner.</summary>
    public const int MissesForDeadWithoutSpare = 2;

    public int PrimaryMisses { get; private set; }
    public int NormalMisses { get; private set; }
    /// <summary>The primary was declared dead while the spare carries the traffic (said once).</summary>
    public bool PrimaryDeclaredDead { get; private set; }
    /// <summary>The spare died and nothing could replace it.</summary>
    public bool SpareDeadUnreplaced { get; set; }

    public static TimeSpan Interval(TimeSpan sinceConnect) => sinceConnect < FastPhase ? FastInterval : SlowInterval;

    public void Reset()
    {
        PrimaryMisses = 0;
        NormalMisses = 0;
        PrimaryDeclaredDead = false;
        SpareDeadUnreplaced = false;
    }

    /// <summary>
    /// The verdict <paramref name="action"/> could not be acted on yet (inside the minimum gap between
    /// automatic switches, nothing else to switch to, ...): keep the miss count one short of the
    /// threshold, so the very next miss gives the same verdict and the switch happens as soon as it
    /// is allowed. Call it only when the switch did NOT happen; a switch (or a network change) resets.
    /// </summary>
    public void Defer(ColituWatchAction action)
    {
        switch (action)
        {
            case ColituWatchAction.PrimaryDead:
            case ColituWatchAction.Reconnect:
                NormalMisses = Math.Max(NormalMisses, MissesForDead - 1);
                break;
        }
    }

    /// <param name="primaryOk">The primary alone (verify path); null without a spare (the normal path is the primary then).</param>
    /// <param name="normalOk">The normal path (what the apps use).</param>
    /// <param name="online">The device reaches the internet outside the tunnel; only asked when a check failed.</param>
    public ColituWatchAction Round(bool? primaryOk, bool normalOk, bool online)
    {
        var anyMiss = primaryOk == false || !normalOk;
        if (anyMiss && !online)
        {
            return ColituWatchAction.Offline;
        }
        if (primaryOk == null)
        {
            if (normalOk)
            {
                NormalMisses = 0;
                return ColituWatchAction.None;
            }
            if (++NormalMisses < MissesForDead)
            {
                return ColituWatchAction.None;
            }
            NormalMisses = 0;
            return ColituWatchAction.PrimaryDead;
        }

        if (!normalOk)
        {
            if (primaryOk == true)
            {
                PrimaryMisses = 0;
            }
            else
            {
                PrimaryMisses++;
            }
            if (++NormalMisses < MissesForDead)
            {
                return ColituWatchAction.None;
            }
            NormalMisses = 0;
            return ColituWatchAction.Reconnect;
        }
        NormalMisses = 0;
        if (primaryOk == true)
        {
            PrimaryMisses = 0;
            PrimaryDeclaredDead = false;
            return ColituWatchAction.None;
        }
        PrimaryMisses++;
        if (SpareDeadUnreplaced && PrimaryMisses >= MissesForDeadWithoutSpare)
        {
            PrimaryMisses = 0;
            return ColituWatchAction.Reconnect;
        }
        if (PrimaryMisses >= MissesForDead && !PrimaryDeclaredDead)
        {
            PrimaryDeclaredDead = true;
            return ColituWatchAction.SpareCarries;
        }
        return ColituWatchAction.None;
    }
}

/// <summary>
/// Spare health probe: the spare alone (the <c>colitu-verify-spare</c> inbound) while the primary is
/// healthy, every 60 s for a UDP/QUIC spare and every 180 s for a TCP spare (each TCP probe opens a
/// connection). 2 misses while online make it dead. A replacement is swapped in by reloading the
/// core only while the tunnel is quiet (under 10 KB in the last 10 s).
/// </summary>
public sealed class ColituSpareHealth
{
    public static readonly TimeSpan UdpInterval = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan TcpInterval = TimeSpan.FromSeconds(180);
    public const int MissesForDead = 2;
    public const long QuietBytes = 10 * 1024;
    public static readonly TimeSpan QuietWindow = TimeSpan.FromSeconds(10);

    public int Misses { get; private set; }

    public static TimeSpan Interval(string? spareProtocol) => ColituWarmSpare.IsUdp(spareProtocol) ? UdpInterval : TcpInterval;

    public void Reset() => Misses = 0;

    /// <summary>True when the spare is now dead (2 misses while online).</summary>
    public bool Probe(bool ok, bool online)
    {
        if (ok)
        {
            Misses = 0;
            return false;
        }
        if (!online)
        {
            return false;
        }
        if (++Misses < MissesForDead)
        {
            return false;
        }
        Misses = 0;
        return true;
    }

    /// <summary>A core reload may happen now: the tunnel carried less than 10 KB in the last 10 s (unknown, -1, counts as quiet).</summary>
    public static bool CanSwap(long bytesLastWindow) => bytesLastWindow < QuietBytes;
}

/// <summary>Outcome of one parallel round (primary and spare checked at the same time).</summary>
public enum ColituParallelOutcome
{
    /// <summary>The primary carries traffic: connected as planned.</summary>
    PrimaryWins,
    /// <summary>Only the spare carried traffic (also after the primary's 1.5 s grace): the roles swap.</summary>
    SwapRoles,
    /// <summary>Neither carried traffic: the primary is marked failed and this spare is not reused in this connect.</summary>
    BothFailed,
    /// <summary>No spare was attached and the primary failed.</summary>
    PrimaryFailed
}

/// <summary>Adaptive Connect 2.0 parallel connect: one core start, the traffic check on primary and spare at once.</summary>
public static class ColituParallelConnect
{
    /// <summary>The primary may still answer this long after only the spare did.</summary>
    public static readonly TimeSpan PrimaryGrace = TimeSpan.FromMilliseconds(1500);

    /// <param name="spareOk">Null without a spare.</param>
    /// <param name="primaryOkAfterGrace">The primary's grace check (only asked when the primary failed and the spare passed).</param>
    public static ColituParallelOutcome Decide(bool primaryOk, bool? spareOk, bool primaryOkAfterGrace = false) =>
        primaryOk || primaryOkAfterGrace ? ColituParallelOutcome.PrimaryWins
        : spareOk == true ? ColituParallelOutcome.SwapRoles
        : spareOk == false ? ColituParallelOutcome.BothFailed
        : ColituParallelOutcome.PrimaryFailed;
}

/// <summary>
/// Stall marks can't lock a network: when the marks would leave at most one offered transport,
/// they are ignored for that round (a network problem). The order is then: proven (last good)
/// first, then unmarked, then the ignored marks; what failed in this round last.
/// </summary>
public static class ColituTransportOrder
{
    /// <summary>The marks would leave at most one of two or more offered transports.</summary>
    public static bool MarksCoverAlmostAll(IReadOnlyCollection<string> offered, Func<string, bool> stalled) =>
        offered.Count >= 2 && offered.Count(protocol => !stalled(protocol)) <= 1;

    /// <summary>The tier of a transport in a round whose marks are ignored (lower goes first).</summary>
    public static int IgnoredMarksTier(string protocol, Func<string, bool> proven, Func<string, bool> stalled, Func<string, bool> failedThisRound) =>
        failedThisRound(protocol) ? 3 : proven(protocol) ? 0 : stalled(protocol) ? 2 : 1;
}
