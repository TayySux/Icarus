namespace Icarus.Bench;

/// <summary>How a latency component was obtained, so the UI can label its confidence.</summary>
public enum LatencyEvidence
{
    /// <summary>No measurement path exists on this machine. Nothing is displayed as a value.</summary>
    Unavailable,

    /// <summary>Measured directly by sampling a real signal.</summary>
    Measured,

    /// <summary>Read from a driver or operating system counter.</summary>
    Reported,

    /// <summary>Stated by hardware or vendor specification, not verified here.</summary>
    Specified,
}

public sealed record LatencyComponent
{
    public required string Name { get; init; }
    public required double? ValueMs { get; init; }
    public required LatencyEvidence Evidence { get; init; }
    public required string Explanation { get; init; }
    public bool IsAvailable => ValueMs.HasValue;
}

/// <summary>
/// Input latency split into the parts that are measured independently.
/// These are not summed. A single "total input latency" figure would imply the parts
/// compose cleanly into one figure on one machine, which is not something any of these
/// measurements establishes.
/// </summary>
public sealed record InputLatencyReport
{
    public required IReadOnlyList<LatencyComponent> Components { get; init; }
    public required string Note { get; init; }
    public IEnumerable<LatencyComponent> Available => Components.Where(c => c.IsAvailable);
    public IEnumerable<LatencyComponent> Unavailable => Components.Where(c => !c.IsAvailable);
}

public static class InputLatency
{
    public static InputLatencyReport Build(
        double? peripheralPollMs,
        LatencyEvidence peripheralEvidence,
        double? dpcIsrMs,
        LatencyEvidence dpcEvidence,
        double? presentToPhotonMs,
        LatencyEvidence presentEvidence,
        double? controllerPollMs,
        LatencyEvidence controllerEvidence)
    {
        var components = new List<LatencyComponent>
        {
            new()
            {
                Name = "Peripheral polling interval",
                ValueMs = peripheralPollMs,
                Evidence = peripheralPollMs.HasValue ? peripheralEvidence : LatencyEvidence.Unavailable,
                Explanation = "Time between successive samples the mouse reports its position. A device "
                    + "sampling at 125 Hz reports every 8 ms regardless of how fast the game runs.",
            },
            new()
            {
                Name = "OS event-to-consumer delay (DPC/ISR)",
                ValueMs = dpcIsrMs,
                Evidence = dpcIsrMs.HasValue ? dpcEvidence : LatencyEvidence.Unavailable,
                Explanation = "Delay between a device interrupt arriving and the input stack delivering it to "
                    + "the application. Requires an ETW DPC/ISR trace; not derivable from frame times.",
            },
            new()
            {
                Name = "Present-to-photon",
                ValueMs = presentToPhotonMs,
                Evidence = presentToPhotonMs.HasValue ? presentEvidence : LatencyEvidence.Unavailable,
                Explanation = "Time from the frame being presented to the corresponding photon leaving the "
                    + "display. Needs a supported capture path and display instrumentation; when absent it "
                    + "is reported as unavailable rather than estimated.",
            },
            new()
            {
                Name = "Controller poll interval",
                ValueMs = controllerPollMs,
                Evidence = controllerPollMs.HasValue ? controllerEvidence : LatencyEvidence.Unavailable,
                Explanation = "Measured interval between successive controller state reads. The configured "
                    + "rate is not the achieved rate, so this is timed rather than assumed.",
            },
        };

        return new InputLatencyReport
        {
            Components = components,
            Note = "These components are listed separately on purpose. Adding them would imply a single "
                 + "end-to-end latency figure that this tooling has not established, because the parts "
                 + "overlap and are sampled at different rates.",
        };
    }
}
