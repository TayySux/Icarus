# Icarus

Private Windows input workbench. **Development increment 0.2: measurement layer complete; tweaks, diagnostics and the remaining pages not yet built.** Source committed; Windows build, hardware behavior, and Fortnite compatibility require verification. Do not infer passing tests or working game integration from this README.

## Implemented in this increment

- WPF dark graphite workbench with editable JSON sequences and timestamped event timeline.
- Scan-code keyboard input, relative mouse movement, five mouse buttons through SendInput.
- Steps: key press/down/up, mouse click/move, delay.
- Explicit arm switch; F8 trigger with no-repeat registration; Ctrl+Alt+End panic.
- Foreground process guard, cancellation on focus loss, owned-input release in cleanup; profile edits and imports disarm output.
- Minimum hold scheduling target: ceiling(1500 / FPS) ms when a user-supplied measured FPS exists; otherwise a clearly labeled configurable fallback. Dependent-step gaps are at least this target. This is not a game acceptance guarantee.
- Preview runs through a logging output backend, never SendInput.
- Validated snake_case JSON and Base64 sharing; atomic local profile replacement.
- Radial deadzone/response math and deterministic test executable.
- Windows GitHub Actions build, test and self-contained publish workflow.
- **`Icarus.Bench` measurement layer:** latency statistics and propagation floors, real
  ICMP/TCP probes with loss and jitter, frame-time percentiles with per-frame CPU/GPU
  attribution, decomposed input latency, controller drift, circularity and achieved poll
  interval. See *Measurement layer* below.

## Measurement layer (Phase A)

`Icarus.Bench` owns every number the product displays. It contains no estimates and no
placeholders: where a measurement cannot be taken, the result carries an unavailability
reason instead of a value.

**Ping and jitter** — real ICMP and TCP probes. Reports minimum, median, p95, RFC 3550
interarrival jitter, and packet loss. A single average latency is never reported,
because a mean hides the tail that is actually felt. `Distance.PathFloorKm` derives the
propagation floor from the measured round trip so an unreachable target is visible as
such: at 200 km/ms in fibre, 20 ms implies a 4000 km path, and a server 1200 km away
cannot be reached faster than 6 ms. `Distance.IsPhysicallyInconsistent` flags any
distance/latency pairing that violates propagation.

Probe targets are never assumed. Epic does not publish a fixed list of regional game
hostnames, and Fortnite assigns a host per match, so `EndpointCatalog` accepts targets
only from a live observed game connection (verified) or from explicit user entry
(clearly labelled as not confirmed). A region that was not probed produces no result
object, so there is nothing for the UI to display.

**Frame time** — `MsBetweenPresents` per frame, reported as median, 1% low and 0.1% low
frame *times*. Average FPS is not produced, since averaging is what hides stutter.
`FrameBound` attributes each frame to CPU or GPU from PresentMon's `CPUBusy`/`GPUBusy`;
a frame lacking both is `Unknown` rather than guessed. `LowPercentilesReliable` requires
200 frames for a 1% low and withholds the 0.1% low below 1000 frames, because a
percentile from too few samples is a single arbitrary value wearing a percentile's
authority.

**Input latency** — four components measured and labelled independently: peripheral poll
interval, OS DPC/ISR delay, present-to-photon, and controller poll interval. Each carries
its own `LatencyEvidence` (`Measured`, `Reported`, `Specified`, `Unavailable`). They are
deliberately **not summed**: `InputLatencyReport` exposes no total, and a test asserts
that no such property exists, because a single end-to-end figure would imply a
composition that none of these measurements establishes.

**Controller diagnostics** — `Drift` measures resting movement over a recorded window and
suggests the smallest radial deadzone that covers the *worst* observed sample rather
than the mean, so it stays drift-free as the stick warms. `Circularity` reports percentage
error against the stick's own best-fit radius plus the effective range, distinguishing a
non-circular gate from one that is merely small, and returns null for a partial sweep
rather than judging from too few quadrants. `PollIntervalMonitor` times successive reads
to report the *achieved* rate, its jitter and its worst 1% — never the requested rate by
assertion.

## Claim policy

Marketing-style performance guarantees are not permitted anywhere in this project. That
covers absolute-latency claims, claims that a setting removes network delay entirely,
and claims that input cannot be detected. `LanguageGuardTests` enforces the ban as a
build step rather than a review convention, scanning source, markup and documentation
case-insensitively across whole words, so such a claim cannot reappear in a string,
tooltip or document without failing the test run.

The guard is itself tested against a table of violating and non-violating samples
(`GuardDetectsViolations`), because a check that cannot fail proves nothing. Wording that
merely describes a measurement is unaffected: reporting that a change made no measurable
difference, or naming a measured latency in milliseconds, passes.

## Build

Requires Windows 10/11 x64 and .NET 10 SDK. From the repository root:

