using Icarus.Bench;

namespace Icarus.Tests;

/// <summary>Shared assertion helper so each test file can fail with a specific reason.</summary>
internal static class Check
{
    public static void That(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
    }

    /// <summary>Asserts an action throws, and returns the exception for further inspection.</summary>
    public static T Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T expected) { return expected; }
        throw new Exception(message);
    }
}

/// <summary>Probe runner returning fixed results, so tests never touch the network.</summary>
internal sealed class StubProbeRunner : IProbeRunner
{
    public LatencySummary TcpResult { get; set; } = Statistics.Summarize([], 0);
    public LatencySummary? IcmpSummary { get; set; }
    public string? IcmpReason { get; set; }

    public Task<LatencySummary> TcpAsync(string host, int port, int samples, CancellationToken token)
        => Task.FromResult(TcpResult);

    public Task<(LatencySummary?, string?)> IcmpAsync(string host, int samples, CancellationToken token)
        => Task.FromResult((IcmpSummary, IcmpReason));
}
