# CEC (Consumer Electronics Control)

## What is CEC?

CEC is a feature of the HDMI specification that allows connected devices to communicate with each other over the HDMI bus using a single-wire serial protocol. It enables things like:

- A device powering the TV on when it starts playing
- The TV putting devices into standby when the user switches it off
- Remote controls on one device controlling another
- Automatic input switching

Baird registers on the CEC bus as a **Playback device** with the OSD name `"Baird"`.

---

## Architecture

The implementation spans four files:

| File | Role |
|------|------|
| [Baird/Services/ICecService.cs](../Baird/Services/ICecService.cs) | Service interface |
| [Baird/Services/CecService.cs](../Baird/Services/CecService.cs) | `cec-client` process wrapper |
| [Baird/Services/CecParser.cs](../Baird/Services/CecParser.cs) | Raw CEC packet decoder |
| [Baird/Controls/CecDebugControl.axaml](../Baird/Controls/CecDebugControl.axaml) + [CecDebugViewModel.cs](../Baird/ViewModels/CecDebugViewModel.cs) | Debug UI panel |

### Dependency on `cec-client`

Baird does **not** speak to the CEC hardware directly. Instead it spawns `cec-client` (from **libcec**) as a long-running interactive subprocess and communicates with it over stdin/stdout.

```
Baird ──stdin──► cec-client ──► HDMI/CEC bus
      ◄─stdout──              ◄──
```

`cec-client` is launched with:

```
cec-client -t p -o Baird -d 8
```

| Flag | Meaning |
|------|---------|
| `-t p` | Register as a **Playback** device |
| `-o Baird` | OSD (on-screen display) name broadcast to the TV |
| `-d 8` | Debug level 8 — full traffic logging, needed so we can parse incoming events |

### Startup

`CecService.StartAsync()` is called from `MainView` during application startup, deliberately in the background so a missing or misbehaving `cec-client` binary does not block the UI:

```csharp
_ = _cecService.StartAsync().ContinueWith(t =>
{
    if (t.IsFaulted)
        Console.WriteLine($"[MainView] CEC Service failed to start: ...");
});
```

If `cec-client` is not installed, the service silently does nothing — all commands become no-ops.

### Command sending

Commands are written as text to the process's stdin with a 1-second timeout:

| Method | `cec-client` command sent |
|--------|--------------------------|
| `PowerOnAsync()` | `on 0` |
| `PowerOffAsync()` | `standby 0` |
| `VolumeUpAsync()` | `volup` |
| `VolumeDownAsync()` | `voldown` |
| `ChangeInputToThisDeviceAsync()` | `as` (Active Source) |
| `CycleInputsAsync()` | `tx 10:44:34` then `tx 10:45` |
| `GetPowerStatusAsync()` | `pow 0` |

The address `0` in these commands refers to logical address 0, which is always the TV.

### Output reading and event firing

A background task (`ReadOutputAsync`) continuously reads lines from the process stdout. Each line is run through `CecParser.ParseLine()` to annotate raw CEC traffic with a human-readable interpretation (e.g. `TV (0) -> Broadcast (F): Standby`).

Two typed events are fired based on the parsed output:

| Event | Trigger condition |
|-------|-----------------|
| `TvStandby` | Parsed command contains `"Standby"` |
| `TvPowerOn` | Parsed command contains `"Image View On"`, `"Text View On"`, or `"Report Power Status"` with `": On"` |

All output lines are also emitted via `CommandLogged` for display in the debug UI.

---

## What We Use CEC For

### Auto-pause on TV standby

When the TV goes to standby (`TvStandby` fires), any currently playing video is paused, and a flag `_pausedForCecStandby` is set.

### Auto-resume and input claim on TV power-on

When the TV powers back on (`TvPowerOn` fires):
1. `ChangeInputToThisDeviceAsync()` is called immediately to assert Baird as the active source.
2. If `_pausedForCecStandby` is set, the video is resumed.

