using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Icarus.Core;
using Icarus.Native;
using Fg = Icarus.Native.Foreground;

namespace Icarus.App;
public partial class MainWindow : Window
{
    // Cross-process key recovery. The watchdog must be running and holding the shared
    // mapping open BEFORE any macro can press a key, because a memory-mapped file dies with
    // its last handle: an owner that is killed first takes the lease with it and there is
    // nothing left for anyone to read. The starting order in the constructor is load-bearing.
    private readonly WatchdogSupervisor watchdog = new();
    private readonly IInputOwnership ownership;
    private readonly IHeartbeat? heartbeat;
    private readonly MacroEngine live;
    private bool crashRecoveryActive;
    private readonly string profilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Icarus", "profiles", "workbench.json");
    private CancellationTokenSource? running;
    private MacroProfile? activeProfile;
    private HwndSource? source;
    private IntPtr hwnd;
    private bool ready, closing, allowOutput, focusRequired = true, hotkeysReady;
    private readonly DispatcherTimer guard = new() { Interval = TimeSpan.FromMilliseconds(10) };
    public MainWindow()
    {
        InitializeComponent();

        // STARTUP ORDER IS LOAD-BEARING. The watchdog is started first and must hold the
        // shared mapping open before this process claims the lease or presses anything. A
        // memory-mapped file is destroyed when its last handle closes, so if the owner dies
        // first the lease goes with it and the watchdog finds nothing to release. Claiming
        // before the watchdog is up would reintroduce exactly that failure.
        watchdog.Start();
        crashRecoveryActive = watchdog.IsRunning;

        if (crashRecoveryActive)
        {
            try
            {
                ownership = new SharedInputOwnership(SharedLease.CreateOrOpen());
                ownership.Claim();
                heartbeat = new TimerHeartbeat(ownership);
                heartbeat.Start();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Fall back rather than refuse to start, but say so plainly: without a
                // watchdog a killed process can leave a key held.
                ownership = new NullInputOwnership();
                heartbeat = null;
                crashRecoveryActive = false;
            }
        }
        else
        {
            ownership = new NullInputOwnership();
            heartbeat = null;
        }

        live = new MacroEngine(new WindowsInput(), new MonotonicClock(), ownership, heartbeat);
        live.Trace += Log;

        Status.Text = crashRecoveryActive
            ? "DISARMED | crash recovery ACTIVE (watchdog running)"
            : "DISARMED | crash recovery UNAVAILABLE, a killed process could leave a key held. "
              + (watchdog.UnavailableReason ?? "");
        // A delay-only profile is inert by design, not a fabricated detected binding.
        Editor.Text = ProfileCodec.ToJson(new MacroProfile { Name = "My sequence", Steps = [new() { Kind = StepKind.Delay, DurationMs = 100 }] });
        try { if (File.Exists(profilePath)) Editor.Text = ProfileCodec.ToJson(ProfileCodec.FromJson(File.ReadAllText(profilePath))); }
        catch (Exception e) { Log("Profile load failed: " + e.Message); }
        ready = true;
        SourceInitialized += (_, _) => InitializeHotkeys();
        guard.Tick += (_, _) => GuardTick();
        guard.Start();
        Closing += ClosingWindow;
    }
    private void InitializeHotkeys()
    {
        hwnd = new WindowInteropHelper(this).Handle;
        source = HwndSource.FromHwnd(hwnd);
        source?.AddHook(WndProc);
        bool panic = RegisterHotKey(hwnd, 1, 0x4000 | 0x0001 | 0x0002, 0x23);
        bool trigger = RegisterHotKey(hwnd, 2, 0x4000, 0x77);
        hotkeysReady = panic && trigger;
        if (!hotkeysReady)
        {
            UnregisterHotKey(hwnd, 1); UnregisterHotKey(hwnd, 2);
            Armed.IsEnabled = false;
            Log("Hotkey registration failed; live output disabled. Close apps using F8 or Ctrl+Alt+End and restart.");
        }
    }
    private IntPtr WndProc(IntPtr h, int message, IntPtr w, IntPtr l, ref bool handled)
    {
        if (message != 0x0312) return IntPtr.Zero;
        if (w.ToInt32() == 1) { Panic(); handled = true; }
        if (w.ToInt32() == 2) { _ = StartAsync(false); handled = true; }
        return IntPtr.Zero;
    }
    private void GuardTick()
    {
        string foregroundProcess = Fg.ProcessName;
        if (running != null && activeProfile != null && focusRequired && !Matches(foregroundProcess, activeProfile.FocusWindow)) running.Cancel();
        Status.Text = $"{(allowOutput ? "ARMED" : "DISARMED")} | Foreground: {foregroundProcess} | Runs: {live.CompletedRuns} | Held: {live.HeldCount} | FPS / ping / pad: unavailable";
    }
    private static bool Matches(string actual, string expected) => string.Equals(actual, Path.GetFileNameWithoutExtension(expected), StringComparison.OrdinalIgnoreCase);
    private async Task StartAsync(bool preview)
    {
        if (running != null) { Log("A sequence is already running; trigger ignored."); return; }
        if (!preview && (!allowOutput || !hotkeysReady)) return;
        try
        {
            var profile = ProfileCodec.FromJson(Editor.Text);
            if (!preview && focusRequired && (string.IsNullOrWhiteSpace(profile.FocusWindow) || !Matches(Fg.ProcessName, profile.FocusWindow)))
                throw new InvalidOperationException("Foreground process does not match profile focus_window.");
            using var cancel = new CancellationTokenSource();
            running = cancel;
            activeProfile = preview ? null : profile;
            // Preview sends nothing, so it has no keys to protect. NullInputOwnership is
            // explicit rather than omitted: the reduced protection is a deliberate choice
            // for a mode that cannot strand a key, not an oversight.
            var engine = preview
                ? new MacroEngine(new PreviewOutput(Log), new MonotonicClock(), new NullInputOwnership())
                : live;
            bool requireFocus = focusRequired;
            Log($"{(preview ? "Preview" : "Live")} started; minimum hold target {Timing.MinimumHold(profile.MeasuredFps, profile.FallbackHoldMs)} ms. FPS source: {(profile.MeasuredFps is null ? "unavailable; fallback" : "user-supplied measurement")}");
            await Task.Run(() => engine.RunAsync(profile, () => preview || (!cancel.IsCancellationRequested && (!requireFocus || Matches(Fg.ProcessName, profile.FocusWindow))), cancel.Token));
            Log("Sequence completed.");
        }
        catch (OperationCanceledException) { Log("Sequence canceled; release cleanup attempted."); }
        catch (Exception e) { Log("ERROR: " + e.Message); Armed.IsChecked = false; }
        finally { running = null; activeProfile = null; }
    }
    private void Panic()
    {
        running?.Cancel();
        Armed.IsChecked = false;
        try { live.ReleaseAll(); Log("Panic: owned inputs released."); }
        catch (Exception e) { Log("RELEASE ERROR: " + e.Message); }
    }
    private void ArmChanged(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        allowOutput = Armed.IsChecked == true && hotkeysReady;
        if (!allowOutput) running?.Cancel();
        Log(allowOutput ? "Live output armed. F8 triggers the profile." : "Live output disarmed.");
    }
    private void GuardChanged(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        Panic(); focusRequired = FocusOnly.IsChecked == true;
        Log(focusRequired ? "Foreground guard enabled." : "WARNING: foreground guard disabled by user.");
    }
    private void EditorChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) { if (ready) { running?.Cancel(); Armed.IsChecked = false; } }
    private async void Preview_Click(object sender, RoutedEventArgs e) => await StartAsync(true);
    private void Panic_Click(object sender, RoutedEventArgs e) => Panic();
    private void Save_Click(object sender, RoutedEventArgs e) => UiAction(() =>
    {
        var json = ProfileCodec.ToJson(ProfileCodec.FromJson(Editor.Text));
        Directory.CreateDirectory(Path.GetDirectoryName(profilePath)!);
        File.WriteAllText(profilePath + ".tmp", json);
        File.Move(profilePath + ".tmp", profilePath, true);
        Log("Profile saved under AppData/Icarus/profiles.");
    });
    private void Export_Click(object sender, RoutedEventArgs e) => UiAction(() => { ShareCode.Text = ProfileCodec.Export(ProfileCodec.FromJson(Editor.Text)); Log("Profile exported to the share-code field."); });
    private void Import_Click(object sender, RoutedEventArgs e) => UiAction(() => { Panic(); Editor.Text = ProfileCodec.ToJson(ProfileCodec.Import(ShareCode.Text.Trim())); Log("Profile imported; output disarmed."); });
    private void UiAction(Action action) { try { action(); } catch (Exception e) { Log("ERROR: " + e.Message); } }
    private void Log(string text)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (LogBox.Text.Length > 100000) LogBox.Text = LogBox.Text[^50000..];
            LogBox.AppendText($"{DateTimeOffset.Now:HH:mm:ss.fff} {text}{Environment.NewLine}");
            LogBox.ScrollToEnd();
        }));
    }
    private async void ClosingWindow(object? sender, CancelEventArgs e)
    {
        if (closing) return;
        e.Cancel = true;
        Panic();
        while (running != null) await Task.Delay(10);
        try { live.ReleaseAll(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Input release failed"); return; }
        closing = true; guard.Stop();
        UnregisterHotKey(hwnd, 1); UnregisterHotKey(hwnd, 2);
        source?.RemoveHook(WndProc);

        // Orderly shutdown. Managed release above covers the normal case; stopping the
        // heartbeat and asking the watchdog to release is belt and braces for anything the
        // in-process set missed. The supervisor is disposed last so the watchdog outlives
        // the request to release.
        heartbeat?.Stop();
        ownership.RequestRelease();
        heartbeat?.Dispose();
        watchdog.Dispose();
        Close();
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
