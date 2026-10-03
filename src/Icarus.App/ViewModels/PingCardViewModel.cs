using Icarus.Bench;
using Icarus.Core;

namespace Icarus.App;

/// <summary>
/// Card 3 — Ping Optimizer, Quantum Ping Optimizer.
///
/// Shows the full distribution per region rather than one number, with the propagation
/// floor beside each latency so a physically unreachable target is visible. There is no
/// cumulative counter: only measured current-versus-baseline figures appear.
/// </summary>
public sealed class PingCardViewModel : CardViewModel
{
    public PingCardViewModel()
    {
        Title = "Ping Optimizer";
        Category = "QUANTUM PING OPTIMIZER";
        Body = "Measure latency and jitter to your nearest server, and address what a desktop "
             + "application can affect: Wi-Fi conditions, bufferbloat, uplink saturation and "
             + "background traffic. Distance sets the physical floor, and that floor is shown "
             + "next to every figure.";
        ActionLabel = "TEST ROUTE";
    }

    public override string ActionKind => "PingOptimize";

    private PingCardState state = PingCardState.NotTested;

    /// <summary>
    /// Per-region results. Empty until a route test runs: a region that was not probed has
    /// no entry, so nothing can be shown for it.
    /// </summary>
    public IReadOnlyList<RegionRow> Regions { get; private set; } = [];

    public Measured<double?> BaselineMedianMs { get; private set; } =
        Measured<double?>.Missing("no baseline route test yet");

    public Measured<double?> CurrentMedianMs { get; private set; } =
        Measured<double?>.Missing("no current route test yet");

    public PingCardState State => state;

    public override bool CanRun => true;

    public override string StateWord => state switch
    {
        PingCardState.Testing => "Measuring...",
        PingCardState.Tested => "Route tested",
        _ => "Not tested",
    };

    public override double? Progress => state == PingCardState.Testing ? 0.5 : null;

    /// <summary>
    /// Baseline against current for the nearest probed region. Returns null when there is
    /// not a pair to compare, so the card shows no delta rather than a fabricated one.
    /// </summary>
    public string? DeltaSummary
    {
        get
        {
            if (!BaselineMedianMs.HasValue || !CurrentMedianMs.HasValue) return null;
            double before = BaselineMedianMs.Value!.Value;
            double after = CurrentMedianMs.Value!.Value;
            if (before <= 0) return null;
            double change = (after - before) / before;
            return Math.Abs(change) < 0.01
                ? "No measurable difference between the baseline and current route test."
                : $"Median moved {CardFormat.Ms(before)} to {CardFormat.Ms(after)} ({change:+0.0;-0.0;0}%).";
        }
    }

    public void SetState(PingCardState next)
    {
        state = next;
        OnChanged();
    }

    public void Apply(IReadOnlyList<ProbeResult> results, bool isBaseline, DateTimeOffset when)
    {
        ArgumentNullException.ThrowIfNull(results);

        Regions = results.Select(RegionRow.From).ToArray();

        // Only a verified game endpoint establishes a route baseline. A user-entered host
        // is measured, but it is not the player's in-match path.
        var best = results
            .Where(r => r.VerifiedGameService && r.Tcp.HasSamples)
            .OrderBy(r => r.Tcp.MedianMs ?? double.MaxValue)
            .FirstOrDefault();

        if (best is null)
        {
            var note = results.Count == 0
                ? "no endpoints were probed"
                : "no verified game endpoint answered; start the game and test the route again";
            BaselineMedianMs = Measured<double?>.Missing(note);
            CurrentMedianMs = Measured<double?>.Missing(note);
        }
        else
        {
            var value = best.Tcp.MedianMs;
            var label = $"{best.Label}, floor {CardFormat.Km(best.PathFloorKm ?? 0)}";
            BaselineMedianMs = isBaseline
                ? Measured<double?>.Earlier(value, when, label)
                : Measured<double?>.Now(value, label);
            CurrentMedianMs = isBaseline
                ? Measured<double?>.Missing("not re-tested yet")
                : Measured<double?>.Now(value, label);
        }

        state = PingCardState.Tested;
        OnChanged();
    }

    public override Task RunAsync(CancellationToken token) => Task.CompletedTask;
}

public enum PingCardState { NotTested, Testing, Tested }

/// <summary>
/// One probed region. Carries the full distribution plus the propagation floor, because a
/// single figure is meaningless and an unreachable target needs its distance visible.
/// </summary>
public sealed record RegionRow
{
    public required string Label { get; init; }
    public required bool Verified { get; init; }
    public required string Min { get; init; }
    public required string Median { get; init; }
    public required string P95 { get; init; }
    public required string Jitter { get; init; }
    public required string Loss { get; init; }
    public required string FloorKm { get; init; }
    public required string Provenance { get; init; }
    public required bool HasSamples { get; init; }
    public bool DistanceInconsistent { get; init; }

    public static RegionRow From(ProbeResult r)
    {
        var s = r.Tcp;
        static string Fmt(double? v) => v is null ? "—" : $"{v.Value:0.0} ms";

        return new RegionRow
        {
            Label = r.Label,
            Verified = r.VerifiedGameService,
            HasSamples = s.HasSamples,
            Min = Fmt(s.MinMs),
            Median = Fmt(s.MedianMs),
            P95 = Fmt(s.P95Ms),
            Jitter = Fmt(s.JitterMs),
            Loss = $"{s.LossPercent:0.0}%",
            FloorKm = r.PathFloorKm is double km ? CardFormat.Km(km) : "—",
            Provenance = EndpointCatalog.DescribeProvenance(r),
            DistanceInconsistent = r.DistanceInconsistent,
        };
    }
}
