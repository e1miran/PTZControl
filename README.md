# PTZControl

A lightweight Windows utility for controlling a UVC/DirectShow camera's **Pan, Tilt, and Zoom** (and Roll, Exposure, Iris, Focus) — the same properties exposed in a webcam driver's "Camera Controls" tab — from the command line or from global hotkeys, with savable camera-scene presets.

No dependencies beyond what ships with Windows and the .NET Framework.

## Features

- Direct control of pan/tilt/zoom/roll/exposure/iris/focus via the `IAMCameraControl` DirectShow interface
- Background hotkey daemon with a system tray icon — no AutoHotkey or third-party tools required
- 8 savable camera-scene presets (pan + tilt + zoom), recalled instantly from `F1`–`F8`
- Step sizes and device selection persist automatically between launches
- One-off CLI commands for scripting, testing, or troubleshooting
- Handles digital pan/tilt correctly: since crop range depends on zoom level, presets always apply zoom first, then pan/tilt

## Requirements

- Windows with a UVC/DirectShow-compatible camera that exposes pan/tilt/zoom controls via its driver

## Usage

### Background hotkey mode (the main way to use it)

```
PTZControl.exe
```
or
```
PTZControl.exe hotkeys [deviceIndex] [panStep] [tiltStep] [zoomStep]
```

Runs silently in the system tray. Right-click the tray icon for **Status**, **List presets**, or **Exit**.

| Hotkey | Action |
|---|---|
| `Ctrl+Alt+Left` / `Right` | Pan |
| `Ctrl+Alt+Up` / `Down` | Tilt |
| `Ctrl+Alt+PageUp` / `PageDown` | Zoom in / out |
| `Ctrl+Alt+Home` | Reset pan/tilt/zoom to defaults |
| `Ctrl+Alt+End` | Show current status (balloon tip) |
| `F1`–`F8` | Recall preset 1–8 |
| `Ctrl+Alt+Shift+F1`–`F8` | Save current pan/tilt/zoom as preset 1–8 |

Pan and Tilt are available only if the camera is **not** fully zoomed out.

Any `deviceIndex`/`panStep`/`tiltStep`/`zoomStep` you pass on the command line is saved automatically and becomes the default the next time you launch with no arguments.

**Autostart with Windows:** put a shortcut to `PTZControl.exe hotkeys` in your Startup folder (`Win+R` → `shell:startup`).

### CLI commands

```
PTZControl.exe list
PTZControl.exe status [deviceIndex]

PTZControl.exe set   <pan|tilt|zoom|roll|exposure|iris|focus> <absoluteValue> [deviceIndex]
PTZControl.exe move  <pan|tilt|zoom|roll|exposure|iris|focus> <delta>         [deviceIndex]
PTZControl.exe reset <pan|tilt|zoom|roll|exposure|iris|focus>                 [deviceIndex]

PTZControl.exe presetsave <1-8> [deviceIndex]
PTZControl.exe presetload <1-8> [deviceIndex]
PTZControl.exe presets

PTZControl.exe config
PTZControl.exe setsteps <deviceIndex> <panStep> <tiltStep> <zoomStep>
```

- `deviceIndex` defaults to `0` (use `list` to see indices if you have more than one camera)
- `move` is relative to the current value; `set` is absolute
- Camera property values must land on the driver's native step grid (see `status` for each property's `step=`) — an arbitrary value between min/max can fail with `0x80070057` (`E_INVALIDARG`) if it doesn't align

## How presets work

Each preset stores pan, tilt, and zoom together. On recall, **zoom is applied first**, then pan, then tilt — because digital pan/tilt crop range depends on the current zoom level, applying zoom first ensures pan/tilt land where you actually set them.

Presets persist to:
```
%APPDATA%\PTZControl\presets.ini
```

Step sizes and device index persist to:
```
%APPDATA%\PTZControl\config.ini
```

## Notes on hotkeys

`F1`–`F8` are registered as global, unmodified hotkeys for preset recall. If those keys are already claimed by something else on your system (media keys, another app's global hotkeys, a laptop's Fn-lock behavior), those presses may not reach whichever app you intended instead. If that's a problem, add a modifier (e.g. `MOD_ALT`) to the `VK_F1`–`VK_F8` registration in `RegisterAll()`.


