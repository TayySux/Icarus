using Icarus.Bench;

namespace Icarus.Tests;

/// <summary>
/// Phase A3 and A4 tests: decomposed input latency, controller drift, circularity and
/// achieved poll interval.
/// </summary>
internal static class InputLatencyTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> All()
    {
        yield return ("Latency components stay separate and unsummed", ComponentsNotSummed);
        yield return ("Unavailable components carry no value", Unavailable);
        yield return ("Drift suggestion covers the worst resting sample", DriftSuggestion);
        yield return ("Drift refuses an empty window", DriftEmpty);
        yield return ("Circularity measures gate error and range", CircularityMeasure);
        yield return ("Circularity declines a partial sweep", CircularityPartial);
        yield return ("Poll interval reports achieved rate and worst case", PollInterval);
        yield return ("A single interval is not a rate", PollIntervalSingle);
    }

    static Task ComponentsNotSummed()
    {
        var r = InputLatency.Build(
            peripheralPollMs: 8, peripheralEvidence: LatencyEvidence.Measured,
            dpcIsrMs: 0.5, dpcEvidence: LatencyEvidence.Measured,
            presentToPhotonMs: null, presentEvidence: LatencyEvidence.Unavailable,
            controllerPollMs: 1, controllerEvidence: LatencyEvidence.Measured);

        Check.That(r.Available.Count() == 3, "three components measured");
        Check.That(r.Unavailable.Count() == 1, "present-to-photon unavailable");
        Check.That(r.Note.Contains("separately"), "note explains the parts are not summed");

        // The real guarantee is structural: the report type offers no total to display.
        Check.That(typeof(InputLatencyReport).GetProperties().All(p => p.Name != "TotalMs"),
            "report exposes no summed total");
        return Task.CompletedTask;
    }

    static Task Unavailable()
    {
        var r = InputLatency.Build(null, LatencyEvidence.Unavailable, null, LatencyEvidence.Unavailable,
            null, LatencyEvidence.Unavailable, null, LatencyEvidence.Unavailable);
        Check.That(!r.Available.Any(), "nothing available");
        foreach (var c in r.Components)
        {
            Check.That(!c.IsAvailable, $"{c.Name} must not be available");
            Check.That(c.ValueMs is null, $"{c.Name} must not carry a value");
            Check.That(c.Evidence == LatencyEvidence.Unavailable, $"{c.Name} evidence");
            Check.That(c.Explanation.Length > 0, $"{c.Name} explains what it would measure");
        }
        return Task.CompletedTask;
    }

    static Task DriftSuggestion()
    {
        var readings = new List<StickReading>();
        for (int i = 0; i < 100; i++)
            readings.Add(new StickReading((short)(i == 50 ? 983 : 100), (short)(i == 51 ? -900 : 120)));

        var d = Drift.Analyze(readings);
        Check.That(d.SampleCount == 100, "sample count");
        Check.That(d.PeakRestingMagnitude > d.RestingMagnitude, "peak exceeds mean");
        Check.That(d.SuggestedDeadzone > d.PeakRestingMagnitude, "suggestion clears the worst sample");
        Check.That(d.SuggestedDeadzone <= 0.99, "suggestion stays in range");
        Check.That(d.AxisXMax > 0 && d.AxisYMax > 0, "per axis maxima reported");
        return Task.CompletedTask;
    }

    static Task DriftEmpty()
    {
        Check.Throws<ArgumentException>(() => Drift.Analyze([]), "empty drift window accepted");
        return Task.CompletedTask;
    }

    static Task CircularityMeasure()
    {
        var perfect = new List<StickReading>();
        for (int i = 0; i < 16; i++)
        {
            double a = i * Math.PI / 8;
            perfect.Add(new StickReading((short)(Math.Cos(a) * 32000), (short)(Math.Sin(a) * 32000)));
        }
        var r = Circularity.Analyze(perfect, gate: 32767)!;
        Check.That(r.MaxDeviationPercent < 1, $"circle error under 1%, was {r.MaxDeviationPercent}");
        Check.That(r.EffectiveRangePercent > 97, "full range reached");

        // A square gate: the raw reading grows with both axes, so a diagonal push
        // travels further than an axis push. On a square gate XInput reports about
        // 1.27x full scale on the diagonal (32767 * sqrt(2)), which is the signature of
        // a non-circular gate.
        const short axis = 22000;
        var square = new List<StickReading>
        {
            new(axis, 0), new(0, axis), new(-axis, 0), new(0, -axis),
            new(31000, 31000), new(-31000, 31000), new(31000, -31000), new(-31000, -31000),
        };
        var s = Circularity.Analyze(square, gate: 32767)!;
        Check.That(s.MaxDeviationPercent > 30, $"square gate shows large error, was {s.MaxDeviationPercent:F1}%");
        Check.That(s.EffectiveRangePercent > 97, "diagonal reaches beyond full scale");

        // A stick with a uniform gate: error against its own best fit stays small even
        // when the gate is smaller than full scale. This is the distinction that matters
        // for a player, so it must not be conflated with the square-gate case.
        var small = new List<StickReading>();
        for (int i = 0; i < 16; i++)
        {
            double a = i * Math.PI / 8;
            small.Add(new StickReading((short)(Math.Cos(a) * 28000), (short)(Math.Sin(a) * 28000)));
        }
        var u = Circularity.Analyze(small, gate: 32767)!;
        Check.That(u.MaxDeviationPercent < 1, $"uniform gate is circular, was {u.MaxDeviationPercent:F1}%");
        Check.That(u.EffectiveRangePercent is > 80 and < 90,
            $"uniform small gate reports reduced range, was {u.EffectiveRangePercent:F1}%");
        return Task.CompletedTask;
    }

    static Task CircularityPartial()
    {
        Check.That(Circularity.Analyze([new StickReading(1, 1)], 32767) is null, "one sample cannot judge a circle");
        var four = Enumerable.Range(0, 4).Select(i => new StickReading((short)(i * 100), 0)).ToArray();
        Check.That(Circularity.Analyze(four, 32767) is null, "four samples cannot judge a circle");
        return Task.CompletedTask;
    }

    static Task PollInterval()
    {
        // A 1 ms loop that is regularly descheduled for 5 ms.
        var intervals = new List<double>();
        for (int i = 0; i < 98; i++) intervals.Add(1.0);
        intervals.AddRange([5.0, 5.0]);

        var r = PollIntervalMonitor.FromIntervals(intervals, "test")!;
        Check.That(Math.Abs(r.MedianIntervalMs - 1.0) < 1e-9, "median is the requested rate");
        Check.That(Math.Abs(r.DerivedHz!.Value - 1000) < 1e-6, "derived rate");
        Check.That(r.MaxIntervalMs == 5, "worst case recorded");
        Check.That(r.OnePercentWorstMs >= 1, "worst 1% reported");
        Check.That(r.Note.Contains("achieved"), "note separates achieved from requested");
        return Task.CompletedTask;
    }

    static Task PollIntervalSingle()
    {
        Check.That(PollIntervalMonitor.FromIntervals([1.0], "test") is null, "one interval is not a rate");
        Check.That(PollIntervalMonitor.FromIntervals([], "test") is null, "no intervals is not a rate");
        return Task.CompletedTask;
    }
}
