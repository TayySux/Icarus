using Icarus.Bench;

namespace Icarus.Tests;

/// <summary>
/// Probe orchestration tests. These cover the rule that a region which was not probed
/// produces no figure at all, and that a blocked ICMP path degrades to a stated reason
/// rather than a fabricated latency.
/// </summary>
internal static class ProbeTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> All()
    {
        yield return ("Unverified endpoints are not presented as game servers", Provenance);
        yield return ("Blocked ICMP yields a reason instead of a latency", BlockedIcmp);
        yield return ("Unprobed regions produce no result at all", UnprobedRegion);
        yield return ("Observed game connections are the verified source", ObservedSource);
    }

    static Task Provenance()
    {
        var user = EndpointCatalog.FromUserEntry("example.net", 443, "My host");
        Check.That(!user.IsVerifiedGameService, "user entry is not verified");

        var described = EndpointCatalog.DescribeProvenance(new ProbeResult
        {
            TargetId = user.Id,
            Label = user.Label,
            Host = user.Host,
            Tcp = Statistics.Summarize([10d, 11, 12], 3),
            Icmp = null,
            IcmpUnavailableReason = null,
            PathFloorKm = null,
            VerifiedGameService = false,
        });
        Check.That(described.Contains("not confirmed"), "unverified endpoint is labelled as such");
        Check.That(!described.Contains("Epic"), "no vendor authority is implied");
        return Task.CompletedTask;
    }

    static async Task BlockedIcmp()
    {
        // A network that answers TCP but silently drops ICMP, which is common.
        var runner = new StubProbeRunner
        {
            TcpResult = Statistics.Summarize([20d, 21, 22], 3),
            IcmpSummary = null,
            IcmpReason = "All 3 ICMP probes to host timed out. ICMP appears blocked on this network.",
        };

        var result = await new PingProbe(runner).ProbeAsync(
            EndpointCatalog.FromObservedConnection("198.51.100.5", 9999, "Game"), samples: 3);

        Check.That(result.Icmp is null, "no icmp figure when blocked");
        Check.That(result.IcmpUnavailableReason is not null, "reason recorded");
        Check.That(result.Tcp.MedianMs == 21, "tcp still measured");
        Check.That(result.PathFloorMsFloorIsSane(), "distance floor derived from tcp");
    }

    static async Task UnprobedRegion()
    {
        var runner = new StubProbeRunner { TcpResult = Statistics.Summarize([15d], 1) };
        var results = await new PingProbe(runner).ProbeAllAsync(
            [EndpointCatalog.FromUserEntry("a.example", 443, "A")], samples: 1);

        Check.That(results.Count == 1, "one result for one probed target");
        // A region never probed yields no entry, so there is nothing for the UI to show.
        Check.That(results.All(r => !r.TargetId.Contains("b.example")), "unprobed region has no result");
    }

    static Task ObservedSource()
    {
        var t = EndpointCatalog.FromObservedConnection("203.0.113.10", 7777, "FortniteClient-Win64-Shipping");
        Check.That(t.IsVerifiedGameService, "observed connection is game relevant");
        Check.That(t.Label.Contains("FortniteClient"), "label names the process");
        Check.That(t.KnownDistanceKm is null, "distance stays unknown rather than guessed");
        Check.That(EndpointCatalog.DescribeProvenance(new ProbeResult
        {
            TargetId = t.Id, Label = t.Label, Host = t.Host,
            Tcp = Statistics.Summarize([10d], 1), Icmp = null, IcmpUnavailableReason = null,
            PathFloorKm = null, VerifiedGameService = true,
        }).Contains("observed"), "observed endpoints state their provenance");
        return Task.CompletedTask;
    }
}

internal static class ProbeResultExtensions
{
    /// <summary>
    /// The distance floor must be derived from a real measurement: 21 ms implies a
    /// 4200 km floor. Guards against a floor being produced from a missing value.
    /// </summary>
    public static bool PathFloorMsFloorIsSane(this ProbeResult result)
        => result.PathFloorKm is > 4000 and <= 4400;
}