### Debug panel

A debug screen (accessible in the app) provides manual buttons for every CEC command and a live scrolling log of all CEC traffic with parsed annotations. This is useful for verifying the HDMI-CEC wiring is working and diagnosing TV compatibility issues.

---

## CecParser

`CecParser.ParseLine()` handles the raw text output from `cec-client`. It matches lines containing `>>` or `<<` with a hex payload (e.g. `TRAFFIC: [581] << 88`) and decodes:

- **Source and destination** logical addresses from the first byte's high/low nibbles
- **Opcode** from the second byte, using a built-in lookup table of ~35 known CEC opcodes
- **Opcode parameters** for specific opcodes:
  - `User Control Pressed (0x44)` — decodes the key code
  - `Report Power Status (0x90)` — decodes On / Standby
  - `Active Source (0x82)` — formats the physical address

Lines that do not match a traffic pattern are returned unchanged.

---

## Known Bugs and Limitations

### `TogglePowerAsync` is not a real toggle

`TogglePowerAsync()` always calls `PowerOnAsync()`. The code comment acknowledges this explicitly. The UI exposes a "Toggle" button which misleadingly always sends a power-on command rather than toggling.

### `GetPowerStatusAsync` never returns the actual status

`GetPowerStatusAsync()` sends `pow 0` and immediately returns the string `"Check Log"`. The real response arrives later on stdout but is never correlated back to the caller. The status bar in the debug panel therefore always displays `Power Status: Check Log`.

### `CycleInputsAsync` logs a failure before succeeding

The method calls `LogCommand(...)` with `success: false` and the message "Not directly supported by cec-client simple commands" before proceeding to send the raw `tx 10:44:34` command that actually does the job. This creates a misleading error entry in the log every time cycle inputs is used.

### `cec-client` stderr is discarded

`RedirectStandardError` is `false`, so any error output from `cec-client` is lost. Startup failures or hardware errors written to stderr are invisible to Baird.

### Physical address format in Active Source is wrong

In `CecParser`, Active Source (0x82) formats the physical address as `bytes[2]:X2}.{bytes[3]:X2}.0.0` (two full bytes). CEC physical addresses are actually four nibbles, so byte 2 contains ports 1 and 2 and byte 3 contains ports 3 and 4. The correct format is:

```
(byte2 >> 4).(byte2 & 0xF).(byte3 >> 4).(byte3 & 0xF)
```

For example, `0x12 0x00` should display as `1.2.0.0`, not `12.00.0.0`.

### Help text in the debug UI names the wrong tool

[CecDebugControl.axaml](../Baird/Controls/CecDebugControl.axaml) displays:

> Requires cec-ctl (v4l-utils)

`cec-ctl` is a different tool (part of `v4l-utils`). Baird actually requires `cec-client` from **libcec** (usually in the `cec-utils` or `libcec` package). The install command on Raspberry Pi OS is:

```
sudo apt install cec-utils
```

### No reconnection if `cec-client` crashes

If the `cec-client` process exits unexpectedly, the background reader exits silently. `TvStandby` and `TvPowerOn` events will stop firing. A subsequent command will attempt to restart the process via `SendCommandAsync → StartAsync`, but there is no active health check or reconnection loop.

### `_pausedForCecStandby` is not thread-safe

The flag is read and written inside `Dispatcher.UIThread.Post` closures but `ChangeInputToThisDeviceAsync()` in the `TvPowerOn` handler is fired outside that closure before `_pausedForCecStandby` is checked. In practice this is unlikely to cause problems, but the ordering guarantee between the fire-and-forget async call and the UIThread.Post is implicit.

---

## Dependencies

- **`cec-client`** from [libcec](https://github.com/Pulse-Eight/libcec) — must be on `PATH`
  - Raspberry Pi OS: `sudo apt install cec-utils`
  - The service starts silently without it and all operations become no-ops
