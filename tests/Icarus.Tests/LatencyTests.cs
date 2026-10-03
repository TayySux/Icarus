using Icarus.Bench;

namespace Icarus.Tests;

/// <summary>
/// Phase A1 and A2 tests: latency statistics, distance floors and frame-time
/// percentiles. The recurring assertion is that a missing measurement stays missing
/// rather than collapsing to zero or being replaced by an estimate.
/// </summary>
internal static class LatencyTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> All()
    {
        yield return ("Percentiles use nearest rank", Percentiles);
        yield return ("Jitter is the mean absolute consecutive delta", Jitter);
        yield return ("No samples yields null for every statistic", NoSamples);
        yield return ("Lost probes count as loss and never as fast", Loss);
        yield return ("Distance floor exposes the propagation limit", DistanceFloor);
        yield return ("Impossible distance pairings are flagged", DistanceConsistency);
        yield return ("Frame percentiles report times not an average rate", FramePercentiles);
        yield return ("Short captures do not get trusted low percentiles", ShortCaptureIsNotTrusted);
        yield return ("CPU or GPU limit is attributed per frame", FrameBoundAttribution);
        yield return ("Frames without busy time are unknown", FrameBoundUnknown);
    }

    static Task Percentiles()
    {
        double[] data = [10, 20, 30, 40, 50, 60, 70, 80, 90, 100];
        Check.That(Statistics.Percentile(data, 50) == 50, "p50 of an even set");
        Check.That(Statistics.Percentile(data, 95) == 100, "p95 of ten samples is the top sample");
        Check.That(Statistics.Percentile(data, 99) == 100, "p99");
        Check.That(Statistics.Percentile(data, 0) == 10, "p0");
        Check.That(Statistics.Median([3d, 1, 2]) == 2, "median of an odd set");
        Check.That(Statistics.Percentile([], 95) is null, "no samples means no value");
        return Task.CompletedTask;
    }

    static Task Jitter()
    {
        Check.That(Math.Abs(Statistics.Jitter([0d, 10, 20, 30])!.Value - 10) < 1e-9, "steady spacing");
        Check.That(Math.Abs(Statistics.Jitter([0d, 10, 0, 10])!.Value - 10) < 1e-9, "alternating samples");
        Check.That(Math.Abs(Statistics.Jitter([5d, 5, 5, 5])!.Value) < 1e-9, "constant series has no jitter");
        Check.That(Statistics.Jitter([7d]) is null, "one sample cannot produce jitter");
        return Task.CompletedTask;
    }

    static Task NoSamples()
    {
        var s = Statistics.Summarize([], sent: 5);
        Check.That(!s.HasSamples, "no samples");
        Check.That(s.MinMs is null && s.MedianMs is null && s.P95Ms is null
                 && s.MaxMs is null && s.JitterMs is null, "every statistic stays null");
        Check.That(s.LossPercent == 100, "all five probes lost");
        return Task.CompletedTask;
    }

    static Task Loss()
    {
        // Four of ten answered. The missing six must not appear as zero.
        var s = Statistics.Summarize([20d, 22, 21, 23], sent: 10);
        Check.That(Math.Abs(s.LossPercent - 60) < 1e-9, "loss percent");
        // Nearest rank over [20,21,22,23] is the second value, 21. Percentiles here use
        // nearest rank rather than interpolating between the middle pair, so every
        // reported percentile is a value that was actually observed.
        Check.That(s.MedianMs == 21, "median of answered samples only");
        Check.That(s.MinMs == 20 && s.MaxMs == 23, "answered samples span the real range");
        Check.That(s.Sent == 10 && s.Received == 4, "counts recorded");
        return Task.CompletedTask;
    }

    static Task DistanceFloor()
    {
        // A 20 ms round trip implies a 4000 km path floor at 200 km/ms in fibre.
        Check.That(Math.Abs(Distance.PathFloorKm(20)!.Value - 4000) < 1e-6, "20 ms floor");
        Check.That(Distance.PathFloorKm(null) is null, "no measurement means no distance");
        Check.That(Distance.PathFloorKm(0) is null, "zero is not a distance");
        return Task.CompletedTask;
    }

    static Task DistanceConsistency()
    {
        // 1200 km cannot be crossed in 5 ms; propagation alone needs 6 ms.
        Check.That(Distance.IsPhysicallyInconsistent(5, 1200), "5 ms to 1200 km is impossible");
        Check.That(!Distance.IsPhysicallyInconsistent(20, 1200), "20 ms to 1200 km is plausible");
        Check.That(!Distance.IsPhysicallyInconsistent(20, null), "unknown distance cannot be contradicted");
        return Task.CompletedTask;
    }

    static Task FramePercentiles()
    {
        // A realistic capture: 1000 frames, 980 at 16.7 ms and 20 hitches at 100 ms.
        // Twenty hitches is 2% of the capture, so the 99th percentile falls inside the
        // hitch group rather than on its boundary. That is the whole point of reporting
        // frame times rather than an average rate: the mean stays 18.3 ms either way.
        var frameTimes = Enumerable.Repeat(16.7, 980).Concat(Enumerable.Repeat(100.0, 20)).ToArray();
        var frames = frameTimes.Select(ms => new FrameSample { MsBetweenPresents = ms }).ToArray();
        var s = FrameStatistics.Summarize(frames);

        Check.That(s.FrameCount == 1000, "frame count");
        Check.That(s.MedianMs == 16.7, "median frame time");
        Check.That(s.OnePercentLowMs == 100, "1% low lands on a hitch");
        Check.That(s.PointOnePercentLowMs == 100, "0.1% low present with enough frames");
        Check.That(s.LowPercentilesReliable, "a long capture is reliable");
        Check.That(s.LowPercentileCaveat is null, "no caveat when the capture is long enough");
        Check.That(Math.Abs(s.OnePercentLowFps!.Value - 10) < 1e-6, "1% low as fps");
        Check.That(Math.Abs(s.MedianFps!.Value - 59.88) < 0.1, "median fps");
        Check.That(s.OnePercentLowFps < s.MedianFps, "1% low fps is always worse than median");
        Check.That(s.MaxMs == 100, "worst frame time");
        return Task.CompletedTask;
    }

    static Task ShortCaptureIsNotTrusted()
    {
        // 100 frames is fewer than one frame per 1% step, so a low percentile from it
        // is a single arbitrary sample and must be flagged rather than presented.
        var frames = Enumerable.Repeat(10.0, 99).Append(100.0)
            .Select(ms => new FrameSample { MsBetweenPresents = ms }).ToArray();
        var s = FrameStatistics.Summarize(frames);

        Check.That(!s.LowPercentilesReliable, "short capture is not reliable");
        Check.That(s.LowPercentileCaveat is not null, "short capture carries a caveat");
        Check.That(s.LowPercentileCaveat!.Contains("indicative"), "caveat says the value is indicative only");
        Check.That(s.PointOnePercentLowMs is null, "0.1% low withheld below 1000 frames");
        Check.That(s.MedianMs == 10, "median is still meaningful on a short capture");
        Check.That(s.MaxMs == 100, "worst frame is still reported");
        return Task.CompletedTask;
    }

    static Task FrameBoundAttribution()
    {
        var gpu = new FrameSample { MsBetweenPresents = 16, CpuBusyMs = 4, GpuBusyMs = 12 };
        var cpu = new FrameSample { MsBetweenPresents = 16, CpuBusyMs = 12, GpuBusyMs = 4 };
        Check.That(gpu.Bound == FrameBound.Gpu, "gpu limited");
        Check.That(cpu.Bound == FrameBound.Cpu, "cpu limited");
        return Task.CompletedTask;
    }

    static Task FrameBoundUnknown()
    {
        var none = new FrameSample { MsBetweenPresents = 16 };
        Check.That(none.Bound == FrameBound.Unknown, "missing busy time is unknown");
        var s = FrameStatistics.Summarize([none]);
        Check.That(s.BoundCounts[FrameBound.Unknown] == 1, "unknown counted as unknown");
        Check.That(!s.BoundCounts.ContainsKey(FrameBound.Cpu), "no cpu claim without data");
        return Task.CompletedTask;
    }
}
