using Icarus.Bench;
using Icarus.Core;

namespace Icarus.App;

/// <summary>
/// Card 1 — Zero Delay, Quantum Delay Engine.
///
/// The brand name is a product label. The card reports what was measured and labels each
/// component with its source. It never presents a single total, because summing the
/// components would imply a composition none of these measurements establishes.
/// </summary>
public sealed class LatencyCardViewModel : CardViewModel
{
    public LatencyCardViewModel()
    {
        Title = "Zero Delay";
        Category = "QUANTUM DELAY ENGINE";
        Body = "Shorten the input path and show what each step costs. Latency is measured and "
             + "decomposed into peripheral polling, OS event delay, frame time and display "
             + "response. These parts are reported separately and never added into one number.";
        ActionLabel = "MEASURE LATENCY";
    }

    public override string ActionKind => "MeasureLatency";

    private LatencyCardState state = LatencyCardState.Idle;

    /// <summary>
    /// The headline figure. Null until a measurement runs, at which point it carries the
    /// median of whatever component was actually measured. It is never a placeholder and
    /// never a target value.
    /// </summary>
    public Measured<double?> Headline { get; private set; } =
        Measured<double?>.Missing("run a measurement to populate this");

    /// <summary>Per-component breakdown, each independently sourced.</summary>
    public IReadOnlyList<Measured<double?>> Components { get; private set; } = [];

    public string HeadlineCaption => Headline.Source switch
    {
        Provenance.Unavailable => "NOT MEASURED",
        _ => Headline.SourceLabel().ToUpperInvariant(),
    };

    public LatencyCardState State => state;

    public override bool CanRun => state == LatencyCardState.Idle || state == LatencyCardState.Complete;

    public override string StateWord => state switch
    {
        LatencyCardState.Running => "Measuring...",
        LatencyCardState.Complete => "Measured",
        _ => "Idle",
    };

    public override double? Progress => state == LatencyCardState.Running ? 0.5 : null;

    /// <summary>
    /// Builds the card from a real report. Any component without a measurement is carried
    /// through as unavailable, with the reason attached, rather than being dropped or
    /// filled with a guess.
    /// </summary>
    public void Apply(InputLatencyReport report, DateTimeOffset when)
    {
        ArgumentNullException.ThrowIfNull(report);

        var sourced = report.Components
            .Select(c => c.ValueMs.HasValue
                ? Measured<double?>.Now(c.ValueMs.Value, c.Name)
                : Measured<double?>.Missing(c.Name + " not measurable here"))
            .ToArray();

        Components = sourced;

        // The headline is the largest component that was actually measured. Naming it
        // this way avoids implying a total while still giving the card a real figure.
        var measured = report.Available
            .Where(c => c.ValueMs.HasValue)
            .OrderByDescending(c => c.ValueMs!.Value)
            .ToArray();

        Headline = measured.Length > 0
            ? Measured<double?>.Now(measured[0].ValueMs!.Value,
                $"largest measured component: {measured[0].Name}. Not a total.")
            : Measured<double?>.Missing("no latency component could be measured on this machine");

        state = LatencyCardState.Complete;
        OnChanged();
    }

    public override Task RunAsync(CancellationToken token) => Task.CompletedTask;

    internal void SetRunning()
    {
        state = LatencyCardState.Running;
        OnChanged();
    }
}

public enum LatencyCardState { Idle, Running, Complete }
