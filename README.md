# AutoBright

**Automatic monitor brightness for Windows, driven by a real ambient-light
sensor.**

This is an experiment to see if Claude Code could produce the whole application 
without any manual code changes. The only manual edit I've made is to add this 
paragraph to the README.md file.

AutoBright reads the room's light level from a [Yoctopuce](https://www.yoctopuce.com/)
USB light sensor and continuously adjusts the brightness of every connected
monitor over [DDC/CI](https://en.wikipedia.org/wiki/Display_Data_Channel) — so
your screens get brighter in daylight and dimmer at night, automatically. When
you want to take over, a manual mode lets you pin brightness to a fixed level or
a slider value.

It runs as a small desktop utility that can tuck itself into the notification
area (system tray) and keep working in the background.

---

## Features

- **Ambient-light driven** — maps measured lux to a brightness percentage with a
  configurable linear curve (min/max lux → min/max brightness).
- **Multi-monitor** — drives every DDC/CI-capable monitor at once, translating
  the target percentage to each display's own hardware range.
- **Smooth & quiet** — slew-rate limiting prevents visible jumps; lux hysteresis
  ignores sensor noise; redundant writes are skipped.
- **Manual override** — switch off auto mode and pin to Min, Max, or a slider
  value (within your configured band or the absolute 0–100% range).
- **Resilient** — survives the sensor being unplugged, monitors being
  disconnected, and hub/network drops, recovering automatically when hardware
  returns.
- **Tray support** — minimize to the notification area, start minimized, restore
  on double-click.
- **Auto-saving settings** — every change is validated inline and persisted; no
  "Apply" button to remember.
- **Self-contained** — ships as a single Windows app that bundles the .NET
  runtime; no separate install required.

---

## Requirements

**To run:**

- Windows 10 (1809 / build 17763) or later, x64 or ARM64.
- One or more monitors that support **DDC/CI** brightness control (enable
  "DDC/CI" in your monitor's OSD menu if available). Most external desktop
  monitors qualify; many built-in laptop panels do not.
- A **Yoctopuce light sensor** (e.g. Yocto-Light series) connected over USB, or
  reachable via a Yoctopuce network hub.
- The Yoctopuce VirtualHub / USB driver is **not** required for a USB sensor —
  the bundled library talks to it directly — but the sensor must be plugged in.

> No sensor? The app still launches and runs; it simply reports the sensor as
> unavailable and makes no brightness changes until one appears.

**To build:** the [.NET 10 SDK](https://dotnet.microsoft.com/download) with the
**.NET MAUI** workload (see [Building](#building)).

---

## Installing / Running

1. Download a release build, or build it yourself (below).
2. Run `AutoBright.exe`. The main window shows the current lux, the target
   brightness, your settings, and a per-monitor status list.
3. Adjust the settings (they save as you go) and the app does the rest. Use
   **Hide to tray** to keep it running in the background.

---

## Using AutoBright

### Automatic mode (default)

Set the brightness curve in the **Settings** card:

| Setting | Meaning |
|---|---|
| **Min lux** / **Max lux** | The ambient range to map across. At or below Min lux you get minimum brightness; at or above Max lux, maximum. |
| **Min / Max brightness (%)** | The brightness range to map into. |
| **Update rate (s)** | How often to read the sensor and adjust. |
| **Max change rate (%/s)** | Caps how fast brightness moves, for smooth fades. Set `0` to disable smoothing (snap instantly). |
| **Lux hysteresis** | Ignore ambient changes smaller than this, to stop hunting on a noisy sensor. Set `0` to disable. |

### Manual mode

Tick **Manual brightness control** to bypass the sensor:

- **Min / Max / Manual** — pin to your configured minimum, maximum, or a slider
  value.
- **Use absolute 0–100% range** — let Min/Max/slider span the full 0–100% instead
  of being bounded by your configured min/max brightness.

Manual changes apply immediately. Switching back to automatic resumes smoothly
from wherever manual left off.

### Tray / background

- **Hide to tray** minimizes to the notification area.
- **Double-click** the tray icon (or right-click → **Restore**) to bring it back;
  right-click → **Exit** to quit.
- Tick **Minimize to tray on close** so the window's close button hides instead
  of quitting.
- Tick **Run at Windows startup** to launch AutoBright automatically when you log
  in (a per-user `HKCU\…\Run` entry, no admin rights needed).
- Tick **Start minimized to tray** so the app starts hidden in the notification
  area. When combined with **Run at Windows startup**, AutoBright launches at
  login straight to the tray (the startup entry adds `--start-minimized`).

### Buttons

- **Refresh monitors** — force a full re-scan of connected displays.
- **Reconnect sensor** — drop and re-establish the sensor connection.

---

## Command-line options

Every option falls back to your saved setting, then a built-in default
(`CLI > saved > default`):

| Option | Type | Description |
|---|---|---|
| `--min-lux <n>` | float | Lux mapped to minimum brightness |
| `--max-lux <n>` | float | Lux mapped to maximum brightness |
| `--min-brightness <n>` | int | Minimum brightness % |
| `--max-brightness <n>` | int | Maximum brightness % |
| `--update-rate <n>` / `--interval <n>` | float | Seconds between updates |
| `--max-brightness-rate <n>` | float | Max % change per second (`0` = off) |
| `--lux-hysteresis <n>` | float | Minimum lux change before adjusting (`0` = off) |
| `--remote-hub <addr>` | string | `usb` (default) or a network hub address |
| `--show-lux` | flag | Show the live lux value in the window |
| `--minimize-to-tray` | flag | Close button hides to tray |
| `--start-minimized` | flag | Start hidden in the tray |
| `--debug` | flag | Verbose debug logging |

Example:

```powershell
AutoBright.exe --min-lux 30 --max-lux 500 --min-brightness 10 --start-minimized
```

The manual-override settings are configured in the GUI, not on the command line.

---

## Settings file

Settings are stored as JSON at:

```
%APPDATA%\BrightnessDimmer\settings.json
```

It is created automatically and rewritten whenever you change a setting or move
the window. Delete it to reset everything to defaults.

---

## Building

AutoBright is a **.NET 10 / .NET MAUI** app targeting Windows only.

### Prerequisites

```powershell
# Install the .NET 10 SDK from https://dotnet.microsoft.com/download, then:
dotnet workload install maui
```

(Or install the **.NET Multi-platform App UI development** workload via the
Visual Studio Installer.)

### Build & run

```powershell
# From the repo root
dotnet build src/AutoBright/AutoBright.csproj
dotnet run   --project src/AutoBright/AutoBright.csproj
```

The host architecture is detected automatically, so no `-r` flag is needed for a
local build. In Visual Studio, open `AutoBright.slnx` and press F5.

### Publish

There are two deployment shapes, depending on whether the .NET runtime is
bundled:

```powershell
# Self-contained — bundles the .NET runtime + Windows App SDK (~240 MB, runs on
# a machine with neither installed). The csproj defaults to this.
dotnet publish src/AutoBright/AutoBright.csproj -c Release -r win-x64

# Framework-dependent — bundles only the Windows App SDK; the .NET Desktop
# Runtime must be installed separately (much smaller). This is what the MSI uses.
dotnet publish src/AutoBright/AutoBright.csproj -c Release -r win-x64 `
    --self-contained false -p:WindowsAppSDKSelfContained=true
```

Output lands under `src/AutoBright/bin/Release/net10.0-windows10.0.19041.0/<rid>/publish/`.

> Note: the plain **build** output (`bin/Release/<tfm>/AutoBright.exe`, no
> `publish` folder) is always framework-dependent, so running it on a machine
> without the .NET Desktop Runtime shows a "download the runtime" prompt. That's
> expected — use a published build for distribution.

### Build the MSI installer

The installer is a framework-dependent build wrapped in an MSI. It does **not**
bundle the .NET runtime (keeping it small) but **recognises it as a dependency**:
if the .NET Desktop Runtime is missing, the installer stops and points the user
to the download.

```powershell
# From the repo root — publishes framework-dependent, then builds the MSI
./build-installer.ps1                 # version 1.0.0.0
./build-installer.ps1 -Version 1.1.0.0
```

Only the .NET SDK is required; the WiX build tooling is restored from NuGet
automatically. The resulting `AutoBright-<version>-win-x64.msi` is written under
`installer/bin/Release/`. It runs a setup wizard (welcome/license → progress →
a **"Setup completed" dialog** with a Finish button), installs to
`Program Files\AutoBright`, adds a Start Menu shortcut, and appears in Add/Remove
Programs.

The installer sources live in [installer/](installer/):
`Package.wxs` (WiX authoring), `AutoBright.Installer.wixproj` (WiX SDK project),
and `License.rtf` (the GPL notice shown on the welcome page). The runtime
dependency is detected by checking for
`%ProgramFiles%\dotnet\shared\Microsoft.WindowsDesktop.App`; end users install
the runtime from <https://dotnet.microsoft.com/download/dotnet/10.0> (the x64
**.NET Desktop Runtime**).

---

## Project structure

```
src/AutoBright/
├─ Controller/   BrightnessController, ConfigData, AppState   (the engine)
├─ Hardware/     MonitorManager (DDC/CI), YoctoSensorManager  (the devices)
├─ Settings/     AppSettings                                  (JSON persistence)
├─ MainPage.*    the window UI and its logic
├─ TrayIcon.cs   notification-area icon (Win32)
└─ ...           MAUI bootstrap, resources, styles
```

---

## Documentation

- **[SPECIFICATION.md](SPECIFICATION.md)** — platform-neutral functional spec
  (what the app does and how it must behave).
- **[TECHNICAL.md](TECHNICAL.md)** — implementation-level blueprint (classes,
  algorithms, Win32 interop, data formats) for rebuilding the app.

---

## Troubleshooting

| Symptom | Likely cause / fix |
|---|---|
| "No compatible monitors found" | Monitor doesn't expose DDC/CI, or it's disabled in the OSD. Enable DDC/CI; try **Refresh monitors**. Most laptop panels can't be controlled this way. |
| Brightness never changes | Sensor not detected (check the status line / `--show-lux`), or you're in manual mode. Try **Reconnect sensor**. |
| Window opens off-screen | It clamps to a connected display automatically; if needed, delete the settings file to reset geometry. |
| Sensor LEDs stay on | LED suppression is best-effort and only runs on supported Yoctopuce hardware. |

---

## License

AutoBright is free software licensed under the **GNU General Public License v3.0**
(or, at your option, any later version). You may use, study, share, and modify it
under those terms; if you distribute it or a derivative, you must pass on the same
freedoms and make the source available. It comes with **no warranty**.

See the [LICENSE](LICENSE) file for the full text, or
<https://www.gnu.org/licenses/gpl-3.0.html>.

```
Copyright (C) 2026 rossington350

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.
```
