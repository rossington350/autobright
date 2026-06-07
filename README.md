# Brightness Dimmer

A Windows GUI application that adjusts monitor brightness using a Yoctopuce light sensor.

## Features

- Maps ambient lux to monitor brightness using configurable min/max lux and brightness values.
- Supports multiple monitors and recovers when displays disconnect/reconnect.
- Runs continuously with a GUI for live parameter changes.
- Handles missing Yoctopuce sensor and reconnects when the device returns.

## Requirements

- Windows 10/11
- Python 3.9+
- Tkinter (bundled with standard Python on Windows)
- Yoctopuce Python library (`yoctopuce` package)

## Installation

1. Create and activate a virtual environment:

```powershell
python -m venv .venv
.\.venv\Scripts\Activate.ps1
```

2. Upgrade pip:

```powershell
python -m pip install --upgrade pip
```

3. Install the project and runtime dependencies:

```powershell
python -m pip install -e .
```

If you want to build a packaged executable, also install packaging dependencies:

```powershell
python -m pip install .[packaging]
```

## Usage

Run the app from the project directory:

```powershell
.\.venv\Scripts\python.exe -m autobright --min-lux 50 --max-lux 300 --min-brightness 20 --max-brightness 100 --show-lux
```

Or run the main script directly:

```powershell
.\.venv\Scripts\python.exe autobright.py --min-lux 50 --max-lux 300 --min-brightness 20 --max-brightness 100 --show-lux
```

### Command line options

- `--min-lux` : Lux value that maps to minimum brightness.
- `--max-lux` : Lux value that maps to maximum brightness.
- `--min-brightness` : Minimum brightness percentage (0-100).
- `--max-brightness` : Maximum brightness percentage (0-100).
- `--remote-hub` : YoctoHub address, or `usb` for local USB devices.
- `--interval` : Refresh interval in seconds.
- `--show-lux` : Display lux value in the GUI.
- `--start-minimized` : Start the app minimized to the notification area.
- `--debug` : Enable debug logging.

## Packaging with PyInstaller

The repository includes `build.ps1` and `autobright.spec` for packaging.

Build the bundled executable using the helper script:

```powershell
.\build.ps1
```

This uses `autobright.spec`, which bundles the Yoctopuce DLLs from `yoctopuce/cdll` and produces a single-file executable.

If you prefer to run PyInstaller directly:

```powershell
python -m pip install .[packaging]
python -m PyInstaller autobright.spec
```

## GUI Controls

- Change the lux/brightness mapping values on the fly.
- Click `Apply` to use new settings immediately.
- Click `Refresh monitors` to force a monitor re-scan.
- Click `Reconnect sensor` to force sensor reinitialization.

## Notes

- The app preserves the last known monitor list during transient monitor drops and retries connections.
- If the Yoctopuce sensor is unplugged, the app continues running and attempts to reconnect.
- The brightness mapping is clamped to the configured brightness range.
