namespace Icarus.Core;

/// <summary>
/// Whether the owner's process is still running. Abstracted so the release policy can be
/// tested against a simulated process table.
/// </summary>
public interface IProcessProbe
{
    bool IsRunning(int pid);
}

/// <summary>
/// The watchdog's decision, and the reason behind it.
///
/// The rule is deliberately one-directional. When the watchdog is unsure, it releases:
/// an early key-up costs a user one extra press, while a late one leaves their keyboard
/// stuck with no indication why. Availability of a keystroke is recoverable; a stuck
/// modifier is not.
/// </summary>
public sealed record ReleaseDecision(ReleaseReason Reason, IReadOnlyList<OwnedInput> ToRelease)
{
    public bool ShouldRelease => Reason != ReleaseReason.None && ToRelease.Count > 0;

    /// <summary>Explanation suitable for a log line or the status strip.</summary>
    public string Explain() => Reason switch
    {
        ReleaseReason.None => "Owner is alive; no release needed.",
        ReleaseReason.OwnerExited => "Owning process exited; releasing held inputs.",
        ReleaseReason.HeartbeatStalled => "Owner stopped responding; releasing held inputs.",
        ReleaseReason.OwnerRequested => "Owner requested release.",
        _ => "Releasing held inputs.",
    };
}

/// <summary>
/// Release policy for the crash-proof watchdog.
///
/// Managed cleanup cannot run when a process is killed, so the guarantee that no input
/// stays held has to be enforced from outside the process. This type holds that policy: it
/// answers a single question — given what the shared state says, should the watchdog send
/// key-ups, and for which inputs?
/// </summary>
public static class WatchdogPolicy
{
    /// <summary>
    /// Decides whether to release.
    ///
    /// Order matters. An explicit request is honoured first because the owner knows best.
    /// A dead process is unambiguous. A stall is the ambiguous case and is treated as death,
    /// which is the safe direction: releasing early breaks one keystroke, releasing late
    /// leaves a key stuck.
    /// </summary>
    public static ReleaseDecision Decide(
        InputLease lease,
        long lastSeenHeartbeat,
        long nowHeartbeat,
        IProcessProbe processes,
        IStallClock clock)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(clock);

        var owned = lease.Snapshot();

        // No owner means there is nothing this watchdog is responsible for.
        if (lease.OwnerPid == 0)
            return new ReleaseDecision(ReleaseReason.None, []);

        if (lease.ReleaseRequested)
            return new ReleaseDecision(ReleaseReason.OwnerRequested, owned);

        if (!processes.IsRunning(lease.OwnerPid))
            return new ReleaseDecision(ReleaseReason.OwnerExited, owned);

        // The heartbeat advanced since the last check, so the owner is demonstrably alive
        // and no release is due. This is the common case and must stay cheap.
        if (nowHeartbeat != lastSeenHeartbeat)
            return new ReleaseDecision(ReleaseReason.None, []);

        // Heartbeat unchanged. How long has it been unchanged? The counter alone cannot
        // say, so the caller supplies elapsed time via the clock.
        if (clock.ElapsedMs(lease) >= lease.StallTimeoutMs)
            return new ReleaseDecision(ReleaseReason.HeartbeatStalled, owned);

        return new ReleaseDecision(ReleaseReason.None, []);
    }
}

/// <summary>
/// Supplies elapsed milliseconds for the stall test. The production implementation reads
/// the wall clock; tests supply a controllable one so the timeout can be exercised without
/// waiting.
///
/// Named IStallClock rather than IClock because Icarus.Core already has an IClock used by
/// the macro engine for scheduling waits. They are unrelated and must not be conflated.
/// </summary>
public interface IStallClock
{
    /// <summary>
    /// Milliseconds since the lease was last observed to have a *changed* heartbeat. The
    /// watchdog passes a monotonic counter, so a stalled owner keeps returning the same
    /// value and this grows.
    /// </summary>
    double ElapsedMs(InputLease lease);
}

/// <summary>Wall-clock implementation used by the watchdog process.</summary>
public sealed class StallClock : IStallClock
{
    private readonly Dictionary<long, (long Heartbeat, DateTimeOffset Since)> seen = [];

    public double ElapsedMs(InputLease lease)
    {
        lock (seen)
        {
            if (!seen.TryGetValue(lease.Generation, out var entry) || entry.Heartbeat != lease.Heartbeat)
            {
                // The heartbeat moved, so the stall timer restarts from now.
                seen[lease.Generation] = (lease.Heartbeat, DateTimeOffset.UtcNow);
                return 0;
            }
            return (DateTimeOffset.UtcNow - entry.Since).TotalMilliseconds;
        }
    }

    /// <summary>Drops tracking for leases whose owner is long gone, so the map cannot grow.</summary>
    public void Forget(long generation)
    {
        lock (seen) seen.Remove(generation);
    }
}
