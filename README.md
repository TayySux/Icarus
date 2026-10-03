# Icarus

A performance and macro toolkit for Windows 10/11, built around .NET 10 and WPF.

Icarus combines a low-latency input engine, a virtual-controller passthrough layer, a
reversible system-tweak suite, and an in-app benchmark harness in a single desktop app.

> **Status: under active development.** Modules land progressively; see *Roadmap* below.

## Features

### Command Center
One dashboard for engine state, controller status, active tweaks, and the last
benchmark result.

### Macro Engine
- Per-macro toggles with **up to 6 conditions** (must-match) and **up to 6 alternative
  conditions** (any-of).
- Timeline-based sequences of keyboard, mouse, aim, and controller steps with
  adjustable delays.
- Dry-run mode to verify a macro without sending real input.
- Optional focus window requirement (macro only runs while a target window is active).
- All timing driven by a high-resolution timer with spin-wait refinement.

### Controller Passthrough
- Physical controller input is mirrored to a virtual pad while excluding the virtual
  device from the read path, so passthrough never feeds back into itself.
- Macro output is merged on top of the passthrough state.
- **HidHide integration** blocks dual-input risk; if the driver is not detected,
  controller output stays disabled and the UI flags it in red rather than
  pretending it is safe.
- Held inputs are tracked and released on cancel, device disconnect, or window switch,
  so the app never leaves a key stuck down.

### Tweaks
Reversible tweaks across Performance, Input, Network, and Cleanup groups. Each tweak
records the exact previous value, can export that journal to JSON, and restores it
byte-for-byte. A System Restore checkpoint is created before any change is applied.

### Benchmark
Measures and stores, with timestamps:
- FPS and 1% low (PresentMon)
- Ping and jitter (TCP / ICMP)
- DPC and timer jitter
- Controller poll interval

### Setup Assistant
Reads the machine's actual hardware and Fortnite configuration, then recommends
settings and frames limits based on the detected refresh rate. Numbers shown are
measured or read from the system — never invented.

## Safety model

- Every tweak is individually revertible, and the journal of original values can be
  exported before applying changes.
- A System Restore point is created before the first tweak in a session.
- Passthrough output is gated on HidHide being present; dual input is surfaced as a
  hard warning instead of being silently ignored.
- Held-key release is guaranteed on cancel, disconnect, and focus loss.

## Requirements

| Requirement | Version |
| ----------- | ------- |
| .NET SDK | 10.0 |
| OS | Windows 10 / 11 (x64) |
| HidHide *(controller passthrough only)* | latest |
| ViGEmBus *(controller passthrough only)* | latest |

Gameplay behaviour must be validated on your own account. Icarus is a tool for
measuring and configuring your system; how you use it is your responsibility.

## Roadmap

| Phase | Module | Status |
| ----- | ------ | ------ |
| 1 | Core models & serialization | planned |
| 2 | Input engine (hooks, SendInput, timing) | planned |
| 3 | Controller passthrough (ViGEm + HidHide) | planned |
| 4 | Fortnite configuration module | planned |
| 5 | Macro engine & timing | planned |
| 6 | App shell, theme, navigation | planned |
| 7 | Controller page & calibration tools | planned |
| 8 | Tweaks suite | planned |
| 9 | Benchmark harness | planned |
| 10 | Setup Assistant | planned |
| 11 | Polish, packaging, docs | planned |

## Building

```powershell
dotnet build Icarus.sln -c Release
dotnet test  Icarus.sln -c Release
```

## License

All features are free. No paid tiers, no licenses to purchase, and no external links
required to use any feature.
