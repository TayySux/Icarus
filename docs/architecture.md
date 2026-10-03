# Architecture

Icarus is split into eight projects so that testable logic never depends on WPF or on
native interop, and so the high-risk native layers are isolated.

```
Icarus.sln
├── src/
│   ├── Icarus.Core      Models, serialization, profiles, settings
│   ├── Icarus.Native    P/Invoke surface (SendInput, hooks, power, timer, focus)
│   ├── Icarus.Input     Hook thread, scancode sender, timing, XInput poller,
│   │                    ViGEm passthrough hub, HidHide integration
│   ├── Icarus.Fortnite  Process detection, GameUserSettings.ini reader,
│   │                    bind mapping, setup-assistant rules
│   ├── Icarus.Tweaks    ITweak implementations, backup journal, restore points
│   ├── Icarus.Bench     PresentMon FPS, ping/jitter, DPC/timer jitter, results
│   └── Icarus.App       WPF MVVM shell, views, view models, theme
└── tests/
    └── Icarus.Tests     Unit tests for every layer above
```

## Dependency direction

```
                 ┌─────────────┐
                 │  Icarus.App │
                 └──────┬──────┘
        ┌───────────┬───┴────┬──────────┬───────────┐
        ▼           ▼        ▼          ▼           ▼
   Icarus.Core  Icarus.Input Icarus   Icarus      Icarus.Bench
                      │      Fortnite Tweaks
                      ▼
               Icarus.Native
```

Rules:

- `Icarus.Core` depends on nothing. It holds models and JSON contracts only.
- `Icarus.Native` is the only project that declares `DllImport` / `LibraryImport`.
  Everything else consumes it through interfaces.
- `Icarus.Input` never references WPF. It raises events and is driven by the app.
- `Icarus.Tweaks` talks to the registry through an interface so tests can supply a
  mock registry and assert exact apply → revert behaviour.
- `Icarus.Tests` references the logic projects, never `Icarus.App`.

## Why `Icarus.Native` is C#

`Icarus.Native` is a managed project whose only job is to own the P/Invoke surface,
using source-generated `LibraryImport` interop. Keeping it managed means the solution
builds with nothing but the .NET SDK — no Visual Studio C++ workload, no MSVC
toolchain, no separate native build step. The project boundary still matters: if a
native component is ever added later, only this project changes.

## Controller passthrough

The design goal is that Fortnite sees exactly one controller, while the macro engine
still observes the real pad.

```
 physical pad ──XInput read──▶ filter (drop virtual device) ──▶ merge ──▶ ViGEm ──▶ Fortnite
                                                     ▲
 macro engine ─────────────────────────────────────┘
```

Three details make this safe:

1. **Self-exclusion.** The virtual pad created by ViGEm is itself visible to XInput.
   The read path identifies and drops it, otherwise passthrough would feed its own
   output back in and create a feedback loop.
2. **HidHide gating.** With both the real and virtual pads visible, Fortnite would see
   two controllers and double the input. If HidHide is not detected, controller output
   is refused and the UI shows a red warning rather than enabling it anyway.
3. **Held-input tracking.** Every key or button sent is recorded. On cancel, device
   disconnect, or window switch, the held set is released — the app never leaves an
   input stuck down.

## Macro timing

Macros are timelines of steps, not sleep chains. Each step has a duration, and the
scheduler targets an absolute deadline per step computed from a high-resolution
timer, with a short spin-wait window at the end of each wait to correct drift. Sleep
granularity on Windows is coarse enough that chained `Sleep` calls accumulate error
over a long macro; absolute deadlines do not.

A minimum hold time is enforced per input step based on the frame rate the game is
running at, so a step is never shorter than a frame the game can observe.

## Tweaks

Each tweak implements:

| Member | Purpose |
| ------ | ------- |
| `Check()` | Report current state as `On` / `Off` / `Unavailable` |
| `Apply()` | Write the new value, recording the exact previous value first |
| `Revert()` | Restore the recorded value |
| `Describe()` | Human-readable explanation of what it changes |

`Apply()` writes the original value into a journal before changing anything, so
`Revert()` is always exact rather than best-effort. The journal is exportable to
JSON, and a System Restore checkpoint is created before the first apply in a session.

## Honesty rules for measurements

Every number the UI displays is measured at runtime or read from the system:

- FPS and 1% low come from PresentMon, not estimates.
- Ping and jitter come from real TCP / ICMP probes.
- The setup assistant derives frame limits from the detected refresh rate.
- Where a value cannot be measured, the UI states that it is unavailable instead of
  showing a placeholder.

If a number cannot be obtained on a given machine, it is shown as unavailable. It is
never faked.
