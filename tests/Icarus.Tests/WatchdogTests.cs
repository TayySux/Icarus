using Icarus.Core;

namespace Icarus.Tests;

/// <summary>
/// Watchdog release-policy tests.
///
/// These cover the guarantee that a held input is released even when the owning process
/// cannot clean up after itself. The policy is tested as pure logic so every branch is
/// reachable without killing a process or waiting on a real timeout.
/// </summary>
internal static class WatchdogTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> All()
    {
        yield return ("A live owner keeps its inputs held", LiveOwnerHolds);
        yield return ("A dead owner releases everything", DeadOwnerReleases);
        yield return ("A stalled owner releases after the timeout", StallReleases);
        yield return ("A heartbeat inside the timeout keeps inputs", HeartbeatResets);
        yield return ("An explicit request releases immediately", ExplicitRequest);
        yield return ("No owner means nothing to release", NoOwner);
        yield return ("Release is idempotent", IdempotentRelease);
        yield return ("A new owner does not inherit stale inputs", NewOwnerClears);
        yield return ("Duplicate ownership is a set, not a list", OwnershipIsASet);
        yield return ("Clearing only happens after a release succeeds", ClearIsExplicit);
        yield return ("Release covers keyboard and mouse alike", CoversMouseAndMouse);
    }

    static Task LiveOwnerHolds()
    {
        var lease = new InputLease();
        lease.Claim(1234);
        lease.Add(new OwnedInput(false, 30, false));

        // Heartbeat advanced, so the owner is demonstrably alive.
        var decision = WatchdogPolicy.Decide(lease, lastSeenHeartbeat: 1, nowHeartbeat: lease.Heartbeat,
            new FakeProcesses(running: true), new FakeStallClock(0));

        Check.That(decision.Reason == ReleaseReason.None, $"expected no release, got {decision.Reason}");
        Check.That(!decision.ShouldRelease, "should not release");
        Check.That(lease.HeldCount == 1, "input stays owned while owner is alive");
        return Task.CompletedTask;
    }

    static Task DeadOwnerReleases()
    {
        var lease = new InputLease();
        lease.Claim(1234);
        lease.Add(new OwnedInput(false, 30, false));
        lease.Add(new OwnedInput(false, 31, false));

        // Killed process: the heartbeat can never advance again, and the pid is gone.
        var decision = WatchdogPolicy.Decide(lease, lastSeenHeartbeat: 1, nowHeartbeat: lease.Heartbeat,
            new FakeProcesses(running: false), new FakeStallClock(0));

        Check.That(decision.Reason == ReleaseReason.OwnerExited, $"got {decision.Reason}");
        Check.That(decision.ShouldRelease, "a dead owner must release");
        Check.That(decision.ToRelease.Count == 2, "every held input is released");
        Check.That(decision.Explain().Contains("exited"), "reason is explained");

        lease.ClearHeld();
        Check.That(lease.HeldCount == 0, "held set cleared after release");
        return Task.CompletedTask;
    }

    static Task StallReleases()
    {
        var lease = new InputLease(stallTimeoutMs: 2000);
        lease.Claim(1234);
        lease.Add(new OwnedInput(false, 30, false));

        // The process is still running, but the heartbeat has not moved. Only the clock
        // distinguishes "briefly paused" from "hung".
        var decision = WatchdogPolicy.Decide(lease, lastSeenHeartbeat: 1, nowHeartbeat: lease.Heartbeat,
            new FakeProcesses(running: true), new FakeStallClock(2500));

        Check.That(decision.Reason == ReleaseReason.HeartbeatStalled, $"got {decision.Reason}");
        Check.That(decision.ShouldRelease, "a hung owner must not leave a key held");
        return Task.CompletedTask;
    }

    static Task HeartbeatResets()
    {
        var lease = new InputLease(stallTimeoutMs: 2000);
        lease.Claim(1234);
        lease.Add(new OwnedInput(false, 30, false));

        // Stalled for a long time, then the owner pulses. The stall timer must reset,
        // otherwise a macro that pauses mid-sequence would lose its keys.
        var clock = new FakeStallClock(5000);
        lease.Pulse();
        var decision = WatchdogPolicy.Decide(lease, lastSeenHeartbeat: lease.Heartbeat - 1,
            nowHeartbeat: lease.Heartbeat, new FakeProcesses(running: true), clock);

        Check.That(!decision.ShouldRelease, "a fresh heartbeat must prevent release");
        Check.That(lease.HeldCount == 1, "input still held after the heartbeat resumed");
        return Task.CompletedTask;
    }

    static Task ExplicitRequest()
    {
        var lease = new InputLease();
        lease.Claim(1234);
        lease.Add(new OwnedInput(false, 30, false));
        lease.RequestRelease();

        // Even though the process is alive and heartbeating, an explicit request wins.
        var decision = WatchdogPolicy.Decide(lease, lastSeenHeartbeat: 1, nowHeartbeat: lease.Heartbeat,
            new FakeProcesses(running: true), new FakeStallClock(0));

        Check.That(decision.Reason == ReleaseReason.OwnerRequested, $"got {decision.Reason}");
        Check.That(decision.ShouldRelease, "an explicit request must release");
        return Task.CompletedTask;
    }

    static Task NoOwner()
    {
        var lease = new InputLease();
        // No Claim call: nothing owns anything.
        var decision = WatchdogPolicy.Decide(lease, lastSeenHeartbeat: 0, nowHeartbeat: 0,
            new FakeProcesses(running: false), new FakeStallClock(99_000));

        Check.That(!decision.ShouldRelease, "an unowned lease must not release");
        Check.That(decision.Reason == ReleaseReason.None, "no reason to release");
        return Task.CompletedTask;
    }

    static Task IdempotentRelease()
    {
        var lease = new InputLease();
        lease.Claim(99);
        lease.Add(new OwnedInput(false, 30, false));

        var processes = new FakeProcesses(running: false);
        var clock = new FakeStallClock(0);

        var first = WatchdogPolicy.Decide(lease, 1, lease.Heartbeat, processes, clock);
        Check.That(first.ShouldRelease, "first pass releases");

        lease.ClearHeld();
        // After clearing, a second decision must produce nothing. A watchdog that kept
        // releasing would send key-ups for keys the user is holding.
        var second = WatchdogPolicy.Decide(lease, 1, lease.Heartbeat, processes, clock);
        Check.That(!second.ShouldRelease, "a cleared lease must not release again");
        Check.That(second.ToRelease.Count == 0, "nothing left to release");
        return Task.CompletedTask;
    }

    static Task NewOwnerClears()
    {
        var lease = new InputLease();
        lease.Claim(100);
        lease.Add(new OwnedInput(false, 30, false));

        // A new process takes the lease. The previous owner's keys are not its problem.
        lease.Claim(200);
        Check.That(lease.HeldCount == 0, "a new owner must not inherit stale inputs");
        Check.That(lease.OwnerPid == 200, "owner pid updated");
        Check.That(lease.Generation > 1, "generation distinguishes owners");
        return Task.CompletedTask;
    }

    static Task OwnershipIsASet()
    {
        var lease = new InputLease();
        lease.Claim(1);
        var input = new OwnedInput(false, 30, false);
        lease.Add(input);
        lease.Add(input);
        lease.Add(input);

        Check.That(lease.HeldCount == 1, "repeated ownership of one input is still one input");
        return Task.CompletedTask;
    }

    static Task ClearIsExplicit()
    {
        var lease = new InputLease();
        lease.Claim(1);
        lease.Add(new OwnedInput(false, 30, false));

        // A failed release must leave inputs tracked so they can be retried, so clearing is
        // never automatic: it happens only after key-ups are confirmed sent.
        Check.That(lease.HeldCount == 1, "held set survives until release is confirmed");
        lease.Remove(new OwnedInput(false, 30, false));
        Check.That(lease.HeldCount == 0, "an explicit remove drops one input");
        return Task.CompletedTask;
    }

    static Task CoversMouseAndMouse()
    {
        var lease = new InputLease();
        lease.Claim(1);
        lease.Add(new OwnedInput(false, 42, Extended: true));   // extended keyboard key
        lease.Add(new OwnedInput(true, 1, false));               // left mouse button

        var decision = WatchdogPolicy.Decide(lease, 1, lease.Heartbeat,
            new FakeProcesses(running: false), new FakeStallClock(0));

        Check.That(decision.ToRelease.Count == 2, "both keyboard and mouse are released");
        Check.That(decision.ToRelease.Any(i => i.IsMouse), "mouse button included");
        Check.That(decision.ToRelease.Any(i => i.Extended), "extended flag preserved");
        Check.That(decision.ToRelease.First(i => i.Extended).ToToken().Code == 42,
            "scan code survives the round trip to a token");
        return Task.CompletedTask;
    }

    private sealed class FakeProcesses(bool running) : IProcessProbe
    {
        public bool IsRunning(int pid) => running;
    }

    private sealed class FakeStallClock(double elapsed) : IStallClock
    {
        public double ElapsedMs(InputLease lease) => elapsed;
    }
}
