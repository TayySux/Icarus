using System.Diagnostics;
using Icarus.Core;

namespace Icarus.Native;

/// <summary>
/// Starts and supervises the watchdog process.
///
/// The watchdog must be running, and holding its handle to the shared mapping open, BEFORE
/// any input is claimed. A memory-mapped file is destroyed when its last handle closes, so
/// an owner that dies first takes the lease with it and there is nothing left to release.
/// Starting the watchdog lazily, at the moment a release is needed, is too late by
/// definition. See the note on SharedLease.
/// </summary>
public sealed class WatchdogSupervisor : IDisposable
{
    private Process? process;

    /// <summary>
    /// True when a watchdog is running and the lease has cross-process protection.
    /// Surfaced in the UI rather than assumed: without a watchdog a killed process can
    /// leave a key held, and the user is entitled to know that.
    /// </summary>
    public bool IsRunning
    {
        get
        {
            try { return process is not null && !process.HasExited; }
            catch (InvalidOperationException) { return false; }
        }
    }

    /// <summary>Why the watchdog is unavailable, or null when it is running.</summary>
    public string? UnavailableReason { get; private set; }

    /// <summary>
    /// Locates and launches the watchdog. Returns false rather than throwing: an absent
    /// watchdog degrades protection, it does not stop the application from starting.
    /// </summary>
    public bool Start(string? executableDirectory = null)
    {
        string? path = Locate(executableDirectory);
        if (path is null)
        {
            UnavailableReason =
                "Icarus.Watchdog.exe was not found beside the application. Cross-process key release "
                + "is inactive, so force-closing Icarus while a macro is running could leave a key held.";
            return false;
        }

        try
        {
            // No window: the watchdog is a background service with no user interface.
            var info = new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            process = Process.Start(info);
            if (process is null)
            {
                UnavailableReason = "The watchdog process could not be started.";
                return false;
            }

            UnavailableReason = null;
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            UnavailableReason = $"The watchdog process could not be started: {e.Message}";
            process = null;
            return false;
        }
    }

    private static string? Locate(string? directory)
    {
        string root = directory ?? AppContext.BaseDirectory;
        string candidate = Path.Combine(root, "Icarus.Watchdog.exe");
        if (File.Exists(candidate)) return candidate;

        // Fall back to the build output layout, so running from a development tree works.
        string? devRoot = FindRepositoryRoot(root);
        if (devRoot is null) return null;

        foreach (var config in new[] { "Release", "Debug" })
        {
            string path = Path.Combine(devRoot, "src", "Icarus.Watchdog", "bin", config,
                "net10.0-windows", "Icarus.Watchdog.exe");
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static string? FindRepositoryRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src"))
                && Directory.Exists(Path.Combine(dir.FullName, "tests")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    public void Dispose()
    {
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception) { }
        process.Dispose();
        process = null;
    }
}
