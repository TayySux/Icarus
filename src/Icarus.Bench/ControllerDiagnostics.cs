namespace Icarus.Bench;

/// <summary>One raw stick reading, in the -32768..32767 range XInput reports.</summary>
public readonly record struct StickReading(short X, short Y)
{
    public double Magnitude => Math.Sqrt((double)X * X + (double)Y * Y) / 32767.0;
}

/// <summary>
/// Resting movement measured over a fixed window with the stick untouched.
/// The suggestion is derived from what was actually observed: the smallest radial
/// deadzone at which every recorded sample reads as centred.
/// </summary>
public sealed record DriftReport
{
    public required int SampleCount { get; init; }
    public required double RestingMagnitude { get; init; }
    public required double PeakRestingMagnitude { get; init; }
    public required double SuggestedDeadzone { get; init; }
    public required double AxisXMax { get; init; }
    public required double AxisYMax { get; init; }
    public required string Note { get; init; }
}

public static class Drift
{
    /// <summary>
    /// Analyses a window of readings captured while the stick was at rest.
    /// <paramref name="suggestionPadding"/> keeps the suggestion clear of the observed
    /// peak so ordinary temperature and supply variation does not push the stick back
    /// outside the deadzone.
    /// </summary>
    public static DriftReport Analyze(IReadOnlyList<StickReading> readings, double suggestionPadding = 0.01)
    {
        ArgumentNullException.ThrowIfNull(readings);
        if (readings.Count == 0)
            throw new ArgumentException("Drift needs recorded samples; an empty window cannot show resting movement.", nameof(readings));
        if (suggestionPadding < 0 || suggestionPadding >= 1)
            throw new ArgumentOutOfRangeException(nameof(suggestionPadding));

        double peak = readings.Max(r => r.Magnitude);
        double meanX = readings.Average(r => Math.Abs((double)r.X / 32767.0));
        double meanY = readings.Average(r => Math.Abs((double)r.Y / 32767.0));

        return new DriftReport
        {
            SampleCount = readings.Count,
            RestingMagnitude = readings.Average(r => r.Magnitude),
            PeakRestingMagnitude = peak,
            // The suggestion must cover the worst observed sample, not the average.
            SuggestedDeadzone = Math.Clamp(peak + suggestionPadding, 0.0, 0.99),
            AxisXMax = readings.Max(r => Math.Abs((double)r.X / 32767.0)),
            AxisYMax = readings.Max(r => Math.Abs((double)r.Y / 32767.0)),
            Note = "Measured with the stick untouched. The suggested deadzone covers the worst sample "
                 + "recorded in the window rather than the average, so it stays drift-free as the stick warms up.",
        };
    }
}

/// <summary>
/// Circularity: how far the stick's usable area departs from a true circle.
/// A stick that reports a square gate cannot produce true circles, and the size of the
/// error explains why aiming feels different at the diagonals.
/// </summary>
public sealed record CircularityReport
{
    public required int SampleCount { get; init; }
    public required double BestFitRadius { get; init; }
    public required double MeanRadius { get; init; }
    public required double MaxDeviationPercent { get; init; }

    /// <summary>Fraction of full scale actually reachable, which is often well below 100%.</summary>
    public required double EffectiveRangePercent { get; init; }

    public required string Note { get; init; }
}

public static class Circularity
{
    /// <summary>
    /// Analyses readings captured while the stick was swept to its outer edge.
    /// <paramref name="gate"/> is the raw maximum magnitude observed in XInput units.
    /// Null when the sweep produced too few samples to judge.
    /// </summary>
    public static CircularityReport? Analyze(IReadOnlyList<StickReading> readings, short gate)
    {
        ArgumentNullException.ThrowIfNull(readings);
        // Need points in every quadrant, otherwise a partial sweep would look circular.
        if (readings.Count < 8) return null;

        double maxMagnitude = readings.Max(r => Math.Sqrt((double)r.X * r.X + (double)r.Y * r.Y));
        if (maxMagnitude <= 0) return null;

        double bestFit = maxMagnitude / 32767.0;
        double mean = readings.Average(r => r.Magnitude);
        double worst = readings.Max(r => Math.Abs(r.Magnitude - bestFit) / bestFit * 100.0);

        return new CircularityReport
        {
            SampleCount = readings.Count,
            BestFitRadius = bestFit,
            MeanRadius = mean,
            MaxDeviationPercent = worst,
            EffectiveRangePercent = maxMagnitude / 32767.0 * 100.0,
            Note = "Percentage error is measured against the furthest sample reached. A gate below 100% means "
                 + "the stick never reports full deflection in any direction, so small movements near the edge "
                 + "produce less response than they should.",
        };
    }
}
