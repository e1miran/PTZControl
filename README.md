# PTZControl

A lightweight Windows utility for controlling a UVC/DirectShow camera's **Pan, Tilt, and Zoom** (and Roll, Exposure, Iris, Focus) — the same properties exposed in a webcam driver's "Camera Controls" tab — from the command line or from global hotkeys, with savable camera-scene presets.

No dependencies beyond what ships with Windows and the .NET Framework. Single `.cs` file, no external libraries.

## Features

- Direct control of pan/tilt/zoom/roll/exposure/iris/focus via the `IAMCameraControl` DirectShow interface
- Background hotkey daemon with a system tray icon — no AutoHotkey or third-party tools required
- 8 savable camera-scene presets (pan + tilt + zoom), recalled instantly from `F1`–`F8` by default
- Fully remappable hotkeys via a plain-text `hotkeys.ini` — no recompiling, with a built-in check for keys already taken by other programs
- Step sizes and device selection persist automatically between launches
- One-off CLI commands for scripting, testing, or troubleshooting
- Handles digital pan/tilt correctly: since crop range depends on zoom level, presets always apply zoom first, then pan/tilt

## Requirements

- Windows with a UVC/DirectShow-compatible camera that exposes pan/tilt/zoom controls via its driver
- `csc.exe`, the C# compiler that ships with .NET Framework — no separate install needed. It's typically found at:
  ```
  C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
  ```

## Building

From a Command Prompt in the folder containing `PTZControl.cs`:

```
csc.exe /target:winexe /out:PTZControl.exe /reference:System.Windows.Forms.dll,System.Drawing.dll PTZControl.cs
```

`/target:winexe` builds it as a Windows GUI-subsystem app so launching the hotkey daemon never flashes a console window. CLI commands still print normally when run from an existing Command Prompt.

### Optional: custom icon

Add an `.ico` file (16×16/32×32/48×48 recommended) next to the source and compile with:

```
csc.exe /target:winexe /win32icon:PTZControl.ico /out:PTZControl.exe /reference:System.Windows.Forms.dll,System.Drawing.dll PTZControl.cs
```

This sets the icon shown in Explorer, the taskbar, and any shortcut — the tray icon picks up the same icon automatically at runtime.

## Usage

### Background hotkey mode (the main way to use it)

```
PTZControl.exe
```
or
```
PTZControl.exe hotkeys [deviceIndex] [panStep] [tiltStep] [zoomStep]
```

Runs silently in the system tray. Right-click the tray icon for **Status**, **List presets**, **Edit hotkeys file**, **Reload hotkeys**, or **Exit**.

Default hotkeys (all remappable, see [Customizing hotkeys](#customizing-hotkeys)):

| Hotkey | Action |
|---|---|
| `Ctrl+Alt+Left` / `Right` | Pan |
| `Ctrl+Alt+Up` / `Down` | Tilt |
| `Ctrl+Alt+PageUp` / `PageDown` | Zoom in / out |
| `Ctrl+Alt+Home` | Reset pan/tilt/zoom to defaults |
| `Ctrl+Alt+End` | Show current status (balloon tip) |
| `F1`–`F8` | Recall preset 1–8 |
| `Ctrl+Alt+Shift+F1`–`F8` | Save current pan/tilt/zoom as preset 1–8 |

If pan or tilt has no range to move in at the current zoom level (common with digital pan/tilt at 1x zoom), the hotkey simply does nothing — no automatic zoom adjustment happens.

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

PTZControl.exe bindings
```

- `bindings` prints the hotkey file path and each binding, flagging any that are invalid, duplicated, or already in use by another program (exit the tray app first, or its own keys will show as in use)
- `deviceIndex` defaults to `0` (use `list` to see indices if you have more than one camera)
- `move` is relative to the current value; `set` is absolute
- Camera property values must land on the driver's native step grid (see `status` for each property's `step=`) — an arbitrary value between min/max can fail with `0x80070057` (`E_INVALIDARG`) if it doesn't align

## How presets work

Each preset stores pan, tilt, and zoom together. On recall, **zoom is applied first**, then pan, then tilt — because digital pan/tilt crop range depends on the current zoom level, applying zoom first ensures pan/tilt land where you actually set them rather than getting clamped against a mismatched range.

Presets persist to:
```
%APPDATA%\PTZControl\presets.ini
```

Step sizes and device index persist to:
```
%APPDATA%\PTZControl\config.ini
```

Hotkey bindings are stored in:
```
%APPDATA%\PTZControl\hotkeys.ini
```

## Customizing hotkeys

Hotkeys live in a plain-text file, created with the defaults the first time the tray app runs:

```
%APPDATA%\PTZControl\hotkeys.ini
```

Open it from the tray menu (**Edit hotkeys file...**), change what you like, save, then choose **Reload hotkeys** — no restart or recompile needed.

```ini
# ActionName=Modifier+Modifier+Key
PanLeft=Ctrl+Shift+Left
TiltUp=Ctrl+Alt+Up
ZoomIn=Ctrl+Alt+PageUp
Recall1=F1
Save1=Ctrl+Alt+Shift+F1
Status=
```

- **Actions:** `PanLeft` `PanRight` `TiltUp` `TiltDown` `ZoomIn` `ZoomOut` `Reset` `Status` `Recall1`–`Recall8` `Save1`–`Save8`
- **Modifiers:** `Ctrl`, `Alt`, `Shift`, `Win`
- **Keys:** any .NET key name — `Left`, `PageUp`, `Home`, `F1`–`F24`, `A`–`Z`, `0`–`9`, `NumPad0`, `Add`, … (for the `+` key itself use `Oemplus` or `Add`)
- **Disable an action** by leaving its value blank (or `none`). A line you delete falls back to that action's default.
- Windows reserves a few combinations (`Ctrl+Alt+Del`, `Win+L`, and `F12` for debuggers) that can't be assigned.

If a key can't be assigned — misspelled, duplicated within the file, or already claimed by another program — a message box lists exactly which ones and why, and the rest still work. The same F-key caveat applies to the defaults: `F1`–`F8` are plain global hotkeys, so if another app or your laptop's Fn-lock behavior already uses them, remap the `Recall` actions to something like `Alt+F1`.

## License

Add a license of your choice here (e.g. MIT).
