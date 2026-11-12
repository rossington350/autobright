# Adaptive Monitor Brightness Controller — Functional Specification

## 1. Purpose

This document specifies the functional behaviour of an application that
**automatically adjusts the brightness of one or more computer monitors in
response to ambient light** measured by an external light (lux) sensor. It is
written to be implementation-agnostic: it describes *what* the program does and
*how it must behave*, not which programming language, GUI toolkit, sensor
vendor, or operating system API is used. An AI or developer given this document
should be able to reproduce a functionally equivalent application on any
platform.

The reference implementation is a Windows desktop application named **AutoBright**,
written in **C# on .NET 10 using .NET MAUI** (with the WinUI 3 / Windows App SDK
window underneath). Because the hardware it drives is Windows-specific (DDC/CI
monitor brightness, a USB Yoctopuce light sensor, a Win32 notification-area
icon), it ships as a single Windows target rather than a cross-platform MAUI head
set. Even so, every requirement below is stated in terms of capabilities (read
ambient light, enumerate monitors, set monitor brightness, persist settings, show
a GUI), each of which has analogues on other platforms.

---

## 2. High-Level Behaviour

The program runs continuously as a small desktop utility with a window and an
optional system-tray/notification-area presence. On a fixed timer it:

1. Reads the current ambient light level (in **lux**) from an external sensor.
2. Maps that lux value to a **target brightness percentage** using a
   user-configurable linear mapping.
