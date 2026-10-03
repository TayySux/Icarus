using System.Diagnostics;

namespace Icarus.Bench;

/// <summary>
/// Achieved poll interval measured by timing consecutive reads of controller state.
/// The configured rate is not the achieved rate: a loop that asks every millisecond
/// still completes late under load, and that lateness is what shows up as input lag.
/// </summary>
public sealed record PollIntervalReport
{
    public required int SampleCount { get; init; }
    public required double MedianIntervalMs { get; init; }
    public required double? P95IntervalMs { get; init; }

    /// <summary>Worst 1% of intervals: the delay that produces a visible hitch.</summary>
    public required double? OnePercentWorstMs { get; init; }
    public required double? MaxIntervalMs { get; init; }
    public required double? JitterMs { get; init; }
    public required double? DerivedHz { get; init; }
    public required string Note { get; init; }
}

public sealed class PollIntervalMonitor
{
    /// <summary>
    /// Times the gaps between consecutive controller reads. Returns null when fewer
    /// than two intervals were captured, because a single interval is not a rate.
    /// </summary>
    public static PollIntervalReport? FromIntervals(IReadOnlyList<double> intervalMs, string source)
    {
        ArgumentNullException.ThrowIfNull(intervalMs);
        if (intervalMs.Count < 2) return null;

        var median = Statistics.Median(intervalMs);

        return new PollIntervalReport
        {
            SampleCount = intervalMs.Count,
            MedianIntervalMs = median ?? 0,
            P95IntervalMs = Statistics.Percentile(intervalMs, 95),
            OnePercentWorstMs = Statistics.Percentile(intervalMs, 99),
            MaxIntervalMs = intervalMs.Max(),
            JitterMs = Statistics.Jitter(intervalMs),
            DerivedHz = median is > 0 ? 1000.0 / median.Value : null,
            Note = $"Measured from {source} by timing successive reads. This is the rate actually achieved, "
                 + "which can differ from the requested interval when the process is descheduled or the "
                 + "device driver batches reports.",
        };
    }

    /// <summary>
    /// Measures gaps while <paramref name="read"/> samples controller state. The read
    /// is called as fast as the loop allows, so the resulting interval reflects the
    /// real cost of a poll on this machine.
    /// </summary>
    public static PollIntervalReport? Measure(Func<int> read, TimeSpan duration, string source)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));

        var intervals = new List<double>();
        long previous = 0;
        long end = Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency);

        while (Stopwatch.GetTimestamp() < end)
        {
            read();
            long now = Stopwatch.GetTimestamp();
            if (previous != 0)
            {
                double ms = Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds;
                // Ignore the first interval, which includes loop setup.
                if (ms > 0) intervals.Add(ms);
            }
            previous = now;
        }

        return FromIntervals(intervals, source);
    }
}
