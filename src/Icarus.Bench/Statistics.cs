namespace Icarus.Bench;

/// <summary>
/// Summary statistics for a set of round trip times in milliseconds.
/// Averages are deliberately absent: a mean round trip time hides exactly the tail
/// behaviour that a player feels, so only order statistics are reported.
/// </summary>
public sealed record LatencySummary
{
    public required int Sent { get; init; }
    public required int Received { get; init; }
    public required double? MinMs { get; init; }
    public required double? MedianMs { get; init; }
    public required double? P95Ms { get; init; }
    public required double? MaxMs { get; init; }

    /// <summary>RFC 3550 interarrival jitter: mean absolute deviation of consecutive deltas.</summary>
    public required double? JitterMs { get; init; }

    public double LossPercent => Sent <= 0 ? 0 : (Sent - Received) * 100.0 / Sent;
    public bool HasSamples => Received > 0;
}

public static class Statistics
{
    /// <summary>
    /// Nearest-rank percentile over an unsorted sample. Returns null when there are no
    /// samples so that callers cannot accidentally display a zero for "no data".
    /// </summary>
    public static double? Percentile(IEnumerable<double> samples, double percentile)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (percentile is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(percentile));
        var ordered = samples.OrderBy(x => x).ToArray();
        if (ordered.Length == 0) return null;
        int rank = (int)Math.Ceiling(percentile / 100.0 * ordered.Length);
        return ordered[Math.Clamp(rank - 1, 0, ordered.Length - 1)];
    }

    public static double? Median(IEnumerable<double> samples) => Percentile(samples, 50);

    /// <summary>
    /// RFC 3550 interarrival jitter. Uses absolute differences between consecutive
    /// samples, which is the definition the RTP specification uses and the one latency
    /// tooling reports. Null when fewer than two samples exist.
    /// </summary>
    public static double? Jitter(IEnumerable<double> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var values = samples.ToArray();
        if (values.Length < 2) return null;
        double total = 0;
        for (int i = 1; i < values.Length; i++)
            total += Math.Abs(values[i] - values[i - 1]);
        return total / (values.Length - 1);
    }

    public static LatencySummary Summarize(IReadOnlyList<double> samples, int sent)
    {
        if (sent < 0) throw new ArgumentOutOfRangeException(nameof(sent));
        return new LatencySummary
        {
            Sent = sent,
            Received = samples.Count,
            MinMs = samples.Count > 0 ? samples.Min() : null,
            MedianMs = Median(samples),
            P95Ms = Percentile(samples, 95),
            MaxMs = samples.Count > 0 ? samples.Max() : null,
            JitterMs = Jitter(samples),
        };
    }
}
