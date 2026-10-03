namespace Icarus.Core;

/// <summary>
/// The owned-input record that survives a process death.
///
/// Implemented in Icarus.Core and consumed by Icarus.Native so the macro engine can
/// declare its intent to hold an input without referencing any interop.
/// </summary>
public interface IInputOwnership
{
    /// <summary>True when cross-process protection is actually active.</summary>
    /// <remarks>
    /// Callers surface this in the UI. A false value means a killed process can strand a
    /// key, which the user is entitled to know before arming anything.
    /// </remarks>
    bool IsActive { get; }

    /// <summary>Identifies this process as the owner. Call once at startup.</summary>
    void Claim();

    /// <summary>
    /// Records an input as owned. MUST be called before the corresponding key-down.
    ///
    /// The ordering is load-bearing. Pressing first and recording second leaves a window in
    /// which the key is physically down with nothing tracking it, and a process killed in
    /// that window strands the key. Returns false when the record cannot be stored, in
    /// which case the caller must NOT press the key.
    /// </summary>
    bool Record(InputToken input);

    /// <summary>Removes an input once its key-up has been confirmed sent.</summary>
    void Forget(InputToken input);

    /// <summary>
    /// Signals liveness. Called from a dedicated timer thread that macro work never
    /// blocks, because a blocked heartbeat reads as a dead process to the watchdog.
    /// </summary>
    void Pulse();

    /// <summary>Asks the watchdog to release everything, for use during orderly shutdown.</summary>
    void RequestRelease();
}

/// <summary>
/// Pulses the ownership heartbeat on a dedicated thread.
///
/// This must not run on the macro thread. A blocked or delayed heartbeat is
/// indistinguishable from a dead process to the watchdog, so a long macro step would
/// otherwise cause the watchdog to release the keys the macro is legitimately holding.
/// </summary>
public interface IHeartbeat : IDisposable
{
    void Start();
    void Stop();
}

/// <summary>
/// Timer-driven heartbeat.
///
/// Runs on a background task rather than the thread pool's shared workers, so a burst of
/// macro work cannot delay it. The interval is well under the watchdog's stall timeout,
/// which leaves room for several missed beats before a release is triggered.
/// </summary>
public sealed class TimerHeartbeat : IHeartbeat
{
    private readonly IInputOwnership ownership;
    private readonly TimeSpan interval;
    private CancellationTokenSource? cts;
    private Task? loop;

    public TimerHeartbeat(IInputOwnership ownership, int intervalMs = 250)
    {
        this.ownership = ownership ?? throw new ArgumentNullException(nameof(ownership));
        if (intervalMs < 50) throw new ArgumentOutOfRangeException(nameof(intervalMs));
        interval = TimeSpan.FromMilliseconds(intervalMs);
    }

    public void Start()
    {
        if (loop is not null) return;
        cts = new CancellationTokenSource();
        var token = cts.Token;
        loop = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                ownership.Pulse();
                try { await Task.Delay(interval, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }, CancellationToken.None);
    }

    public void Stop()
    {
        cts?.Cancel();
        cts?.Dispose();
        cts = null;
        loop = null;
    }

    public void Dispose() => Stop();
}

/// <summary>
/// An ownership record that goes nowhere.
///
/// Used when the shared channel is unavailable so the engine still runs with managed-only
/// cleanup rather than refusing to start. It is not a silent fallback: callers surface the
/// reduced protection, because without a watchdog a killed process can strand a key.
/// </summary>
public sealed class NullInputOwnership : IInputOwnership
{
    public bool IsActive => false;
    public void Claim() { }
    public bool Record(InputToken input) => true;
    public void Forget(InputToken input) { }
    public void Pulse() { }
    public void RequestRelease() { }
}

/// <summary>Tracks ownership in memory only. No cross-process protection.</summary>
public sealed class InProcessInputOwnership : IInputOwnership
{
    private readonly HashSet<InputToken> held = [];
    private readonly object gate = new();

    public bool IsActive => true;

    public void Claim() { }

    public bool Record(InputToken input)
    {
        lock (gate) return held.Add(input);
    }

    public void Forget(InputToken input)
    {
        lock (gate) held.Remove(input);
    }

    public void Pulse() { }
    public void RequestRelease() { }

    public IReadOnlyCollection<InputToken> Held
    {
        get { lock (gate) return [.. held]; }
    }
}
