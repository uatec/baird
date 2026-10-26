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
| `TogglePowerAsync()` | `pow 0`, then `on 0` or `standby 0` |
| `VolumeUpAsync()` | `tx L0:44:41` then `tx L0:45` |
| `VolumeDownAsync()` | `tx L0:44:42` then `tx L0:45` |
| `MuteAsync()` | `tx L0:44:43` then `tx L0:45` |
| `ChangeInputToThisDeviceAsync()` | `as` (Active Source) |
| `CycleInputsAsync()` | `tx L0:44:34` then `tx L0:45` |
| `GetPowerStatusAsync()` | `pow 0`, awaiting the reply for up to 2 s |

The address `0` in these commands refers to logical address 0, which is always the TV. `L` is
Baird's own logical address, learned from the frames cec-client transmits (Playback 1 is usually
taken by another box, so Baird often ends up as Playback 2, `8`).

Volume and mute are sent as *User Control Pressed* keys to the TV rather than cec-client's
`volup`/`voldown`/`mute`, which only address a CEC audio system and do nothing without one.

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

### Remote power and volume keys

The remote is a USB keyboard, so its power and volume buttons arrive as key events (`XF86PowerOff` /
`XF86Sleep` and `XF86Audio*`). `MainView.TryHandleTvKey` intercepts them before any other input
handling — so they work while inputs are blocked or the screensaver is up — and sends them to the
TV: volume up/down/mute as CEC keys, power as `TogglePowerAsync()` (queries the TV, then turns it
off or on). Powering off also pauses playback, as a TV standby does; powering on reclaims the input.
`XF86PowerOff` has no Avalonia `Key`, so it is matched by `PhysicalKey.Power`.

Volume keys pass through a `RemoteKeyGate`: a held key autorepeats at ~25/s but the bus carries
only ~4 key presses/s, and the excess queues in cec-client and keeps changing the volume for
minutes. Repeats are limited to one step per 300 ms and a hold is cut off after 5 s (the display
server can lose a key release and autorepeat forever). Mute sends once per press. The Pi's own
output is pinned to 50% by `baird.service` so there is always some sound.

For the keys to reach Baird, `configure_pi.sh` clears labwc's default bindings for them (Pi volume,
shutdown dialog) in `~/.config/labwc/rc.xml` and tells logind to ignore the power and sleep keys.

For the keys to reach Baird, labwc must be able to parse `~/.config/labwc/rc.xml`: if it isn't
well-formed XML (e.g. `--` inside a comment) labwc ignores the whole file and the Pi's power dialog and
volume keys come back. `configure_pi.sh` checks the file it writes.

### The TV's own remote

When Baird is the input on screen, the TV passes most keys from its own remote on over CEC as *User
Control Pressed* (`0x44`) / *Released* (`0x45`) frames addressed to Baird. `CecService` raises
`UserControlReceived` for each, and `CecRemoteControl` turns them into the key presses Baird already
handles: arrows, OK (Select) → Enter, Exit → BrowserBack, Root Menu → BrowserHome, 0–9, Channel Up/Down →
PageUp/PageDown, EPG → G, play/pause/stop, rewind/fast forward → Left/Right, subtitles → Apps. Power,
volume and mute are left to the TV. Keys are raised through `SyntheticKeyInput`, which also runs
KeyBindings (channel up/down), as Avalonia's keyboard device does. A key with no Released within 550 ms
is treated as let go (CEC 1.4 §13.13). A key from the TV while inputs are blocked unblocks them — the TV
only sends keys to the input it is showing.

**Debugging.** Every key the TV sends is logged:

```
journalctl -t baird -f | grep -E 'CecRemote|Remote-related'
```

- `[CecRemote] TV (0) pressed 'Up' (0x01) → Up.` — received and raised.
- `... sent 'Up' to Playback 1 (4), not Baird — ignored.` — the TV thinks another device is on screen.
- `... pressed 'Red' (0x72) — Baird has no action for it.` — received but unmapped.
- `[CecService] Remote-related frame: ...` — vendor remote commands or Menu Request, which some TVs use
  instead of, or before, User Control.
- Nothing at all — the TV isn't passing keys on (check the TV's CEC/VIERA Link settings). The CEC debug
  panel shows all raw traffic.

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

### Power toggle falls back to "on"

If the TV doesn't answer the power status query within 2 s, `TogglePowerAsync()` turns it on rather
than risk switching off a TV that is in use — so the power key can't turn off a TV that doesn't
reply.

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
