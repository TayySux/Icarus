using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Icarus.Bench;

/// <summary>A probe target. Nothing is measured until a target is actually probed.</summary>
public sealed record ProbeTarget
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required string Host { get; init; }
    public required int Port { get; init; }

    /// <summary>
    /// Great-circle distance in km, when known. Null means unknown, and the UI must not
    /// invent a distance to go with a latency figure.
    /// </summary>
    public double? KnownDistanceKm { get; init; }

    /// <summary>
    /// Set only when this endpoint was verified as an actual game service. An
    /// unverified host is reported as such rather than presented as a game server.
    /// </summary>
    public bool IsVerifiedGameService { get; init; }
}

/// <summary>
/// Performs real ICMP and TCP probes. No value here is modelled or estimated: if a
/// probe does not complete, the result carries an unavailability reason instead.
/// </summary>
public sealed class NetworkProbeRunner : IProbeRunner
{
    private readonly int timeoutMs;

    public NetworkProbeRunner(int timeoutMs = 2000) => this.timeoutMs = timeoutMs;

    public async Task<LatencySummary> TcpAsync(string host, int port, int samples, CancellationToken token)
    {
        var observed = new List<double>(samples);
        for (int i = 0; i < samples; i++)
        {
            token.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            try
            {
                using var client = new TcpClient();
                using var connect = CancellationTokenSource.CreateLinkedTokenSource(token);
                connect.CancelAfter(timeoutMs);
                await client.ConnectAsync(host, port, connect.Token).ConfigureAwait(false);
                sw.Stop();
                if (connect.IsCancellationRequested) continue;
                observed.Add(sw.Elapsed.TotalMilliseconds);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                // Timed out: counted as a lost probe, never as a zero.
            }
            catch (SocketException)
            {
                // Unreachable: counted as a lost probe.
            }
        }
        return Statistics.Summarize(observed, samples);
    }

    public async Task<(LatencySummary? Summary, string? UnavailableReason)> IcmpAsync(string host, int samples, CancellationToken token)
    {
        var observed = new List<double>(samples);
        using var ping = new Ping();
        int timeouts = 0;
        try
        {
            for (int i = 0; i < samples; i++)
            {
                token.ThrowIfCancellationRequested();
                var reply = await ping.SendPingAsync(host, timeoutMs).ConfigureAwait(false);
                switch (reply.Status)
                {
                    case IPStatus.Success:
                        observed.Add(reply.RoundtripTime);
                        break;
                    case IPStatus.TimedOut:
                        timeouts++;
                        break;
                    default:
                        // No response of any kind from this host on this network.
                        return (null, $"ICMP is not answered by {host} ({reply.Status}). "
                                    + "This is common on networks that filter ICMP, and it says nothing about game latency.");
                }
            }
        }
        catch (PingException ex)
        {
            return (null, $"ICMP probe could not run: {ex.Message}");
        }
        catch (PlatformNotSupportedException ex)
        {
            return (null, $"ICMP probe unsupported on this platform: {ex.Message}");
        }

        if (observed.Count == 0 && timeouts == samples)
            return (null, $"All {samples} ICMP probes to {host} timed out. ICMP appears blocked on this network.");

        return (Statistics.Summarize(observed, samples), null);
    }
}

public sealed record ProbeResult
{
    public required string TargetId { get; init; }
    public required string Label { get; init; }
    public required string Host { get; init; }
    public required LatencySummary Tcp { get; init; }
    public required LatencySummary? Icmp { get; init; }
    public required string? IcmpUnavailableReason { get; init; }
    public required double? PathFloorKm { get; init; }
    public required bool VerifiedGameService { get; init; }
    public DateTimeOffset MeasuredAt { get; init; } = DateTimeOffset.Now;

    /// <summary>
    /// True when the measured round trip is faster than propagation allows for the
    /// stated distance, meaning the pairing of distance and endpoint is not sound.
    /// </summary>
    public bool DistanceInconsistent { get; init; }
}

public interface IProbeRunner
{
    Task<LatencySummary> TcpAsync(string host, int port, int samples, CancellationToken token);
    Task<(LatencySummary? Summary, string? UnavailableReason)> IcmpAsync(string host, int samples, CancellationToken token);
}

public sealed class PingProbe
{
    private readonly IProbeRunner runner;

    public PingProbe(IProbeRunner runner) => this.runner = runner;

    /// <summary>
    /// Probes one target. Results are produced per target: a caller must not merge or
    /// substitute figures across regions, and a region that was not probed has no
    /// result to display.
    /// </summary>
    public async Task<ProbeResult> ProbeAsync(ProbeTarget target, int samples, CancellationToken token = default)
    {
        var tcp = await runner.TcpAsync(target.Host, target.Port, samples, token).ConfigureAwait(false);
        var (icmp, icmpReason) = await runner.IcmpAsync(target.Host, samples, token).ConfigureAwait(false);
        var best = tcp.MedianMs ?? icmp?.MedianMs;

        return new ProbeResult
        {
            TargetId = target.Id,
            Label = target.Label,
            Host = target.Host,
            Tcp = tcp,
            Icmp = icmp,
            IcmpUnavailableReason = icmpReason,
            PathFloorKm = Distance.PathFloorKm(best),
            DistanceInconsistent = Distance.IsPhysicallyInconsistent(best, target.KnownDistanceKm),
            VerifiedGameService = target.IsVerifiedGameService,
        };
    }

    /// <summary>
    /// Probes every supplied target, returning one result per target. Targets that were
    /// not probed produce no entry, so the UI cannot show a figure for a region that
    /// was never measured.
    /// </summary>
    public async Task<IReadOnlyList<ProbeResult>> ProbeAllAsync(
        IEnumerable<ProbeTarget> targets, int samples, CancellationToken token = default)
    {
        var results = new List<ProbeResult>();
        foreach (var target in targets)
        {
            token.ThrowIfCancellationRequested();
            results.Add(await ProbeAsync(target, samples, token).ConfigureAwait(false));
        }
        return results;
    }
}
