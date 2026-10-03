using Icarus.Core;
using Icarus.Native;

namespace Icarus.Watchdog;

/// <summary>
/// Standalone release watchdog.
///
/// This exists because managed cleanup cannot run when a process is killed. If the owning
/// application is terminated, loses power, or hangs, nothing in that process will ever send
/// the key-up for whatever it was holding. This process survives that and does it instead.
///
/// It is deliberately minimal: read the lease, ask the shared policy whether a release is
/// due, send key-ups, clear the list. It contains no macro logic of its own, so it cannot
/// itself malfunction in a way that holds a key down.
/// </summary>
internal static class Program
{
    private const int TickMs = 100;

    private static int Main(string[] args)
    {
        // Optional --once mode runs a single decision pass and exits. Used by the
        // integration test, which needs a deterministic tick rather than a loop.
        bool once = args.Contains("--once", StringComparer.OrdinalIgnoreCase);

        SharedLease shared;
        try
        {
            // Create-or-open, NOT open-existing. The watchdog is started before the app
            // ever claims the lease, so at this point the mapping usually does not exist
            // yet. Opening it exclusively was the original defect: the watchdog printed
            // "no input lease present" and exited, so it was never running when the owner
            // was killed. Holding this handle open for the whole loop is what keeps the
            // mapping alive after the owner's death.
            shared = SharedLease.CreateOrOpen();
        }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine(
                "Icarus watchdog: the input lease belongs to another session or user; refusing to run.");
            return 2;
        }

        using (shared)
        {
            var probe = new SystemProcessProbe();
            var clock = new StallClock();
            long lastSeen = 0;

            do
            {
                var lease = shared.Read();

                var decision = WatchdogPolicy.Decide(lease, lastSeen, lease.Heartbeat, probe, clock);
                lastSeen = lease.Heartbeat;

                if (decision.ShouldRelease)
                    Release(shared, decision);

                if (once) break;
                Thread.Sleep(TickMs);
            }
            while (true);
        }

        return 0;
    }

    /// <summary>
    /// Sends a key-up for every owned input, then clears the list.
    ///
    /// The clear happens only after each key-up is confirmed sent. If one fails, the input
    /// stays tracked so the next tick retries it: a partially released macro is recoverable,
    /// an abandoned one is not.
    /// </summary>
    private static void Release(SharedLease shared, ReleaseDecision decision)
    {
        Console.WriteLine($"Icarus watchdog: {decision.Explain()}");

        var output = new WindowsInput();
        var stillHeld = new List<OwnedInput>();
        int released = 0;

        foreach (var input in decision.ToRelease)
        {
            try
            {
                output.Up(input.ToToken());
                released++;
            }
            catch (Exception e)
            {
                // Keep it tracked so the next tick tries again.
                stillHeld.Add(input);
                Console.Error.WriteLine($"Icarus watchdog: could not release an input: {e.Message}");
            }
        }

        // Rebuild the remaining set rather than clearing unconditionally.
        if (stillHeld.Count == 0)
        {
            shared.ClearHeld();
        }
        else
        {
            foreach (var input in decision.ToRelease)
            {
                if (!stillHeld.Contains(input)) shared.Remove(input);
            }
        }

        if (released > 0)
            Console.WriteLine($"Icarus watchdog: released {released} held input(s).");
    }
}
