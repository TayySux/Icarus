using Icarus.Bench;
using Icarus.Core;

namespace Icarus.App;

/// <summary>
/// Card 2 — FPS Boost, Dynamic Frame Stabilizer.
///
/// The headline figures are frame times from a real capture. There is no baseline until
/// one is taken, and the card says so rather than showing an invented figure. After a
/// change, the comparison reports the measured delta or states plainly that no measurable
/// difference was found.
/// </summary>
public sealed class FrameCardViewModel : CardViewModel
{
    /// <summary>
    /// Relative difference below which a before/after pair is reported as unchanged.
    /// Without a threshold, rounding alone makes every pair look like a result.
    /// </summary>
    public const double NoChangeThreshold = 0.01;

    public FrameCardViewModel()
    {
        Title = "FPS Boost";
        Category = "DYNAMIC FRAME STABILIZER";
        Body = "Improve frame-time consistency. Measures 1% and 0.1% lows with PresentMon, "
             + "names what limited each frame, and applies only the change that addresses "
             + "the measured cause.";
    }

    public override string ActionKind => "FrameStabilize";

    private FrameCardState state = FrameCardState.NoBaseline;

    public Measured<FrameTimeSummary?> Baseline { get; private set; } =
        Measured<FrameTimeSummary?>.Missing("no baseline captured yet");

    public Measured<FrameTimeSummary?> Current { get; private set; } =
        Measured<FrameTimeSummary?>.Missing("no re-measurement yet");

    /// <summary>The comparison between baseline and current, or null when not yet possible.</summary>
    public FrameComparison? Comparison { get; private set; }

    public FrameCardState State => state;

    public override bool CanRun => true;

    public override string StateWord => state switch
    {
        FrameCardState.Capturing => "Measuring...",
        FrameCardState.Ready => "Baseline captured",
        FrameCardState.Comparing => "Re-measuring...",
        _ => "Not measured",
    };

    public override double? Progress => state switch
    {
        FrameCardState.Capturing or FrameCardState.Comparing => 0.5,
        _ => null,
    };

    public void SetState(FrameCardState next)
    {
        state = next;
        OnChanged();
    }

    public void ApplyBaseline(FrameTimeSummary summary, DateTimeOffset when)
    {
        Baseline = Measured<FrameTimeSummary?>.Earlier(summary, when, "PresentMon capture");
        Current = Measured<FrameTimeSummary?>.Missing("not re-measured yet");
        Comparison = null;
        state = FrameCardState.Ready;
        OnChanged();
    }

    public void ApplyCurrent(FrameTimeSummary summary, DateTimeOffset when)
    {
        Current = Measured<FrameTimeSummary?>.Now(summary, "PresentMon capture");
        Comparison = Baseline.Value is { } before ? FrameComparison.Compare(before, summary) : null;
        state = FrameCardState.Ready;
        OnChanged();
    }

    public override Task RunAsync(CancellationToken token) => Task.CompletedTask;
}

public enum FrameCardState { NoBaseline, Capturing, Ready, Comparing }

/// <summary>
/// A before/after comparison of frame times. Reports direction and magnitude only when
/// the difference exceeds the noise threshold; otherwise it says the change made no
/// measurable difference, which is the outcome most tweaks actually produce.
/// </summary>
public sealed record FrameComparison
{
    public required double BaselineOnePercentLowMs { get; init; }
    public required double CurrentOnePercentLowMs { get; init; }
    public required double BaselineMedianMs { get; init; }
    public required double CurrentMedianMs { get; init; }
    public required bool Changed { get; init; }

    /// <summary>Relative change in the 1% low. Negative means the low frame time improved.</summary>
    public required double OnePercentLowRelativeChange { get; init; }

    public required double MedianRelativeChange { get; init; }

    /// <summary>
    /// Plain-language summary. Never claims a win; when the difference is inside the
    /// threshold it states that no measurable change occurred.
    /// </summary>
    public string Summary => !Changed
        ? "No measurable difference in frame times between the baseline and the re-measurement. "
        + "This change did not measurably affect stutter on this machine."
        : $"1% low frame time moved from {BaselineOnePercentLowMs:0.00} ms to {CurrentOnePercentLowMs:0.00} ms "
        + $"({OnePercentLowRelativeChange:+0.0;-0.0;0}%), measured under the same conditions. "
        + "Direction only; this is not a guarantee for other sessions.";

    public static FrameComparison Compare(FrameTimeSummary before, FrameTimeSummary after)
    {
        if (before.OnePercentLowMs is not double b || after.OnePercentLowMs is not double a)
            throw new InvalidOperationException(
                "A comparison needs a 1% low from both captures. Capture a longer baseline so the "
                + "low percentile is meaningful.");

        double lowChange = (a - b) / b;
        double medianChange = after.MedianMs is double am && before.MedianMs is double bm && bm > 0
            ? (am - bm) / bm
            : 0;

        return new FrameComparison
        {
            BaselineOnePercentLowMs = b,
            CurrentOnePercentLowMs = a,
            BaselineMedianMs = before.MedianMs ?? 0,
            CurrentMedianMs = after.MedianMs ?? 0,
            // Both the 1% low and the median must move for a change to be called real.
            Changed = Math.Abs(lowChange) >= FrameCardViewModel.NoChangeThreshold
                   && Math.Abs(medianChange) >= FrameCardViewModel.NoChangeThreshold,
            OnePercentLowRelativeChange = lowChange,
            MedianRelativeChange = medianChange,
        };
    }
}