```powershell
dotnet build Icarus.slnx -c Release
dotnet run --project tests/Icarus.Tests -c Release --no-build
dotnet publish src/Icarus.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

The tests are a dependency-free console harness, not a `dotnet test` adapter. A failed assertion exits nonzero. CI artifacts are produced only if build and tests succeed. Their presence is not a hardware acceptance test.

## First use

The initial profile contains only a delay. No edit/build/select/confirm bindings are guessed. Start in preview mode, edit the profile, save, and verify every event before arming. The only trigger implemented is F8, once per physical hotkey press. Hotkeys are global and reserved while the application runs. If either registration fails, live output is disabled.

`focus_window` defaults to `FortniteClient-Win64-Shipping`. For a controlled desktop test, use `notepad` with a nonempty scan-code sequence. Focus that process and press F8. This verifies desktop output only, not game support. KEYEVENTF_SCANCODE values are numeric physical scan codes, not virtual-key values. Set `extended` for extended keys. Mouse buttons: 1 left, 2 right, 3 middle, 4 X1, 5 X2. Mouse movement is relative.

```json
{
  "schema_version": 1,
  "name": "Desktop input check",
  "input_type": "keyboard",
  "key": "F8",
  "focus_window": "notepad",
  "measured_fps": null,
  "fallback_hold_ms": 25,
  "gap_ms": 10,
  "steps": [
    { "kind": "key_press", "scan_code": 30, "duration_ms": 25 }
  ]
}
```

Scan code 30 is a physical key position; resulting text depends on the active keyboard layout. `measured_fps` is manually supplied in this increment, not automatically measured. Scheduling uses a monotonic clock and cooperative waits; Windows may overshoot the target. No sub-millisecond precision or latency claim is made.

Data: `%AppData%/Icarus/profiles/workbench.json`. Export writes the Base64 code into the on-screen share field rather than replacing the clipboard.

## Safety and limitations

Epic's published Fortnite policy says macros are cheating and warns of bans. This application makes no claim of permission, undetectability, account safety, or anti-cheat compatibility. It does not bypass anti-cheat or game restrictions.

Output is serialized. A duplicate owned key-down fails rather than re-pressing. Cancellation, focus loss and normal close attempt release of all owned keyboard/mouse inputs. Input delivery failures are shown and remaining ownership is retained. The focus guard checks before each step and on a UI timer; it is not an atomic foreground/input transaction. Physical keys concurrently held by the user are not distinguished yet. Preview activity can coexist with physical typing but live sequences should not.

**A process forcibly killed, a power loss, a fatal runtime crash, or an unresponsive UI cannot execute managed cleanup. Crash-proof release is not implemented.** A future separate watchdog is required, and even that cannot guarantee release across OS failure. Closing waits for cancellation cleanup. No controller output exists, so controller release is not claimed.

This increment uses least privilege (`asInvoker`), not administrator execution. No system tweaks, registry changes, driver installation, cache deletion, or network reconfiguration occur. There is nothing to revert outside the saved application profile. Delete `%AppData%/Icarus` after closing the app to remove it.

## Compatibility corrections for the full specification

- ViGEmBus and its client libraries are retired dependencies. Re-evaluate maintenance and distribution before integrating them.
- In normal HidHide mode, applications listed are permitted to see hidden devices. Icarus must be allowed; Fortnite must not be allowed to see the physical pad. Adding Fortnite to the normal allow-list defeats the intended hiding. Inverse-cloak mode has different semantics.
- A 1 ms poll period is a scheduling target, not proof of 1 ms device latency or less-than-1 ms passthrough.
- Minimum hold based on average FPS cannot guarantee capture during frame-time spikes.
- Configuration presence does not prove that all Fortnite binds or account-backed settings can be read or changed there. Unknown settings must require manual confirmation.
- A restore point is not an exact per-value backup. Destructive cache deletion cannot be exactly reverted without preserving the deleted files. Restore-point failure must block a preset requiring it.
- A clean DNS lookup benchmark does not demonstrate lower in-match ping; Nagle changes do not generally benefit UDP traffic. No automatic network-tweak benefit is assumed.
- DPC execution time, timer wake-up jitter, device polling and end-to-end input latency are different measurements and must be labeled separately.

## Not implemented

No controller reading or virtual pad, HidHide integration, input hooks, controller triggers, Fortnite bind detection or edit/build presets, automatic FPS measurement, PresentMon, network probes, tweak transactions, restore points, setup assistant, graphical timeline editor, profile auto-switching, tray lifecycle, controller navigation, font bundling, Mica, or automatic driver setup. The larger requested eight-project architecture is not represented by empty placeholder projects.

Legacy JSON `conditions`, `alt_conditions`, and `toggle_bind` are rejected explicitly. Unknown extension fields are preserved, but this is not full legacy-format compatibility: a real example file is needed to define that contract.

## Verification still required

Run Windows CI, then desktop input acceptance testing. Test panic, alt-tab, repeated F8, errors, held physical keys, shutdown, layout variations. Full v1.0 requires controller hardware and virtual-device identity tests, independent watchdog engineering, real Fortnite integration validation where permitted, transactional tweak recovery tests, and real benchmark instrumentation. No published release should be called v1.0 before those pass.
