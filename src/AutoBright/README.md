# Brightness Dimmer (C# / .NET MAUI)

A C# / .NET MAUI rewrite of the original Python Brightness Dimmer. It adjusts
monitor brightness automatically based on ambient light measured by a Yoctopuce
light sensor, with a live GUI, system-tray support, and persisted settings.

The hardware this controls (DDC/CI monitor brightness, the Yoctopuce USB sensor,
the Win32 notification-area icon) is Windows-specific, so the MAUI project has a
single Windows target rather than the usual multi-platform head set. On Windows,
MAUI runs on top of WinUI 3 / the Windows App SDK, so the underlying window is
managed through the same `AppWindow`/Win32 APIs the previous build used.

This is a functional port: the lux→brightness mapping, lux hysteresis,
brightness slew-rate smoothing, redundant-write avoidance, transient
monitor-loss handling, and sensor reconnect logic all match the original. See
`../../SPECIFICATION.md` for the language-agnostic behaviour spec.

## Requirements

- Windows 10 (1809+) / Windows 11
- .NET 10 SDK with the .NET MAUI workload (`dotnet workload install maui`)
- A Yoctopuce light sensor (the app runs without one and reconnects when it
  appears)

## Dependencies (NuGet, restored automatically)

- `Microsoft.Maui.Controls` — .NET MAUI UI (self-contained Windows App SDK; no
  separate runtime install needed)
- `Yoctopuce.YoctoLib` — official Yoctopuce .NET library

The system tray icon is implemented directly on the Win32 `Shell_NotifyIcon`
API (no third-party tray dependency).

## Project layout

| File | Responsibility |
|---|---|
| `MauiProgram.cs` | MAUI app builder (fonts, logging) |
| `App.xaml(.cs)` | Application bootstrap, command-line parsing, window creation |
| `MainPage.xaml(.cs)` | GUI, tray lifecycle, settings persistence, UI refresh |
| `Platforms/Windows/` | WinUI bootstrap (`App.xaml`), `app.manifest` |
| `Controller/BrightnessController.cs` | Worker loop: sensor read → map → smooth → drive monitors |
| `Controller/ConfigData.cs` | Immutable tunable-parameter snapshot + startup normalization |
| `Controller/AppState.cs` | Worker→UI shared state snapshot |
| `Hardware/MonitorManager.cs` | DDC/CI monitor enumeration + brightness get/set (user32/dxva2) |
| `Hardware/YoctoSensorManager.cs` | Yoctopuce hub/sensor lifecycle, lux reads, LED suppression |
| `Settings/AppSettings.cs` | JSON settings under `%APPDATA%/BrightnessDimmer/settings.json` |
| `CommandLineOptions.cs` | CLI parsing (CLI > settings > defaults) |
| `TrayIcon.cs` | Native notification-area icon + context menu |

## Build

```powershell
dotnet build src\AutoBright\AutoBright.csproj -c Release
```

The Windows App SDK is self-contained and therefore needs a concrete
architecture; the project defaults the runtime identifier to the host's RID, so
no `-r` flag is required. On an ARM device that builds `win-arm64`
automatically. To force a specific target, pass e.g. `-r win-x64`.

## Run

```powershell
dotnet run --project src\AutoBright\AutoBright.csproj -r win-x64 -- --min-lux 50 --max-lux 300 --min-brightness 20 --max-brightness 100 --show-lux
```

Or run the built executable directly (the RID is not appended to the output
path, so there is no `win-x64` subfolder):

```powershell
src\AutoBright\bin\Release\net10.0-windows10.0.19041.0\AutoBright.exe --show-lux
```

## Publish a single self-contained app

```powershell
dotnet publish src\AutoBright\AutoBright.csproj -c Release -r win-x64
```

## Command-line options

- `--min-lux` / `--max-lux` — lux values mapped to min/max brightness
- `--min-brightness` / `--max-brightness` — brightness percentage range (0–100)
- `--remote-hub` — `usb` (default) or a YoctoHub network address
- `--update-rate` (alias `--interval`) — seconds between update cycles
- `--max-brightness-rate` — max brightness change %/s (`0` disables smoothing)
- `--lux-hysteresis` — minimum lux change before re-mapping (`0` disables)
- `--show-lux` — display the live lux value
- `--minimize-to-tray` — closing the window hides to tray instead of quitting
- `--start-minimized` — start hidden in the notification area
- `--debug` — enable debug logging

Any option left unset falls back to the saved setting, then the built-in
default.
