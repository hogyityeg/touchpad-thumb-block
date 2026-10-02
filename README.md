# touchpad-thumb-block

Disables the left side of a laptop touchpad on Windows, so a resting or brushing left thumb does not move the cursor or cause accidental clicks.

Fingers that **land** in the blocked zone are ignored until they lift. Fingers that land anywhere else work normally across the whole touchpad, including the blocked zone. The TrackPoint and external mice are not affected.

It is a single small tray app with no driver, no installer and no admin rights required.

## Requirements

- Windows 10 or 11
- A **Precision Touchpad** (most modern laptops). You can check in Settings → Bluetooth & devices → Touchpad: if you see options like "Touchpad sensitivity" and three- and four-finger gestures, you have one.

Developed on a Lenovo ThinkPad E14 Gen 7 (Synaptics touchpad).

## Build

No SDK is needed. The C# compiler that ships with Windows (.NET Framework 4.x) is enough:

```powershell
.\build.ps1
```

This produces `TouchpadZone.exe` next to the source.

## Usage

Run `TouchpadZone.exe`. A tray icon appears: a small touchpad outline with the blocked part shown in red.

- **Double-click** the icon to toggle blocking on and off. The icon turns grey when it is off.
- **Right-click** it to pick the zone width (left 20%, 25%, 33%, 40% or 50%) or to exit. The choice is saved to `TouchpadZone.ini` next to the exe.

Command-line options:

| Option | Effect |
| --- | --- |
| `--zone 0.4` | Block the left 40% (any value from 0.05 to 0.95). Overrides the saved setting. |
| `--log` | Write finger and suppression events to `TouchpadZone.log`. |
| `--caps` | Write the detected touchpad's coordinate range to `TouchpadZone.log` and exit. |

### Start at login

Press `Win+R`, run `shell:startup`, and put a shortcut to `TouchpadZone.exe` in that folder.

## How it works

1. **Raw Input.** The app registers for the touchpad's raw HID reports (usage page `0x0D`, usage `0x05`) and reads each finger's ID and X/Y position. Whether a finger is blocked is decided once, from the position where it first touches.
2. **Low-level mouse hook.** Windows turns touchpad contacts into ordinary mouse input. A `WH_MOUSE_LL` hook drops that input while the only fingers on the pad are blocked ones:
   - Cursor movement and scrolling are dropped only while a blocked finger is actually moving. A thumb resting still therefore does not stop a mouse or TrackPoint from working.
   - Physical clicks are dropped when the touchpad itself reports its button pressed with only blocked fingers down.
   - Tap-to-click is dropped when it follows a short touch by a blocked finger.
   - Button releases are always paired with their presses, so no button can get stuck.

## Limitations

- **Windows still sees the thumb.** The app filters the mouse input Windows generates, not the touchpad data itself. With a thumb resting in the zone, a tap with another finger can still be read as a two-finger tap (right-click), and moving that finger can occasionally be read as a scroll. Hiding the thumb completely would take a kernel filter driver.
- **Elevated windows.** While a window running as administrator has focus, Windows does not pass its input through the hook of a non-elevated app. Run `TouchpadZone.exe` as administrator if you need it there too.
- Windows' own **Touchpad sensitivity** setting ("Low sensitivity") works well alongside this app for accidental touches while typing.
