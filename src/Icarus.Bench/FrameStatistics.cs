namespace Icarus.Bench;

/// <summary>Which resource limited a frame, attributed from PresentMon CPU/GPU busy times.</summary>
public enum FrameBound { Unknown, Cpu, Gpu }

/// <summary>One captured frame. Times are milliseconds as reported by PresentMon.</summary>
public sealed record FrameSample
{
    public required double MsBetweenPresents { get; init; }
    public double? CpuBusyMs { get; init; }
    public double? GpuBusyMs { get; init; }
    public double? MsUntilDisplayed { get; init; }
    public double? ClickToPhotonMs { get; init; }
    public double? DisplayedTimeMs { get; init; }

    /// <summary>
    /// PresentMon reports CPU and GPU busy time per frame. The larger of the two is
    /// the constraint that frame was waiting on. Unknown when neither was captured,
    /// which is different from asserting a limit that was not observed.
    /// </summary>
    public FrameBound Bound => (CpuBusyMs, GpuBusyMs) switch
    {
        (null, null) => FrameBound.Unknown,
        (null, double g) => FrameBound.Gpu,
        (double c, null) => FrameBound.Cpu,
        (double c, double g) => g > c ? FrameBound.Gpu : FrameBound.Cpu,
    };
}

/// <summary>
/// Frame time distribution. Average FPS is not reported: a mean hides the stutter.
/// The percentiles below are the values that correspond to how a frame rate is felt.
/// </summary>
public sealed record FrameTimeSummary
{
    public required int FrameCount { get; init; }
    public required double? MedianMs { get; init; }

    /// <summary>Frame time at the 99th percentile: the "1% low" expressed as a time.</summary>
    public required double? OnePercentLowMs { get; init; }

    /// <summary>Frame time at the 99.9th percentile.</summary>
    public required double? PointOnePercentLowMs { get; init; }
    public required double? MaxMs { get; init; }
    public required IReadOnlyDictionary<FrameBound, int> BoundCounts { get; init; }
    public required double? MedianDisplayedLatencyMs { get; init; }
    public required double? MedianClickToPhotonMs { get; init; }

    /// <summary>True when the capture was long enough for the low percentiles to mean anything.</summary>
    public required bool LowPercentilesReliable { get; init; }

    public required string? LowPercentileCaveat { get; init; }

    /// <summary>FPS derived from the median frame time. Null when nothing was captured.</summary>
    public double? MedianFps => MedianMs is > 0 ? 1000.0 / MedianMs.Value : null;

    /// <summary>
    /// FPS the display could sustain for the 1% low frame time. Always lower than or
    /// equal to <see cref="MedianFps"/>, which is the point: this is the rate the
    /// player actually sees during the worst moments.
    /// </summary>
    public double? OnePercentLowFps => OnePercentLowMs is > 0 ? 1000.0 / OnePercentLowMs.Value : null;
}

public static class FrameStatistics
{
    /// <summary>
    /// Frames needed before the 1% low is worth reporting. Below this, a capture
    /// contains fewer than one frame per percentile step, so the "1% low" would be a
    /// single arbitrary sample presented with the authority of a percentile.
    /// </summary>
    public const int MinimumFramesForLowPercentiles = 200;

    /// <summary>Frames needed before the 0.1% low carries any information at all.</summary>
    public const int MinimumFramesForPointOnePercent = 1000;

    public static FrameTimeSummary Summarize(IReadOnlyList<FrameSample> frames)
    {
        if (frames.Count == 0)
            return new FrameTimeSummary
            {
                FrameCount = 0,
                MedianMs = null,
                OnePercentLowMs = null,
                PointOnePercentLowMs = null,
                MaxMs = null,
                BoundCounts = new Dictionary<FrameBound, int>(),
                MedianDisplayedLatencyMs = null,
                MedianClickToPhotonMs = null,
                LowPercentilesReliable = false,
                LowPercentileCaveat = "No frames were captured.",
            };

        var frameTimes = frames.Select(f => f.MsBetweenPresents).ToArray();
        var counts = frames.GroupBy(f => f.Bound).ToDictionary(g => g.Key, g => g.Count());
        bool reliable = frames.Count >= MinimumFramesForLowPercentiles;

        return new FrameTimeSummary
        {
            FrameCount = frames.Count,
            MedianMs = Statistics.Median(frameTimes),
            OnePercentLowMs = Statistics.Percentile(frameTimes, 99),
            PointOnePercentLowMs = frames.Count >= MinimumFramesForPointOnePercent
                ? Statistics.Percentile(frameTimes, 99.9)
                : null,
            MaxMs = frameTimes.Max(),
            BoundCounts = counts,
            MedianDisplayedLatencyMs = Statistics.Median(Frames(frames, f => f.MsUntilDisplayed)),
            MedianClickToPhotonMs = Statistics.Median(Frames(frames, f => f.ClickToPhotonMs)),
            LowPercentilesReliable = reliable,
            LowPercentileCaveat = reliable
                ? null
                : $"Captured {frames.Count} frames. The 1% low needs at least "
                + $"{MinimumFramesForLowPercentiles} frames to be meaningful, and the 0.1% low needs "
                + $"{MinimumFramesForPointOnePercent}. Treat the low percentiles from this capture as "
                + "indicative only.",
        };
    }

    /// <summary>
    /// Digs out a per-frame metric, discarding frames where PresentMon did not report
    /// it. Averaging across frames including a substituted zero would understate the
    /// result, so absent frames are excluded rather than filled in.
    /// </summary>
    private static double[] Frames(IReadOnlyList<FrameSample> frames, Func<FrameSample, double?> selector)
        => frames.Select(selector).Where(v => v.HasValue).Select(v => v!.Value).ToArray();
}
