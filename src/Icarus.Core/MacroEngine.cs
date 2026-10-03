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
public sealed class MacroEngine(IInputOutput output, IClock clock)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly HashSet<InputToken> held = [];
    private readonly object sync = new();
    public int HeldCount { get { lock (sync) return held.Count; } }
    public long CompletedRuns => Interlocked.Read(ref completed);
    private long completed;
    public event Action<string>? Trace;
    private void Press(InputToken input)
    {
        lock (sync)
        {
            if (!held.Add(input)) throw new InvalidOperationException("Input already held; refusing duplicate down.");
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
                try { output.Up(input); held.Remove(input); }
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
