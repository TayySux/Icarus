namespace Icarus.Core;

/// <summary>
/// One input the engine currently owns: pressed by this application and not yet released.
/// </summary>
public readonly record struct OwnedInput(bool IsMouse, ushort Code, bool Extended)
{
    public InputToken ToToken() => new(IsMouse, Code, Extended);
}

/// <summary>Why the watchdog decided to release.</summary>
public enum ReleaseReason
{
    /// <summary>The owner is alive and heartbeating. Nothing to do.</summary>
    None,

    /// <summary>The owning process no longer exists.</summary>
    OwnerExited,

    /// <summary>The owner stopped heartbeating, so it is hung or was killed.</summary>
    HeartbeatStalled,

    /// <summary>The owner asked for a release explicitly.</summary>
    OwnerRequested,
}

/// <summary>
/// The shared state a watchdog reads to decide whether held inputs are still wanted, and
/// the decision it derives from that state.
///
/// This type is pure. All the policy about when a stuck key must be released lives here
/// so it can be tested without starting a process, a timer, or a shared file.
/// </summary>
public sealed class InputLease
{
    /// <summary>
    /// How long the owner may go without a heartbeat before the watchdog assumes it is
    /// gone. Generous enough to survive a garbage collection pause or a slow frame, tight
    /// enough that a genuinely dead process does not leave a key held for long.
    /// </summary>
    public const int DefaultStallTimeoutMs = 2000;

    private readonly object gate = new();

    private long heartbeat;
    private int ownerPid;
    private long generation;
    private bool releaseRequested;
    private readonly HashSet<OwnedInput> held = [];

    public InputLease(int stallTimeoutMs = DefaultStallTimeoutMs)
    {
        if (stallTimeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(stallTimeoutMs));
        StallTimeoutMs = stallTimeoutMs;
    }

    public int StallTimeoutMs { get; }

    /// <summary>
    /// Increments on every owner liveness tick. The watchdog compares successive values to
    /// detect a stall, so a monotonic counter is used rather than a timestamp: it cannot be
    /// moved backwards by a clock change.
    /// </summary>
    public long Heartbeat
    {
        get { lock (gate) return heartbeat; }
    }

    public int OwnerPid
    {
        get { lock (gate) return ownerPid; }
    }

    /// <summary>
    /// Changes when a new owner takes the lease. A watchdog that was already running can
    /// tell a fresh owner apart from the previous one and will not release the new owner's
    /// inputs based on the old owner's stall.
    /// </summary>
    public long Generation
    {
        get { lock (gate) return generation; }
    }

    public int HeldCount
    {
        get { lock (gate) return held.Count; }
    }

    public bool ReleaseRequested
    {
        get { lock (gate) return releaseRequested; }
    }

    /// <summary>
    /// Populates a lease read from another process. Used by the watchdog, which observes
    /// state rather than owning it. The in-process mutation methods stay available so the
    /// same policy code can be exercised against a snapshot in tests.
    /// </summary>
    public void Load(long heartbeat, int ownerPid, long generation, bool releaseRequested,
        IEnumerable<OwnedInput> held)
    {
        lock (gate)
        {
            this.heartbeat = heartbeat;
            this.ownerPid = ownerPid;
            this.generation = generation;
            this.releaseRequested = releaseRequested;
            this.held.Clear();
            foreach (var input in held) this.held.Add(input);
        }
    }

    /// <summary>
    /// Claims the lease for a process. Any inputs recorded by a previous owner are dropped,
    /// because that owner's watchdog has already released them; carrying them forward would
    /// make the new owner responsible for keys it never pressed.
    /// </summary>
    public void Claim(int pid)
    {
        lock (gate)
        {
            ownerPid = pid;
            generation++;
            heartbeat++;
            releaseRequested = false;
            held.Clear();
        }
    }

    /// <summary>Records an input as owned. Must be called before the key is pressed.</summary>
    public void Add(OwnedInput input)
    {
        lock (gate) held.Add(input);
    }

    /// <summary>Removes an input once its key-up has been sent.</summary>
    public void Remove(OwnedInput input)
    {
        lock (gate) held.Remove(input);
    }

    /// <summary>Signals owner liveness. Called from a thread that is never blocked by macro work.</summary>
    public void Pulse()
    {
        lock (gate) heartbeat++;
    }

    /// <summary>Asks the watchdog to release everything on the next tick.</summary>
    public void RequestRelease()
    {
        lock (gate) releaseRequested = true;
    }

    /// <summary>Snapshot of the currently owned inputs.</summary>
    public OwnedInput[] Snapshot()
    {
        lock (gate) return [.. held];
    }

    /// <summary>
    /// Clears the owned set after a release. Called only once the key-ups have actually
    /// been sent, so a failed release leaves the inputs tracked and retried rather than
    /// silently forgotten.
    /// </summary>
    public void ClearHeld()
    {
        lock (gate) held.Clear();
    }
}
