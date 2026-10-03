using System.Diagnostics;

namespace Icarus.Core;

public readonly record struct InputToken(bool Mouse, ushort Code, bool Extended = false);
public interface IInputOutput
{
    void Down(InputToken input);
    void Up(InputToken input);
    void Move(int x, int y);
}
public interface IClock { Task WaitAsync(int milliseconds, CancellationToken token); }
public sealed class MonotonicClock : IClock
{
    public async Task WaitAsync(int milliseconds, CancellationToken token)
    {
        long start = Stopwatch.GetTimestamp();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            double remaining = milliseconds - Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (remaining <= 0) return;
            // Deliberately no time-critical priority or busy-loop latency claims.
            await Task.Delay(Math.Max(1, (int)Math.Ceiling(remaining)), token).ConfigureAwait(false);
        }
    }
}
public sealed class MacroEngine
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly HashSet<InputToken> held = [];
    private readonly object sync = new();
    private readonly IInputOwnership ownership;
    private readonly IHeartbeat? heartbeat;

    /// <summary>
    /// <paramref name="ownership"/> records every press in the shared lease so a separate
    /// watchdog can release it if this process dies. Ownership is required rather than
    /// optional: an engine that could silently run without it would make cross-process
    /// protection something that quietly vanishes, so callers must pass an explicit
    /// NullInputOwnership to accept reduced protection knowingly.
    ///
    /// <paramref name="heartbeat"/> is pulsed from its own thread. It must not be the macro
    /// thread, because a blocked heartbeat is indistinguishable from a dead process to the
    /// watchdog and would release keys during a legitimately long macro.
    /// </summary>
    public MacroEngine(IInputOutput output, IClock clock, IInputOwnership ownership,
        IHeartbeat? heartbeat = null)
    {
        this.output = output;
        this.clock = clock;
        this.ownership = ownership ?? throw new ArgumentNullException(nameof(ownership));
        this.heartbeat = heartbeat;
    }

    private readonly IInputOutput output;
    private readonly IClock clock;

    public int HeldCount { get { lock (sync) return held.Count; } }
    public long CompletedRuns => Interlocked.Read(ref completed);
    private long completed;
    public event Action<string>? Trace;
    /// <summary>
    /// Presses an input, recording it for cross-process recovery first.
    ///
    /// The order is the whole point. Recording before pressing means there is no instant at
    /// which the key is physically down with nothing tracking it. If this process is killed
    /// in the gap between the two calls, the watchdog still knows to send the key-up.
    /// Pressing first and recording afterwards would leave exactly the stranded-key window
    /// this mechanism exists to close.
    ///
    /// If the record cannot be stored, the press is refused rather than performed: an
    /// untrackable key is worse than a macro that declines to run.
    /// </summary>
    private void Press(InputToken input)
    {
        lock (sync)
        {
            if (!held.Add(input)) throw new InvalidOperationException("Input already held; refusing duplicate down.");
            if (!ownership.Record(input))
            {
                held.Remove(input);
                throw new InvalidOperationException(
                    "Could not record this input for crash recovery, so it was not pressed. "
                    + "Pressing an untrackable key risks leaving it stuck if the process is killed.");
            }
            output.Down(input);
        }
        Trace?.Invoke($"Down {input}");
    }

    private void Release(InputToken input)
    {
        lock (sync)
        {
            if (!held.Contains(input)) throw new InvalidOperationException("Cannot release an input not owned by this run.");
            output.Up(input);
            // Forget only after the key-up is confirmed sent. A failed release must stay
            // tracked so the watchdog, or the next pass, can retry it.
            ownership.Forget(input);
            held.Remove(input);
        }
        Trace?.Invoke($"Up {input}");
    }
    public void ReleaseAll()
    {
        List<Exception> failures = [];
        lock (sync)
        {
            foreach (var input in held.ToArray())
            {
                // Same ordering rule as Release: key-up first, then forget. If the key-up
                // fails the input stays recorded so the watchdog can still recover it.
                try { output.Up(input); ownership.Forget(input); held.Remove(input); }
                catch (Exception e) { failures.Add(e); }
            }
        }
        if (failures.Count > 0) throw new AggregateException("Input release failed; inputs remain tracked. Disarm and resolve the output error.", failures);
    }
    public async Task RunAsync(MacroProfile profile, Func<bool> allowed, CancellationToken token)
    {
        profile.Validate();
        if (!await gate.WaitAsync(0, token).ConfigureAwait(false)) throw new InvalidOperationException("A macro is already running.");
        try
        {
            if (HeldCount != 0) throw new InvalidOperationException("Unreleased inputs remain; output is blocked.");
            int hold = Timing.MinimumHold(profile.MeasuredFps, profile.FallbackHoldMs);
            int gap = Math.Max(hold, profile.GapMs);
            var downAt = new Dictionary<InputToken, long>();
            foreach (var step in profile.Steps)
            {
                token.ThrowIfCancellationRequested();
                if (!allowed()) throw new OperationCanceledException("Foreground safety guard stopped the run.", token);
                var input = step.Kind == StepKind.MouseClick
                    ? new InputToken(true, (ushort)step.MouseButton)
                    : new InputToken(false, step.ScanCode, step.Extended);
                switch (step.Kind)
                {
                    case StepKind.KeyPress:
                    case StepKind.MouseClick:
                        Press(input);
                        await clock.WaitAsync(Math.Max(hold, step.DurationMs), token).ConfigureAwait(false);
                        Release(input);
                        break;
                    case StepKind.KeyDown:
                        Press(input);
                        downAt[input] = Stopwatch.GetTimestamp();
                        await clock.WaitAsync(Math.Max(hold, step.DurationMs), token).ConfigureAwait(false);
                        break;
                    case StepKind.KeyUp:
                        if (downAt.Remove(input, out var since))
                        {
                            int remaining = (int)Math.Ceiling(hold - Stopwatch.GetElapsedTime(since).TotalMilliseconds);
                            if (remaining > 0) await clock.WaitAsync(remaining, token).ConfigureAwait(false);
                        }
                        Release(input);
                        break;
                    case StepKind.MouseMove: output.Move(step.X, step.Y); break;
                    case StepKind.Delay: await clock.WaitAsync(step.DurationMs, token).ConfigureAwait(false); break;
                }
                Trace?.Invoke($"Step {step.Kind}");
                await clock.WaitAsync(gap, token).ConfigureAwait(false);
            }
            Interlocked.Increment(ref completed);
        }
        finally
        {
            try { ReleaseAll(); }
            finally { gate.Release(); }
        }
    }
}
public sealed class PreviewOutput(Action<string> log) : IInputOutput
{
    public void Down(InputToken i) => log($"PREVIEW down {i}");
    public void Up(InputToken i) => log($"PREVIEW up {i}");
    public void Move(int x, int y) => log($"PREVIEW relative move {x}, {y}");
}