3. Smooths and filters the target to avoid jitter and abrupt jumps.
4. Enumerates all connected, brightness-controllable monitors.
5. Writes the target brightness to each monitor (translating the percentage to
   the monitor's own hardware brightness range).
6. Updates the GUI with live status: current lux, target brightness, per-monitor
   state, and any error messages.

The automatic lux-driven behaviour can be overridden: a **manual control** mode
bypasses the sensor mapping entirely and drives every monitor to a user-chosen
brightness (a fixed minimum, a fixed maximum, or a slider value) — see §4.5.

It tolerates the sensor being unplugged and monitors being disconnected,
recovering automatically when hardware returns. All tunable parameters can be
changed live through the GUI and are persisted to disk between runs (the GUI
auto-saves on edit; there is no explicit "save" or "apply" step).

---

## 3. Core Concepts and Data Model

### 3.1 Configuration parameters (all user-adjustable, all persisted)

| Parameter | Meaning | Default | Constraints |
|---|---|---|---|
| `min_lux` | Lux value mapped to minimum brightness | 50.0 | Must be `< max_lux`; auto-corrected to `max_lux - 1` style fix if violated |
| `max_lux` | Lux value mapped to maximum brightness | 300.0 | Must be `> min_lux` |
| `min_brightness` | Minimum brightness, percent | 20 | 0–100, must be `< max_brightness` |
| `max_brightness` | Maximum brightness, percent | 100 | 0–100, must be `> min_brightness` |
| `interval` (a.k.a. update rate) | Seconds between update cycles | 1.0 | Must be `> 0` |
| `max_brightness_rate` | Max brightness change, percent per second (slew-rate limit). `0` disables rate limiting | 25.0 | `>= 0` |
| `lux_hysteresis` | Minimum lux change before target is recomputed. `0` disables hysteresis | 10.0 | `>= 0` |
| `show_lux` | Whether the GUI displays the live lux reading | false | boolean |
| `minimize_to_tray` | If true, closing the window hides to tray instead of quitting | false | boolean |
| `start_minimized` | If true, app starts hidden in the tray | false | boolean |
| `run_at_startup` | If true, the app is registered to launch automatically when the user logs in to the OS (§9.1) | false | boolean |
| `remote_hub` | Sensor connection target: `"usb"` for a locally attached sensor, or a network hub address | `"usb"` | string |
| `manual_control` | If true, ignore the sensor and drive monitors from the manual settings below (§4.5) | false | boolean |
| `manual_mode` | Manual target source: `manual` (slider), `min`, or `max` | `manual` | one of `manual`/`min`/`max` |
| `manual_brightness` | The manual slider value (percent) used when `manual_mode` = `manual` | 50 | 0–100 |
| `manual_absolute` | If true, the manual Min/Max/slider span the absolute 0–100% range; if false they are bounded by `min_brightness`/`max_brightness` | false | boolean |
| `window_geometry` | Saved window position and size (`x`, `y`, `width`, `height`) | (none) | object/struct |

### 3.2 Per-monitor data

For each controllable monitor the program tracks:

- An opaque **handle** identifying the physical monitor for read/write calls.
- A human-readable **name/description**.
- The monitor's **hardware brightness range**: `min`, `current`, `max` raw
  values (these are device-defined integers, *not* percentages — e.g. a monitor
  may report 0–100, or 0–10, or any range).

### 3.3 Runtime state (shared between worker and UI)

- `current_lux` — last sensor reading (or null/none if unavailable).
- `target_brightness` — last computed target percentage.
- `monitor_status` — short human-readable status string.
- `last_error` — last error message (sensor or worker).
- `monitor_list` — list of formatted per-monitor status lines for display.

This state is mutated by a background worker and read by the UI; access must be
synchronized (e.g. via a lock) because they run on different threads.

---

## 4. The Lux-to-Brightness Mapping

### 4.1 Linear mapping with clamping

Given an effective lux value `L`:

- If `L <= min_lux` → target = `min_brightness`.
- If `L >= max_lux` → target = `max_brightness`.
- Otherwise linearly interpolate:

  ```
  target = min_brightness
         + (L - min_lux) * (max_brightness - min_brightness) / (max_lux - min_lux)
  ```

  Round to the nearest integer and clamp to `[min_brightness, max_brightness]`.

If the lux value is unavailable (null), the target is null and no brightness is
written (but the rest of the cycle still runs).

Note the design intent: **brighter ambient light → higher monitor brightness**
(the monitor is made brighter in bright rooms and dimmer in dark rooms).

### 4.2 Lux hysteresis (input filtering)

To prevent constant micro-adjustments from a noisy sensor, the raw lux reading
is passed through a hysteresis filter before mapping:

- Maintain an `effective_lux` value (the last *committed* reading).
- On the first valid reading, or when `lux_hysteresis <= 0`, commit the reading
  immediately.
- Otherwise, only commit the new reading (replacing `effective_lux`) if it
  differs from the committed value by **at least** `lux_hysteresis`. If not,
  reuse the previous `effective_lux`.
- If the new reading is null, reuse the previous `effective_lux`.

The mapping in 4.1 is computed from `effective_lux`, not the raw reading.

### 4.3 Brightness smoothing (output slew-rate limiting)

To avoid visible jumps, the *applied* brightness ramps toward the target rather
than snapping to it:

- Maintain a `smoothed_brightness` float.
- On the first application, or when `max_brightness_rate <= 0`, set
  `smoothed_brightness` directly to the target (no smoothing).
- Otherwise compute `max_step = max_brightness_rate * interval` (the most the
  brightness may move in one cycle). If the distance to the target is within
  `max_step`, jump to the target; otherwise move by `max_step` toward it.
- The applied value is `round(smoothed_brightness)`.

The smoothing state is only updated when monitors are actually being driven; if
all monitors disappear, the smoothing state is left untouched so ramping resumes
from the last applied value when monitors return.

### 4.4 Percentage ↔ hardware-value translation

Monitors expose their own raw brightness range, so the target percentage must be
translated per monitor:

- **Percent → raw:** if the monitor reports no usable range
  (`max <= min`), use the percentage directly as the raw value. Otherwise:

  ```
  raw = monitor.min + (clamped_percent / 100) * (monitor.max - monitor.min)
  ```

  rounded to nearest integer, with `clamped_percent` in `[0, 100]`.

- **Raw → percent** (for display): if no usable range, show the raw value;
  otherwise `round((raw - min) / (max - min) * 100)`.

### 4.5 Manual override

When `manual_control` is enabled, the lux-to-brightness mapping (§4.1) is
**bypassed** and the target brightness comes from the manual settings instead.
The sensor is still read (and shown in the GUI), and lux hysteresis is still
computed, but its result is not used to pick the target. The manual target is:

| `manual_mode` | `manual_absolute = false` | `manual_absolute = true` |
|---|---|---|
| `max` | `max_brightness` | `100` |
| `min` | `min_brightness` | `0` |
| `manual` | `manual_brightness` clamped to `[min_brightness, max_brightness]` | `manual_brightness` clamped to `[0, 100]` |

Manual control **applies immediately**, bypassing the slew-rate limiter (§4.3):
the chosen value is written to every monitor on the next cycle with no ramp. To
avoid a visible jump when the user later switches back to automatic mode, the
smoothing baseline (`smoothed_brightness`) is kept in sync with the manual value
each cycle, so automatic ramping resumes from wherever manual control left off.

---

## 5. Update Cycle (Worker Loop)

A background worker repeats the following every `interval` seconds. It must be
resilient: any exception in a cycle is caught, recorded in `last_error`, and the
loop continues (optionally printing a traceback in debug mode).

1. **Service the sensor connection** (see §6.3 "tick"): register the hub if not
   yet connected, otherwise refresh the device list.
2. **Read lux** from the sensor (may be null).
3. **Filter** the reading through lux hysteresis (§4.2) → `effective_lux`.
4. **Compute raw target** brightness: if `manual_control` is on, use the manual
   target (§4.5); otherwise map `effective_lux` (§4.1).
5. **Enumerate monitors** (§7).
6. **Branch on monitor availability:**

   - **No monitors found:**
     - Increment a failure counter, reset the success counter.
     - If a previous good monitor list exists *and* it was seen within a
       **retain timeout** (10 seconds), treat this as a *transient* loss:
       display the last-known monitor list (marked "last known"), show a status
       like "Temporary monitor loss detected. Retaining last known monitor list
       for Ns while retrying..." with a live countdown, and include what the
       target brightness *would* be.
     - Otherwise show a "No compatible monitors found…" message (advising the
       user that the monitor must support hardware brightness control) and clear
       the monitor list.
     - Do **not** modify smoothing state. Still publish the raw target for
       display.

   - **Monitors found:**
     - Increment a success counter, reset the failure counter, and record this
       list as the last-known-good list with a timestamp.
     - Compute the **applied** target: under manual control use the raw target
       directly (no smoothing, §4.5) and keep `smoothed_brightness` synced to it;
       otherwise compute the **smoothed/applied** target (§4.3).
     - Prune the "last sent value" cache (§5.1) to only currently-present
       handles.
     - For each monitor, build a display line (name, current %, raw value,
       hardware range) and, if there is an applied target:
       - Translate to the monitor's raw value (§4.4).
       - **Skip the write** if the last value we sent to this monitor equals the
         new value *and* the monitor currently reports that same value
         (idempotence / redundant-write avoidance). Mark the line "unchanged".
       - Otherwise write the brightness. On success, cache the sent value and
         mark "set to N%"; on failure, drop the cached value and mark "failed to
         set brightness".
     - Only publish the monitor list/status once a **stabilization threshold**
       of consecutive successful cycles (2) is reached — this skips a possibly
       stale first read at startup.
     - Publish the applied target.
7. Publish `current_lux` and any `last_error` from the sensor.
8. Atomically merge all updates into shared state (under the lock).
9. Sleep `interval` seconds.

### 5.1 Redundant-write avoidance

The program caches the last raw value successfully written to each monitor
handle. A write is skipped only when both (a) the cached last-sent value and
(b) the monitor's currently reported value already equal the desired value. This
avoids unnecessary hardware writes while still re-asserting brightness if an
external agent (the OS, another app, the user) changed it. The cache is pruned
each cycle to drop handles that no longer exist, preventing unbounded growth and
stale handle reuse.

---

## 6. Ambient Light Sensor Interface

The program depends on an external **lux sensor** reachable either over a local
connection (e.g. USB) or over a network hub. The reference implementation uses a
Yoctopuce light sensor, but the contract below is what matters.

### 6.1 Required capabilities

The sensor abstraction must provide:

- **Connect / register** to a hub target (`"usb"` or a network address).
- **Enumerate** available light sensors and select the first one found.
- **Check online status** of the selected sensor.
- **Read the current value** as a floating-point lux measurement.
- **Reset/reconnect** on demand (drop the current sensor and connection so the
  next cycle re-registers).

### 6.2 Connection lifecycle and resilience

- If the sensor library/driver is missing entirely, the program must still
  launch and run; it reports the import/driver error and simply produces null
  lux readings (no brightness changes) until the sensor becomes available.
- Maintain an `api_ready` flag. Each cycle's "tick":
  - If not connected, attempt to register/connect to the hub.
  - If connected, refresh the device list; if that fails, assume the hub was
    lost, clear `api_ready`, and force re-registration next cycle.
- When first acquiring a sensor, optionally set a sensor report frequency
  (reference uses `60/m`, i.e. 60 samples per minute).
- Reading errors (sensor offline, exceptions) clear the current sensor reference
  and return null; the next cycle will attempt to re-find it.
- All error conditions are surfaced via a `last_error` string rather than
  crashing.

### 6.3 Optional: sensor-board indicator suppression

The reference sensor hardware has onboard status LEDs/beacons. The program turns
these off for a non-distracting setup:

- On the sensor's module: set luminosity to 0, turn the beacon off, and persist
  to flash. **Important constraint:** because these settings are written to
  flash (limited write-cycle endurance), they must be written **at most once per
  connection**, guarded by a "leds_configured" flag — not every cycle.
- Additionally, enumerate any standalone LED and color-LED devices and turn them
  off (power off / luminosity 0 / RGB 0).

This whole feature is optional and vendor-specific; on hardware without
controllable indicators it is simply skipped. Every operation here is best-effort
and wrapped so failures never interrupt operation.

---

## 7. Monitor Control Interface

The program must enumerate physical monitors and read/write their brightness
through a hardware brightness-control channel (the reference uses Windows DDC/CI
via the `dxva2`/`user32` APIs; equivalent mechanisms exist elsewhere, e.g.
DDC/CI over I²C on Linux/macOS).

### 7.1 Required capabilities

- **Enumerate** all display monitors and, for each, obtain one or more physical
  monitor handles.
- For each physical monitor, **read** its brightness triple: `min`, `current`,
  `max` raw values, plus a description string. Monitors that don't support
  brightness queries are skipped (not included in the controllable list).
- **Write** a raw brightness value to a monitor handle; report success/failure.
- **Release/destroy** monitor handles when done (resource cleanup).

### 7.2 Caching and refresh behaviour

- Maintain a cache of enumerated monitor handles to avoid re-opening handles
  every cycle.
- A normal `enumerate` call:
  - If the cache is empty or a forced refresh is requested: destroy cached
    handles and do a full re-collection.
  - Otherwise: refresh just the brightness triples on the cached handles,
    dropping any handle that no longer responds; if that empties the cache, do a
    full re-collection.
- A `refresh` (forced) always destroys the cache and re-collects from scratch.
- Handle cleanup must run when monitors are dropped, when refreshing, and on
  application exit, to avoid leaking OS handles.

### 7.3 Failure semantics

- A monitor that fails its brightness query during collection is silently
  excluded.
- A failed brightness *write* is reported per-monitor in the status display and
  clears that monitor's cached "last sent" value so the write is retried next
  cycle.

---

## 8. Graphical User Interface

A single resizable, scrollable window titled "AutoBright" (or equivalent),
organized as a vertical stack of cards: a header, a live readout, a **Manual
override** card, a **Settings** card, and a **Monitors** card. Where the platform
supports it the window uses a translucent (Mica) backdrop, falling back to a
solid background otherwise. The UI runs on the main thread; the worker runs
separately and communicates only through the shared, locked state.

There is **no explicit Save/Apply button** — every editable control auto-commits
(validating and persisting) as described in §8.4.

### 8.1 Layout

A header (app identity plus a **Hide to tray** button) above two groups of
cards. Windows at least ~860 DIPs wide show them side by side; narrower windows
stack them:

- **Status column** — the live status card (§8.3), the manual override card,
  and the monitors card.
- **Settings column** — **Light mapping**, a collapsible **Advanced** card
  (collapsed by default; expanded automatically if one of its fields has a
  validation error), and **Window**.

**Colours follow Windows.** Light/dark follows the system app mode, and the
background uses Mica where available. The accent (buttons, links, switches,
sliders, progress bars, the chart) is the user's Windows accent colour: the
`AccentDark1` shade in light mode and `AccentLight2` in dark mode, with black or
white text on accent fills chosen for contrast. In high-contrast mode the system
highlight colours are used instead. Changes to the accent, theme, or contrast
mode apply live, without a restart. The app icon is deliberately neutral
(graphite with a white glyph) so it suits any accent.

### 8.2 Input controls (editable fields)

Each numeric field shows a caption, the entry, a unit suffix, and a short hint
line underneath that is replaced by the field's validation error (§8.4).

- **Light mapping** — **Min lux**, **Max lux** (lx), **Min brightness**,
  **Max brightness** (%).
- **Advanced** — **Update every** (s, `interval`), **Max change rate** (%/s),
  **Lux hysteresis** (lx).
- **Window** — three checkboxes: **"Minimize to tray on close"**
  (`minimize_to_tray`), **"Run at Windows startup"** (`run_at_startup`, §9.1),
  and **"Start minimized to tray"** (`start_minimized`). A status line below the
  settings cards shows save feedback.
- **Manual override card** — a **switch** that reveals, when on: a three-way
  **Custom / Minimum / Maximum** radio group (`manual_mode`: `manual` / `min` /
  `max`), a **slider** with a live percentage label (`manual_brightness`,
  enabled only in `manual` mode), and a checkbox **"Use the full 0–100% range
  (ignore the configured limits)"** (`manual_absolute`). The slider's range is
  the absolute 0–100% when `manual_absolute` is set, otherwise the configured
  `min_brightness`–`max_brightness` band, and the current value is clamped into
  that range whenever the range changes. When manual control is first switched
  on, the slider is seeded with the brightness currently being driven so there
  is no visible jump.

Some persisted parameters are **not** surfaced as GUI controls and are set only
via CLI/settings: `show_lux` and `remote_hub`.

**Actions:** **Refresh** (monitors card) forces a full monitor re-enumeration;
**Reconnect sensor** (status card, shown only while there is no sensor reading)
forces the sensor to drop and re-register next cycle; **Hide to tray** (header)
hides the window to the notification area.

### 8.3 Live status display (refreshed ~ every 500 ms)

- **Ambient light**: the value in lx (one decimal below 100 lx, whole numbers
  above) when `show_lux` is enabled; `Hidden` when disabled; `—` when
  unavailable.
- **Brightness**: the target, e.g. `73%` or `—`, with a progress bar.
- **Badges**: sensor state (*Sensor connected* / *No sensor reading*, derived
  from whether this cycle produced a lux value) and mode (*Auto*, *Manual*,
  *Manual · minimum*, *Manual · maximum*).
- **Light → brightness chart**: the mapping curve (flat at `min_brightness`
  below `min_lux`, linear to `max_lux`, flat at `max_brightness` above) with
  dashed markers at both thresholds and a dot on the curve at the current
  reading. Under manual control the curve is dimmed and a dashed horizontal line
  shows the manual target instead.
- **Worker status line**: the current monitor status message, with `last_error`
  appended (after a separator) when present; rendered in an error colour when an
  error is shown.
- **Monitor rows**: one row per monitor with its name, current %, a progress
  bar, the raw value and hardware range, and the per-cycle action ("Set to N%",
  "Holding at N%", or "Failed to set brightness" in the error colour). During a
  temporary monitor loss the last-known rows are shown dimmed and marked as not
  responding. The worker publishes structured rows (`MonitorRow`), and the UI
  updates its list in place (rows that compare equal are left untouched) so it
  does not flicker between refreshes.

The UI refresh reads a snapshot of the shared state under the lock and runs on a
repeating timer while the app is running.

### 8.4 Input validation and auto-commit

Editable controls commit automatically: text entries on **Enter/Completed** and
on **focus loss**; the tray checkbox, manual checkbox, radios, and absolute
checkbox on change; the manual slider applies **live** while dragging with the
disk write **debounced** (~400 ms) until the drag settles.

On each commit, all fields are validated **as a set**. Every numeric field must
parse (lux/rate/hysteresis as floats, brightness as integers); then enforce:

- `min_lux < max_lux`
- both brightness values within 0–100, and `min_brightness < max_brightness`
- both lux values within 0–1,000,000
- `0 < interval <= 3600`
- `max_brightness_rate >= 0` (0 disables smoothing)
- `lux_hysteresis >= 0` (0 disables hysteresis)

Validation feedback is **inline** and per field (the offending field's caption
turns the error colour and its hint line shows the message), not a modal
dialog. All fields are checked at once, so several errors can show together.
Non-finite input (`NaN`, `Infinity`) counts as a parse failure. On any parse
failure or violation the commit is abandoned,
leaving the previously applied configuration untouched. On success (and only if
something actually changed) the live parameters are updated, settings are
persisted, and a "Settings saved." status is shown.

### 8.5 Startup validation / auto-correction

When values come from CLI/settings at startup, invalid combinations are
auto-corrected rather than rejected: if `min_lux >= max_lux`, set
`max_lux = min_lux + 1`; if `min_brightness >= max_brightness`, set
`max_brightness = min(100, min_brightness + 1)`; if `interval <= 0`, reset to
default; negative rate/hysteresis are clamped to 0.

---

## 9. System Tray / Notification Area

The app can run minimized to the OS notification area:

- **Hide to tray** removes/hides the window and shows a tray icon with a
  tooltip ("Brightness Dimmer").
- **Double-click** (or left double-click) the tray icon → restore the window.
- **Right-click** the tray icon → context menu with **Restore** and **Exit**.
- **Restore** brings the window back and raises it; the tray icon is removed.
- **Exit** stops the worker, releases monitor handles, removes the tray icon,
  and quits.
- If `start_minimized` is set, the app hides to tray immediately on launch.
- If `minimize_to_tray` is set, the window's close button hides to tray instead
  of quitting; otherwise close performs a full shutdown (stop worker, release
  handles, remove tray icon, destroy window).

The tray icon should use a bundled application icon if available, falling back
to a default system icon.

### 9.1 Run at OS startup

When `run_at_startup` is enabled, the app registers itself with the operating
system's per-user "run on login" mechanism (the reference implementation writes
the executable path to the Windows `HKCU\…\CurrentVersion\Run` registry key;
equivalent mechanisms exist elsewhere, e.g. a `.desktop` autostart entry or a
launch agent). When disabled, the registration is removed.

