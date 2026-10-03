using System.Diagnostics;
using System.Runtime.InteropServices;
using Icarus.Core;
using Icarus.Native;

namespace Icarus.Tests;

/// <summary>
/// A real, separate process that claims the lease and holds one key down.
///
/// This exists so the kill test reproduces an actual crash. A thread inside the test
/// runner would be closed down by the runtime during shutdown, which would run cleanup and
/// defeat the entire purpose of the test.
/// </summary>
internal sealed class HelperProcess : IDisposable
{
    private const string ReadyMarker = "HOLDING";

    private readonly Process process;

    private HelperProcess(Process process) => this.process = process;

    public bool IsStillRunning
    {
        get
        {
            try { return !process.HasExited; }
            catch (InvalidOperationException) { return false; }
        }
    }

    public static HelperProcess? TryStart(ushort scanCode)
    {
        // The helper runs as this same test executable, re-entered with a marker argument,
        // so there is no second binary to build or locate.
        var exe = Environment.ProcessPath;
        if (exe is null || !File.Exists(exe)) return null;

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("--hold-key");
        psi.ArgumentList.Add(scanCode.ToString());

        try
        {
            var p = Process.Start(psi);
            return p is null ? null : new HelperProcess(p);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Waits until the helper reports it has pressed and recorded the key.</summary>
    public async Task<bool> WaitForReadyAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (IsStillRunning && process.StandardOutput.Peek() >= 0)
            {
                var line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                if (line is not null && line.Contains(ReadyMarker, StringComparison.Ordinal))
                    return true;
            }
            else if (!IsStillRunning)
            {
                return false;
            }
            await Task.Delay(50).ConfigureAwait(false);
        }
        return false;
    }

    public void Kill()
    {
        try { process.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException) { }
    }

    public bool WaitForExit(TimeSpan timeout) => process.WaitForExit((int)timeout.TotalMilliseconds);

    /// <summary>
    /// Polls the real asynchronous key state until the key is up.
    ///
    /// GetAsyncKeyState reflects the logical state of the keyboard including injected
    /// input, so it observes the actual system-wide effect rather than what either process
    /// believes it sent. Polling rather than asserting once is required because the watchdog
    /// releases on its own tick.
    /// </summary>
    /// <param name="virtualKey">
    /// Virtual key code. GetAsyncKeyState takes a virtual key, not a scancode, so the
    /// helper's scancode is translated before this is called.
    /// </param>
    public static async Task<bool> WaitForKeyReleasedAsync(int virtualKey, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!KeyState.IsDown(virtualKey)) return true;
            await Task.Delay(100).ConfigureAwait(false);
        }
        return !KeyState.IsDown(virtualKey);
    }

    public void Dispose()
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        process.Dispose();
    }
}

/// <summary>Reads real system-wide key state.</summary>
internal static class KeyState
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public InputUnion Data; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public KeybdInput Keyboard; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeybdInput
    {
        public ushort VirtualKey, Scan;
        public uint Flags, Time;
        public nuint ExtraInfo;
    }

    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;

    /// <summary>
    /// True while the key is logically down.
    ///
    /// GetAsyncKeyState reflects the asynchronous state, which includes input injected by
    /// SendInput, and is readable from any thread and any process. GetKeyState is not used:
    /// it reports the calling thread's synchronous queue state, which would say nothing
    /// about input another process injected.
    /// </summary>
    public static bool IsDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    /// <summary>
    /// True when the async state has ever been populated for this key.
    ///
    /// Used by the crash test to distinguish "the key is stuck down" from "this call
    /// cannot observe injected input at all". Without the distinction a false negative
    /// would be reported as a failed safety guarantee.
    /// </summary>
    public static bool HasState(int virtualKey) => GetAsyncKeyState(virtualKey) != -1;

    /// <summary>
    /// Sends a key-up from the test runner itself.
    ///
    /// Used only as a last-resort cleanup so a failing assertion cannot leave the
    /// developer's keyboard stuck. This is not part of the mechanism under test: if this
    /// call is what releases the key, the watchdog failed and the assertion already said so.
    /// </summary>
    public static void ForceRelease(int virtualKey)
    {
        // The scan code for the letter A, so the up event matches the down event sent by
        // the helper, which uses scancode input.
        var inputs = new[]
        {
            new Input
            {
                Type = InputKeyboard,
                Data = new InputUnion
                {
                    Keyboard = new KeybdInput { VirtualKey = (ushort)virtualKey, Flags = KeyEventKeyUp },
                },
            },
        };
        _ = SendInput(1, inputs, Marshal.SizeOf<Input>());
    }
}

/// <summary>
/// Starts and stops the watchdog process for the duration of a test.
/// </summary>
internal sealed class WatchdogHandle : IDisposable
{
    private readonly Process process;

    private WatchdogHandle(Process process) => this.process = process;

    public static WatchdogHandle? TryStart()
    {
        // The watchdog ships beside the app. Locate it relative to the test binary, which
        // is in the same output tree.
        var testDir = AppContext.BaseDirectory;
        var exe = Path.Combine(testDir, "Icarus.Watchdog.exe");
        if (!File.Exists(exe)) return null;

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        try
        {
            var p = Process.Start(psi);
            return p is null ? null : new WatchdogHandle(p);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        process.Dispose();
    }
}

/// <summary>
/// End-to-end test for the crash-proof release guarantee.
///
/// This is the test that matters. The unit tests prove the policy decides correctly given
/// state; this one proves that when a process is actually terminated while holding a key,
/// a separate process sends the key-up. Everything else in the safety story is inference
/// until this passes.
/// </summary>
internal static class WatchdogIntegrationTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> All()
    {
        yield return ("Shared lease survives a writer and a reader", SharedLeaseRoundTrip);
        yield return ("Held inputs survive the shared boundary", HeldInputsRoundTrip);
        yield return ("The lease refuses to overflow rather than dropping entries", LeaseRefusesOverflow);
        yield return ("A killed owner causes a real key-up", KilledOwnerReleasesKey);
    }