- The stored launch command points at the **current executable**, and includes
  the `--start-minimized` switch (§11) when `start_minimized` is also enabled, so
  a login launch goes straight to the tray.
- Registration is **best-effort**: failure to read or write the OS mechanism is
  ignored and never crashes the app.
- The registration is reconciled with the saved setting **on every settings
  change and once at launch**, so that a moved or reinstalled executable is
  re-registered with the correct path.
- It uses a **per-user** mechanism, requiring no elevation.

---

## 10. Settings Persistence

- Settings are stored as a JSON document in a per-user application-data location
  (reference: `%APPDATA%/BrightnessDimmer/settings.json`, falling back to the
  user's home directory). The directory is created if missing.
- **Load** at startup: read the file if present; on any error, fall back to
  empty/defaults (never crash).
- **Save** writes all parameters from §3.1 plus the sensor hub URL and the
  current window geometry. Saving is automatic: it is triggered whenever a
  setting is committed (§8.4) and, debounced, whenever the window is moved or
  resized. Save failures are caught and ignored (best-effort); they do not crash
  the app. The write itself is atomic (write to a temporary file, then rename
  it over the real path) so a crash or power loss mid-write can never leave a
  truncated or corrupt settings file.
- **Window geometry** is persisted as an object (`x`, `y`, `width`, `height`)
  and only captured while the window is in a normal, on-screen state — a
  minimized/hidden window (which Windows parks near `(-32000, -32000)` with a
  tiny size) is rejected so a bad rectangle is never written. On restore the
  saved rectangle is likewise validated and clamped onto a currently-connected
  display, so a monitor change cannot strand the window off-screen. On first run
  (no valid saved geometry) a compact default size is used.
- The full precedence for each parameter at startup is:
  **command-line argument > saved setting > built-in default.**

---

## 11. Command-Line Interface

All parameters may be supplied at launch. Any option left unset falls back to
the saved setting, then the default. Options:

| Option | Type | Maps to |
|---|---|---|
| `--min-lux` | float | `min_lux` |
| `--max-lux` | float | `max_lux` |
| `--min-brightness` | int | `min_brightness` |
| `--max-brightness` | int | `max_brightness` |
| `--remote-hub` | string | sensor hub (`usb` or network address) |
| `--update-rate` / `--interval` | float | `interval` (aliases) |
| `--max-brightness-rate` | float | `max_brightness_rate` (0 disables) |
| `--lux-hysteresis` | float | `lux_hysteresis` (0 disables) |
| `--show-lux` | flag | `show_lux` |
| `--minimize-to-tray` | flag | `minimize_to_tray` |
| `--start-minimized` | flag | `start_minimized` |
| `--debug` | flag | enable debug logging / tracebacks |

The manual-override parameters (`manual_control`, `manual_mode`,
`manual_brightness`, `manual_absolute`) are **not** command-line options; they
are set through the GUI and persisted to settings.

If the sensor library is unavailable at launch, the app continues to start the
GUI and surfaces the error through `last_error` (producing null lux readings)
rather than crashing.

---

## 12. Concurrency and Lifecycle

- **Single instance:** only one instance of the application runs at a time. A
  second launch attempt detects the first instance and exits immediately
  without opening a window, so two instances never compete to drive the same
  monitor hardware or race each other's writes to the settings file.
- **Two execution contexts:** a UI/main thread and a single background worker
  thread (daemonized so it cannot block process exit). The worker performs all
  sensor reads and monitor writes; the UI only reads shared state and issues
  user commands.
- **Shared state** is guarded by a mutex. The worker batches its per-cycle
  changes into a dict and merges them under the lock once per cycle; the UI
  copies the state under the lock for each refresh.
- **Graceful shutdown** (Exit or window close when not minimizing to tray):
  set a `running` flag false, join the worker thread with a timeout (~2 s),
  release all monitor handles, remove the tray icon, and destroy the window.

---

## 13. Resilience Requirements (summary)

The application must keep running through all of the following without crashing:

- Sensor library/driver entirely absent → run with null lux, report the error.
- Sensor unplugged at runtime → null readings, auto-reconnect attempts each
  cycle.
- Hub/network connection lost → detected on device-list refresh, triggers
  re-registration.
- All monitors disconnected → retain last-known list for a grace period (10 s),
  then report "no compatible monitors", auto-recover when monitors return.
- A monitor failing a brightness write → reported, retried next cycle.
- Any unexpected exception in the worker cycle → caught, recorded, loop
  continues.

---

## 14. Packaging (informative, non-functional)

The reference app is distributed as an **unpackaged** (no MSIX), windowed
Windows executable. It is built **self-contained**, bundling both the .NET
runtime and the Windows App SDK, so it runs on a machine with neither installed.
The application icon (`icon.ico`) is copied alongside the executable for use as
the window title-bar and notification-area icon, and the Yoctopuce sensor library
is referenced as a normal package dependency. Builds target `win-x64` and
`win-arm64`; the output path is kept stable (no runtime-identifier subfolder) so
launch/build configurations resolve regardless of host architecture. Any
equivalent packaging that produces a self-contained desktop binary satisfies this
requirement; it is not part of the runtime behaviour.

The reference implementation additionally ships an **MSI installer** built from a
*framework-dependent* publish (the .NET runtime is **not** bundled, keeping the
download small; the Windows App SDK still is, so the only external dependency is
the .NET Desktop Runtime). The installer **recognises that runtime as a
prerequisite**: it detects whether the runtime's shared framework is present and,
if absent, blocks the install with guidance to download it. It installs per
machine, adds a Start Menu shortcut, and runs a setup wizard that ends with a
"setup complete" dialog. Packaging form is informative only; any installer that
declares the runtime dependency is acceptable.

---

## 15. Default Constants Reference

| Constant | Value |
|---|---|
| Default min lux | 50.0 |
| Default max lux | 300.0 |
| Default min brightness | 20 % |
| Default max brightness | 100 % |
| Default update interval | 1.0 s |
| Default max brightness change rate | 25.0 %/s |
| Default lux hysteresis | 10.0 lux |
| Default manual control | off |
| Default manual mode | manual |
| Default manual brightness | 50 % |
| Default manual absolute range | off |
| Default run at OS startup | off |
| Default start minimized | off |
| Monitor-retain grace period | 10.0 s |
| Stable cycles before publishing monitor list | 2 |
| UI refresh period | ~500 ms |
| Settings/geometry save debounce | ~400 ms |
| Minimum worker sleep (floor on interval) | 0.05 s |
| Worker-join timeout on shutdown | 2.0 s |
| Sensor report frequency (if settable) | 60 samples/min |

---

## 16. Verification (informative)

The formulas in §4 (lux→brightness mapping, hysteresis, slew-rate smoothing,
percent↔hardware-value translation) and the auto-correction rules in §8.5 are
pure, deterministic functions of their inputs and small, well-defined state.
Any reimplementation should validate them with an automated unit-test suite
(worked examples at the clamp boundaries, the hysteresis/smoothing thresholds,
and the auto-correction edge cases) rather than relying on manual QA alone —
these are the parts of the app that are cheapest to get subtly wrong (e.g. an
off-by-one in a clamp, or `>` vs `>=` at a threshold) and cheapest to pin down
with a test. The sensor and monitor I/O layers (§6, §7) are hardware-bound and
are exercised through manual/integration testing instead. See TECHNICAL.md §19
for how the reference implementation's test suite is organized.