    /// <summary>
    /// The owner writes state and a separate reader observes it. This is the primitive the
    /// whole watchdog rests on, so it is verified before anything depends on it.
    /// </summary>
    static Task SharedLeaseRoundTrip()
    {
        using var lease = SharedLease.CreateOrOpen();
        lease.Claim(Environment.ProcessId, generation: 7);

        var observed = lease.Read();
        Check.That(observed.OwnerPid == Environment.ProcessId, "owner pid visible to a reader");
        Check.That(observed.Generation == 7, "generation visible to a reader");
        Check.That(observed.Heartbeat > 0, "heartbeat advanced on claim");
        Check.That(!observed.ReleaseRequested, "release not requested by claiming");
        return Task.CompletedTask;
    }

    static Task HeldInputsRoundTrip()
    {
        using var lease = SharedLease.CreateOrOpen();
        lease.Claim(Environment.ProcessId, generation: 1);

        var extendedKey = new OwnedInput(false, 42, Extended: true);
        var mouse = new OwnedInput(true, 1, false);
        Check.That(lease.Add(extendedKey), "first input accepted");
        Check.That(lease.Add(mouse), "second input accepted");

        var observed = lease.Read();
        Check.That(observed.HeldCount == 2, $"both inputs visible, saw {observed.HeldCount}");

        var snapshot = observed.Snapshot();
        Check.That(snapshot.Contains(extendedKey), "extended flag survives the boundary");
        Check.That(snapshot.Any(i => i.IsMouse && i.Code == 1), "mouse button survives the boundary");

        // Removal must be exact, or a released key would stay tracked and be re-released
        // later, sending a spurious key-up while the user holds that key.
        lease.Remove(extendedKey);
        Check.That(lease.Read().HeldCount == 1, "removal is exact");
        lease.Remove(mouse);
        Check.That(lease.Read().HeldCount == 0, "second removal clears the list");
        return Task.CompletedTask;
    }

    /// <summary>
    /// When the owned-input list is full, Add must refuse rather than silently drop. A
    /// dropped entry is a key that is physically down with nothing tracking it, which is
    /// exactly the failure the watchdog exists to prevent.
    /// </summary>
    static Task LeaseRefusesOverflow()
    {
        using var lease = SharedLease.CreateOrOpen();
        lease.Claim(Environment.ProcessId, generation: 1);

        var accepted = 0;
        for (int i = 0; i < SharedLease.MaxOwnedInputs + 10; i++)
        {
            // Distinct scan codes so each is a separate entry.
            if (lease.Add(new OwnedInput(false, (ushort)(i % 255), false))) accepted++;
        }

        Check.That(accepted == SharedLease.MaxOwnedInputs,
            $"exactly the capacity is accepted, got {accepted}");

        // Everything written must still be readable, i.e. nothing was truncated.
        Check.That(lease.Read().HeldCount == SharedLease.MaxOwnedInputs,
            "the full list reads back intact");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Starts a helper process that claims the lease and holds a key down, kills it without
    /// warning, and asserts the watchdog released the key.
    ///
    /// The helper is a real separate process so that killing it is a genuine crash: no
    /// finally block, no Dispose, no graceful shutdown. That is the only way to reproduce
    /// the failure this mechanism exists for.
    /// </summary>
    static async Task KilledOwnerReleasesKey()
    {
        const ushort scanCode = 30;   // the A key, as a scancode
        const int virtualKey = 0x41;  // the same key as a virtual key code

        var helper = HelperProcess.TryStart(scanCode);
        Check.That(helper != null, "helper process could not be started");

        // A watchdog must already be guarding, otherwise the release below would prove
        // nothing. Start one for the duration of this test.
        var watchdog = WatchdogHandle.TryStart();
        Check.That(watchdog != null, "watchdog could not be started");

        try
        {
            var ready = await helper!.WaitForReadyAsync(TimeSpan.FromSeconds(10));
            Check.That(ready, "helper did not reach the pressed state in time");

            // Distinguish "the key is stuck" from "this check cannot see injected input".
            // Without the second, a false negative would be reported as a broken guarantee.
            Check.That(KeyState.HasState(virtualKey),
                "GetAsyncKeyState cannot observe injected input here, so this test cannot judge the watchdog");
            Check.That(KeyState.IsDown(virtualKey), "key should be down while the helper holds it");
            Check.That(helper.IsStillRunning, "helper should still be alive while holding the key");

            // Kill it outright. TerminateProcess runs no cleanup, no finally, no Dispose.
            helper.Kill();
            helper.WaitForExit(TimeSpan.FromSeconds(5));

            var released = await HelperProcess.WaitForKeyReleasedAsync(
                virtualKey, TimeSpan.FromSeconds(20));
            Check.That(released, $"virtual key {virtualKey:X2} was still down after the owner was killed");
        }
        finally
        {
            // Belt and braces: if the assertion above failed the key may still be down,
            // and the test must not leave the user's keyboard stuck because of it.
            KeyState.ForceRelease(virtualKey);
            helper.Dispose();
            watchdog!.Dispose();
        }
    }
}
