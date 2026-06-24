# AutoBright — Technical Specification

This document is an implementation-level blueprint for rebuilding **AutoBright**,
a Windows desktop utility that drives monitor brightness from an ambient-light
sensor. Where [SPECIFICATION.md](SPECIFICATION.md) describes *what* the app does
in platform-neutral terms, this document describes *how the reference
implementation is built*: the exact stack, project layout, classes, algorithms,
Win32 interop signatures, threading model, and data formats needed to reproduce a
byte-for-byte-equivalent application.

> Audience: an AI or engineer recreating the app from scratch. Read
> SPECIFICATION.md first for the behavioural contract; this document fills in the
> concrete engineering.

---

## 1. Technology Stack

| Concern | Choice |
|---|---|
| Language | C# (`LangVersion=latest`, nullable enabled, implicit usings enabled) |
| Runtime | .NET 10 |
| UI framework | .NET MAUI (`UseMaui=true`, `SingleProject=true`) |
| Underlying window | WinUI 3 / Windows App SDK (resolved from the MAUI window handler) |
| Target framework | `net10.0-windows10.0.19041.0` (single Windows head; **not** multi-target) |
| Output | `WinExe` (windowed, no console) |
| Platforms / RIDs | `x64;arm64` / `win-x64;win-arm64` |
| Sensor library | `Yoctopuce.YoctoLib` 2.1.11632 (NuGet) |
| Logging | `Microsoft.Extensions.Logging.Debug` 10.0.0 (DEBUG builds only) |
| Monitor control | Win32 DDC/CI via `dxva2.dll` + `user32.dll` (P/Invoke) |
| Tray icon | Win32 `Shell_NotifyIcon` + window subclassing (`comctl32.dll`) |
| Settings format | JSON, snake_case keys |

The app is **Windows-only by design**: DDC/CI, the Yoctopuce USB sensor, the
notification-area icon, and Mica all bind to Windows APIs. There is therefore a
single TFM rather than the usual MAUI Android/iOS/Mac/Windows head set.

---

## 2. Repository Layout

```
autobright/
├─ AutoBright.slnx                      # solution (new XML "slnx" format)
├─ SPECIFICATION.md                     # behavioural spec (platform-neutral)
├─ TECHNICAL.md                         # this document
├─ build-installer.ps1                  # publishes framework-dependent + builds the MSI
├─ installer/                           # WiX MSI (see §17)
│  ├─ AutoBright.Installer.wixproj       # WiX SDK project (x64, restores WiX from NuGet)
│  ├─ Package.wxs                       # WiX authoring (payload, shortcut, runtime check, UI)
│  └─ License.rtf                       # GPL notice shown on the welcome page
└─ src/
   └─ AutoBright/
      ├─ AutoBright.csproj
      ├─ icon.ico                       # runtime title-bar + tray icon (Content, copied to output)
      ├─ App.xaml / App.xaml.cs         # MAUI Application; creates the single window
      ├─ MauiProgram.cs                 # MauiApp builder (fonts, logging)
      ├─ MainPage.xaml / MainPage.xaml.cs   # the one and only page + all UI logic
      ├─ CommandLineOptions.cs          # CLI parsing
      ├─ StartupManager.cs              # per-user "run at OS startup" registry entry (§17)
      ├─ TrayIcon.cs                    # Win32 notification-area icon
      ├─ Controller/
      │  ├─ BrightnessController.cs      # worker thread, mapping/smoothing, orchestration
      │  ├─ ConfigData.cs               # immutable config snapshot + ManualMode enum
      │  └─ AppState.cs                 # mutable runtime state shared worker→UI
      ├─ Hardware/
      │  ├─ MonitorManager.cs            # DDC/CI enumerate/read/write/cleanup
      │  └─ YoctoSensorManager.cs        # Yoctopuce hub/sensor lifecycle + lux reads
      ├─ Settings/
      │  └─ AppSettings.cs              # JSON load/save + window geometry struct
      ├─ Resources/
      │  ├─ AppIcon/  Splash/  Images/  Fonts/  Raw/   # standard MAUI resources
      │  └─ Styles/
      │     ├─ Colors.xaml  Styles.xaml  # MAUI template defaults
      │     └─ AppStyles.xaml           # app-specific colours + Card/Stat/Field styles
      └─ Platforms/
         └─ Windows/
            ├─ App.xaml / App.xaml.cs   # MauiWinUIApplication (namespace AutoBright.WinUI)
            └─ (manifest, etc.)
```

Namespaces: root is `AutoBright`. Sub-namespaces mirror folders
(`AutoBright.Controller`, `AutoBright.Hardware`, `AutoBright.Settings`). The
Windows head uses `AutoBright.WinUI`.

---

## 3. Build Configuration (`AutoBright.csproj`)

Key `PropertyGroup` settings (all required to reproduce behaviour):

```xml
<TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
<OutputType>WinExe</OutputType>
<RootNamespace>AutoBright</RootNamespace>
<AssemblyName>AutoBright</AssemblyName>

<UseMaui>true</UseMaui>
<SingleProject>true</SingleProject>
<ImplicitUsings>enable</ImplicitUsings>
<Nullable>enable</Nullable>
<LangVersion>latest</LangVersion>

<!-- Generate C# from XAML at compile time (faster startup than runtime inflation). -->
<MauiXamlInflator>SourceGen</MauiXamlInflator>

<ApplicationTitle>AutoBright</ApplicationTitle>
<ApplicationId>com.autobright.app</ApplicationId>
<ApplicationDisplayVersion>1.0</ApplicationDisplayVersion>
<ApplicationVersion>1</ApplicationVersion>
<ApplicationIcon>icon.ico</ApplicationIcon>

<Platforms>x64;arm64</Platforms>
<RuntimeIdentifiers>win-x64;win-arm64</RuntimeIdentifiers>
<!-- WindowsAppSDKSelfContained needs a concrete RID; default to the host RID so a
     plain `dotnet build` / IDE F5 works without -r. `-r win-arm64` still overrides. -->
<RuntimeIdentifier Condition="'$(RuntimeIdentifier)' == ''">$(NETCoreSdkPortableRuntimeIdentifier)</RuntimeIdentifier>
<SupportedOSPlatformVersion>10.0.17763.0</SupportedOSPlatformVersion>
<TargetPlatformMinVersion>10.0.17763.0</TargetPlatformMinVersion>

<!-- Predictable bin\<Config>\<TFM>\ output (no win-x64 subfolder) so launch configs resolve. -->
<AppendRuntimeIdentifierToOutputPath>false</AppendRuntimeIdentifierToOutputPath>

<!-- Unpackaged (no MSIX); bundle both the .NET runtime and the Windows App SDK. -->
<WindowsPackageType>None</WindowsPackageType>
<SelfContained>true</SelfContained>
<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>
<EnableMsixTooling>true</EnableMsixTooling>
```

Item groups:

```xml
<PackageReference Include="Microsoft.Maui.Controls" Version="$(MauiVersion)" />
<PackageReference Include="Microsoft.Extensions.Logging.Debug" Version="10.0.0" />
<PackageReference Include="Yoctopuce.YoctoLib" Version="2.1.11632" />

<!-- Runtime icon for window + tray; must sit next to the exe. -->
<Content Include="icon.ico">
  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
</Content>
```

Standard MAUI resource items (`MauiIcon`, `MauiSplashScreen`, `MauiImage`,
`MauiFont`, `MauiAsset`) are present with template defaults
(`MauiIcon … Color="#512BD4"`, splash base 128×128, etc.).

### Build / run commands

```bash
# Debug build / run (host RID auto-selected)
dotnet build src/AutoBright/AutoBright.csproj
dotnet run   --project src/AutoBright/AutoBright.csproj

# Release self-contained publish for a specific arch
dotnet publish src/AutoBright/AutoBright.csproj -c Release -r win-x64
dotnet publish src/AutoBright/AutoBright.csproj -c Release -r win-arm64
```

Output path is stable: `bin/<Config>/net10.0-windows10.0.19041.0/AutoBright.exe`
(with a `win-x64/publish/` subtree for `dotnet publish`).

---

## 4. Architecture & Threading Model

Two execution contexts, communicating only through a lock-guarded state object:

```
┌────────────────────────────┐         ┌─────────────────────────────────────┐
│  UI thread (MAUI dispatcher)│        │  Worker thread (background, daemon)  │
│  - MainPage code-behind     │        │  - BrightnessController.WorkerLoop   │
│  - 500 ms refresh timer     │        │  - reads sensor, computes target,    │
│  - reads AppState snapshot  │◄───────┤    drives monitors every `interval`  │
│  - edits → ConfigData       │ lock   │  - publishes AppState under lock     │
│  - issues refresh/reconnect │───────►│  - consumes ConfigData (volatile ref)│
└────────────────────────────┘ requests└─────────────────────────────────────┘
```

Three data-flow channels between threads:

1. **Config push (UI → worker):** `BrightnessController.ApplyConfig(ConfigData)`
   stores a new immutable `ConfigData` into a `volatile` field. The worker reads
   the reference once per cycle (`ConfigData cfg = _config;`). No lock needed —
   the object is immutable and the reference swap is atomic.
2. **State publish (worker → UI):** the worker batches per-cycle updates into a
   fresh `AppState` and merges the changed fields into the shared `_state` under
   `_stateLock`. The UI calls `GetSnapshot()` which returns a deep `Clone()` under
   the same lock.
3. **Commands (UI → worker):** `RequestRefreshMonitors()` and
   `RequestReconnectSensor()` set `int` flags via `Interlocked.Exchange`; the
   worker consumes them at the top of each cycle with `Interlocked.Exchange(…, 0)`.

The worker thread is `IsBackground = true` (daemon) so it never blocks process
exit; shutdown additionally joins it with a 2 s timeout.

---

## 5. Data Model

### 5.1 `ManualMode` enum (`Controller/ConfigData.cs`)

```csharp
public enum ManualMode { Manual, Min, Max }
```

`Manual` = drive the slider value; `Min`/`Max` = pin to the configured (or
absolute) minimum/maximum.

### 5.2 `ConfigData` — immutable config snapshot

`sealed class` with `init`-only auto-properties. A new instance is published on
every change; the worker reads one reference per cycle, so updates are lock-free
and atomic.

| Property | Type | Default |
|---|---|---|
| `MinLux` | double | 50.0 |
| `MaxLux` | double | 300.0 |
| `MinBrightness` | int | 20 |
| `MaxBrightness` | int | 100 |
| `Interval` | double | 1.0 |
| `MaxBrightnessRate` | double | 25.0 |
| `LuxHysteresis` | double | 10.0 |
| `ShowLux` | bool | false |
| `MinimizeToTray` | bool | false |
| `StartMinimized` | bool | false |
| `RunAtStartup` | bool | false |
| `RemoteHub` | string | `"usb"` |
| `ManualControl` | bool | false |
| `ManualMode` | ManualMode | `Manual` |
| `ManualBrightness` | int | 50 |
| `ManualAbsolute` | bool | false |

**`ConfigData Normalized()`** returns a corrected copy (used at construction and
whenever a config is applied). Rules:

- `if (MinLux >= MaxLux) maxLux = MinLux + 1.0;`
- `if (MinBrightness >= MaxBrightness) maxBrightness = min(100, MinBrightness + 1);`
- `if (Interval <= 0) interval = 1.0;`
- `if (MaxBrightnessRate < 0) rate = 0.0;`
- `if (LuxHysteresis < 0) hysteresis = 0.0;`
- `manualBrightness = clamp(ManualBrightness, 0, 100);`

`MinBrightness` and `MinLux` are **not** themselves altered — only the dependent
maxima are pushed up. All other fields pass through unchanged.

### 5.3 `AppState` — mutable runtime snapshot (`Controller/AppState.cs`)

```csharp
public sealed class AppState
{
    public double? CurrentLux { get; set; }
    public int? TargetBrightness { get; set; }
    public string MonitorStatus { get; set; } = "Starting...";
    public string LastError { get; set; } = "";
    public List<string> MonitorList { get; set; } = new();

    public AppState Clone();   // deep-copies MonitorList into a new List<string>
}
```

`Clone()` must allocate a new `List<string>` so the UI never aliases the worker's
list.

---

## 6. `BrightnessController` (the engine)

`sealed class` in `Controller/`. Owns the worker thread, the `MonitorManager`,
and the `YoctoSensorManager`.

### 6.1 Constants & fields

```csharp
private const double RetainMonitorTimeout = 10.0;   // seconds to keep last-known monitors
private const int    MonitorStableCycles  = 2;      // consecutive successes before publishing

private readonly object _stateLock = new();
private readonly AppState _state = new();
private volatile ConfigData _config;
private volatile bool _running;
private Thread? _worker;

private readonly Stopwatch _clock = Stopwatch.StartNew();
private int _monitorSuccessCount;
private int _monitorFailureCount;
private IReadOnlyList<MonitorInfo> _lastGoodMonitors = new List<MonitorInfo>();
private double _lastGoodMonitorsTime;
private double? _smoothedBrightness;
private double? _effectiveLux;
private readonly Dictionary<IntPtr, int> _lastSentValues = new();

private int _refreshRequested;     // Interlocked flag
private int _reconnectRequested;   // Interlocked flag
```

### 6.2 Constructor

`BrightnessController(CommandLineOptions options, AppSettings settings)`:

- Build the initial `ConfigData` with **CLI > settings > default** precedence for
  each field, e.g. `MinLux = options.MinLux ?? settings.MinLux`. For `RemoteHub`:
  `options.RemoteHub ?? (string.IsNullOrWhiteSpace(settings.RemoteHub) ? "usb" : settings.RemoteHub)`.
- Manual fields and `RunAtStartup` come from settings only (no CLI):
  `ManualControl`, `ManualMode`, `ManualBrightness`, `ManualAbsolute`,
  `RunAtStartup`.
- Call `.Normalized()` on the result and store it.
- Construct `_sensorManager = new YoctoSensorManager(_config.RemoteHub, _debug)`.

### 6.3 Public API

```csharp
ConfigData Config { get; }                 // returns _config
void ApplyConfig(ConfigData config);       // _config = config.Normalized()
void Start();                              // spawn worker if not running
void Stop();                              // _running = false; worker.Join(2s)
void DestroyMonitors();                    // _monitorManager.DestroyMonitors()
AppState GetSnapshot();                    // return _state.Clone() under _stateLock
void RequestRefreshMonitors();             // set status "Refreshing monitor list...", clear list, set flag
void RequestReconnectSensor();             // set flag, set LastError "Sensor reconnect requested."
```

`Start()` creates `new Thread(WorkerLoop) { IsBackground = true, Name = "BrightnessWorker" }`.

### 6.4 Algorithms (pure helpers)

**Manual target** — `ManualTarget(ConfigData cfg) → int`:

```
Max  → ManualAbsolute ? 100 : MaxBrightness
Min  → ManualAbsolute ? 0   : MinBrightness
else → ManualAbsolute ? clamp(ManualBrightness, 0, 100)
                      : clamp(ManualBrightness, MinBrightness, MaxBrightness)
```

**Lux→brightness** — `ComputeBrightness(cfg, double? lux) → int?`:

```
lux == null            → null
lux <= MinLux          → MinBrightness
lux >= MaxLux          → MaxBrightness
spanLux <= 0           → MinBrightness            (guard)
else  result = MinBrightness + (lux - MinLux) * (MaxBrightness - MinBrightness) / (MaxLux - MinLux)
      return clamp(round(result, AwayFromZero), MinBrightness, MaxBrightness)
```

**Lux hysteresis** — `ApplyLuxHysteresis(cfg, double? lux) → double?` (mutates `_effectiveLux`):

```
lux == null                              → return _effectiveLux            (reuse committed)
_effectiveLux == null OR LuxHysteresis<=0 → _effectiveLux = lux             (commit first/disabled)
abs(lux - _effectiveLux) >= LuxHysteresis → _effectiveLux = lux             (commit if moved enough)
return _effectiveLux
```

**Brightness smoothing (slew-rate)** — `SmoothBrightness(cfg, int? target) → int?` (mutates `_smoothedBrightness`):

```
target == null                              → null
_smoothedBrightness == null OR MaxBrightnessRate<=0 → _smoothedBrightness = target; return round(it)
maxStep = MaxBrightnessRate * Interval
delta   = target - _smoothedBrightness
abs(delta) <= maxStep → _smoothedBrightness = target
else                  → _smoothedBrightness += (delta > 0 ? +maxStep : -maxStep)
return round(_smoothedBrightness, AwayFromZero)
```

**Percent → monitor raw** — `PercentToMonitorValue(MonitorInfo m, int? pct) → int?`:

```
pct == null                 → null
clamped = clamp(pct, 0, 100)
m.Max <= m.Min              → clamped              (no usable range: use % directly)
else  raw = m.Min + (clamped/100) * (m.Max - m.Min); return round(raw, AwayFromZero)
```

**Monitor raw → percent** — `MonitorValueToPercent(MonitorInfo m, int value) → int`:

```
m.Max <= m.Min → value
else           → round((value - m.Min) / (m.Max - m.Min) * 100, AwayFromZero)
```

All rounding uses `MidpointRounding.AwayFromZero`.

### 6.5 Worker loop (`WorkerLoop`)

Runs `while (_running)`. Per iteration:

1. Snapshot `ConfigData cfg = _config;`. Create an empty `AppState updates` and a
   set of `has*` booleans (`hasMonitorList`, `hasMonitorStatus`, `hasTarget`,
   `hasCurrentLux`, `hasLastError`) tracking which fields to merge.
2. **`try` block:**
   1. If `Interlocked.Exchange(ref _reconnectRequested, 0) == 1` → `_sensorManager.Reset()`.
   2. `bool forceRefresh = Interlocked.Exchange(ref _refreshRequested, 0) == 1;`
   3. `_sensorManager.Tick();`
   4. `double? lux = _sensorManager.GetLux();`
   5. `double? effectiveLux = ApplyLuxHysteresis(cfg, lux);`
   6. `int? rawTarget = cfg.ManualControl ? ManualTarget(cfg) : ComputeBrightness(cfg, effectiveLux);`
   7. `var monitors = _monitorManager.EnumerateMonitors(forceRefresh);`
   8. **Branch on `monitors.Count`:**

      **No monitors (`== 0`):**
      - `_monitorFailureCount++; _monitorSuccessCount = 0;`
      - `now = _clock.Elapsed.TotalSeconds;`
      - If `_lastGoodMonitors.Count > 0 && (now - _lastGoodMonitorsTime) < RetainMonitorTimeout`:
        build display lines from `_lastGoodMonitors` (format below, "last known"),
        compute `timeoutLeft = max(0, (int)(RetainMonitorTimeout - (now - _lastGoodMonitorsTime)))`,
        status = `"Temporary monitor loss detected. Retaining last known monitor list for {timeoutLeft}s while retrying...{targetText}"`.
      - Else: list empty, status = `"No compatible monitors found. Ensure your monitor supports Windows DDC/CI brightness control. Waiting for reconnect...{targetText}"`.
      - `targetText` = `rawTarget is int t ? $" Target brightness would be {t}%" : ""`.
      - Set `hasMonitorList = hasMonitorStatus = true`, `updates.TargetBrightness = rawTarget; hasTarget = true`.
      - **Do not touch `_smoothedBrightness`** (so ramping resumes from last applied value).

      **Monitors found (`> 0`):**
      - `_monitorSuccessCount++; _monitorFailureCount = 0;`
      - `_lastGoodMonitors = monitors; _lastGoodMonitorsTime = _clock.Elapsed.TotalSeconds;`
      - Compute `appliedTarget`:
        - If `cfg.ManualControl`: `appliedTarget = rawTarget;` and if `rawTarget is int mv` then `_smoothedBrightness = mv;` (keep baseline synced, **no smoothing**).
        - Else: `appliedTarget = SmoothBrightness(cfg, rawTarget);`
      - **Prune `_lastSentValues`:** drop every key whose handle is not in the
        current `monitors` set.
      - For each monitor, append a status line and (if `appliedTarget is int applied`) drive it:
        - `targetValue = PercentToMonitorValue(monitor, applied);`
        - if `targetValue is int rawValue`:
          - `lastMatches = _lastSentValues.TryGetValue(handle, out last) && last == rawValue;`
          - **Skip** if `lastMatches && monitor.CurBrightness == rawValue` → line `"  unchanged at {applied}% (raw {rawValue})"`.
          - Else if `_monitorManager.SetBrightness(handle, rawValue)` → `_lastSentValues[handle] = rawValue;` line `"  set to {applied}% (raw {rawValue})"`.
          - Else → `_lastSentValues.Remove(handle);` line `"  failed to set brightness"`.
      - **Publish gating:** only if `_monitorSuccessCount >= MonitorStableCycles`
        set `updates.MonitorList = lines; updates.MonitorStatus = "Monitors adjusted."; hasMonitorList = hasMonitorStatus = true;`
        (this skips a possibly-stale first read at startup).
      - `updates.TargetBrightness = appliedTarget; hasTarget = true;`

   9. `updates.CurrentLux = lux; hasCurrentLux = true;`
   10. `updates.LastError = _sensorManager.LastError ?? ""; hasLastError = true;`
3. **`catch (Exception exc)`:** `updates.LastError = $"Worker error: {exc.Message}"; hasLastError = true;`
   and if `_debug` → `Debug.WriteLine(exc)`. The loop never dies.
4. **Merge under lock:** `lock (_stateLock) { … }` copying only the fields whose
   `has*` flag is set into `_state`.
5. `Thread.Sleep(TimeSpan.FromSeconds(Math.Max(0.05, cfg.Interval)));` — the
   0.05 s floor prevents a pathological busy loop.

**Per-monitor line formats** (exact strings):

```
present:    "{name}: current {pct}% (raw {CurBrightness}), hardware range {Min}-{Max}"
last-known: "{name}: last known current {pct}% (raw {CurBrightness}), hardware range {Min}-{Max}"
```

`name` = `string.IsNullOrEmpty(monitor.Name) ? "Unnamed monitor" : monitor.Name`.
`pct` from `MonitorValueToPercent`.

---

## 7. `MonitorManager` (DDC/CI) — `Hardware/MonitorManager.cs`

### 7.1 `MonitorInfo`

```csharp
public sealed class MonitorInfo
{
    public MonitorInfo(IntPtr handle, string name, int minBrightness, int curBrightness, int maxBrightness);
    public IntPtr Handle { get; }
    public string Name { get; }
    public int MinBrightness { get; set; }   // raw device values, NOT percentages
    public int CurBrightness { get; set; }
    public int MaxBrightness { get; set; }
}
```

### 7.2 Public methods

```csharp
IReadOnlyList<MonitorInfo> EnumerateMonitors(bool refresh = false);
IReadOnlyList<MonitorInfo> RefreshMonitors();   // always full re-collect
bool SetBrightness(IntPtr handle, int brightness);
void DestroyMonitors();
```

`EnumerateMonitors`:

- If `refresh || cache.Count == 0`: `DestroyCachedMonitors(); cache = CollectMonitors();`
- Else: `RefreshCachedBrightness();` and if that empties the cache, `cache = CollectMonitors();`
- Returns a **copy** (`new List<MonitorInfo>(cache)`).

`CollectMonitors` enumerates via `EnumDisplayMonitors` with a delegate callback;
for each `HMONITOR` it gets the physical-monitor count, fetches the
`PHYSICAL_MONITOR[]`, and for each calls `GetMonitorBrightness`. Monitors that
fail the brightness query are **silently excluded**. **Hold a field reference to
the callback delegate** (`_enumProc`) so the GC cannot collect the marshalled
thunk mid-call.

`RefreshCachedBrightness` re-queries `GetMonitorBrightness` on each cached handle,
updating `Min/Cur/Max`; handles that no longer respond are dropped.

`DestroyCachedMonitors` calls `DestroyPhysicalMonitors` on the cached handles and
clears the cache. Must be invoked on drop, on forced refresh, and at app exit to
avoid leaking OS handles.

`SetBrightness` → `SetMonitorBrightness(handle, (uint)brightness)`.

### 7.3 P/Invoke signatures (reproduce exactly)

```csharp
private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprcMonitor, IntPtr dwData);

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
private struct PHYSICAL_MONITOR
{
    public IntPtr hPhysicalMonitor;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string szPhysicalMonitorDescription;
}

[DllImport("user32.dll")]
static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

[DllImport("dxva2.dll")]
static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, ref uint pdwNumberOfPhysicalMonitors);

[DllImport("dxva2.dll", CharSet = CharSet.Unicode)]
static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize, [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

[DllImport("dxva2.dll")]
static extern bool GetMonitorBrightness(IntPtr hMonitor, ref uint pdwMinimumBrightness, ref uint pdwCurrentBrightness, ref uint pdwMaximumBrightness);

[DllImport("dxva2.dll")]
static extern bool SetMonitorBrightness(IntPtr hMonitor, uint dwNewBrightness);

[DllImport("dxva2.dll")]
static extern bool DestroyPhysicalMonitors(uint dwPhysicalMonitorArraySize, [In] PHYSICAL_MONITOR[] pPhysicalMonitorArray);
```

---

## 8. `YoctoSensorManager` — `Hardware/YoctoSensorManager.cs`

Wraps the Yoctopuce .NET API (`YAPI`, `YLightSensor`, `YModule`, `YLed`,
`YColorLed`, all in the global namespace). Fields:

```csharp
public string HubUrl { get; }     // "usb" or network address; blank → "usb"
public bool Debug { get; }
public string LastError { get; private set; } = "";
private YLightSensor? _sensor;
private bool _apiReady;
private bool _ledsConfigured;
```

### 8.1 Methods

`Reset()` — `_sensor = null; _apiReady = false; _ledsConfigured = false;` (forces
full reconnect next tick).

`Tick()` — if `!_apiReady` → `RegisterHub()`, else → `UpdateDeviceList()`.

`GetLux() → double?`:

```
if (_sensor == null && !FindSensor()) return null;
try {
    if (_sensor.isOnline() == false) { _sensor = null; return null; }
    return _sensor.get_currentValue();
} catch (exc) {
    LastError = $"Unable to read sensor value: {exc.Message}";
    _sensor = null; return null;
}
```

`RegisterHub() → bool` — `YAPI.RegisterHub(HubUrl, ref err)`; on `!= YAPI.SUCCESS`
set `LastError = err` and return false; on success `_apiReady = true`. Wrapped in
try/catch (`"RegisterHub failed: …"`).

`UpdateDeviceList() → bool` — guard on `_apiReady`; `YAPI.UpdateDeviceList(ref err)`;
on failure set `LastError`, **clear `_apiReady`** (assume hub lost → re-register
next tick), return false. Try/catch likewise clears `_apiReady`.

`FindSensor() → bool`:

- If `!_apiReady && !RegisterHub()` → false.
- `_sensor = YLightSensor.FirstLightSensor();` (try/catch → `"Unable to enumerate Yocto sensors: …"`).
- If `_sensor == null || !_sensor.isOnline()` → `_sensor = null; LastError = "No Yocto light sensor found."; return false;`
- `try { _sensor.set_reportFrequency("60/m"); } catch {}` (best effort).
- **LED suppression, once per connection** (guarded by `_ledsConfigured`):
  - `module = _sensor.get_module();` then best-effort `module.set_luminosity(0)`,
    `module.set_beacon(YModule.BEACON_OFF)`, `module.saveToFlash()`.
  - `DisableYoctoLeds()` (below).
  - `_ledsConfigured = true;`
  - **Rationale:** these writes hit flash (limited write endurance) — never do
    them every cycle.
- `LastError = ""; return true;`

`DisableYoctoLeds()` (static, best-effort): iterate `YLed.FirstLed()` /
`nextLed()` calling `set_power(YLed.POWER_OFF)`; iterate `YColorLed.FirstColorLed()`
/ `nextColorLed()` calling `set_rgbColor(0)`. Every call individually
try/catch-wrapped; loops break on enumeration errors.

> Resilience: if the Yoctopuce library or driver is missing, the app must still
> launch — errors surface through `LastError`, lux reads return null, and no
> brightness is written until the sensor appears.

---

## 9. `TrayIcon` — `TrayIcon.cs`

A hand-rolled Win32 notification-area icon (no third-party dependency). The host
HWND is **subclassed** to receive the icon's mouse messages.

### 9.1 Construction & API

```csharp
public TrayIcon(IntPtr hwnd, string tooltip, Action<string> callback);
public void Show();     // Shell_NotifyIconW(NIM_ADD) once
public void Hide();     // Shell_NotifyIconW(NIM_DELETE) once
public void Cleanup();  // Hide() + RemoveWindowSubclass
```

The callback is invoked with `"restore"` (left double-click or menu Restore) or
`"quit"` (menu Exit). **Important:** the callback fires on the window message-pump
thread, so callers must marshal back to the UI thread (MainPage does this via
`Dispatcher.Dispatch`).

Constructor sets up `SetWindowSubclass(hwnd, SubclassProc, id=1, 0)` and builds a
`NOTIFYICONDATAW` with `uID = 1`, `uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP`,
`uCallbackMessage = CALLBACK_MESSAGE`, `hIcon = LoadAppIcon()`, `szTip = tooltip`.

`SubclassProc`: on `msg == CALLBACK_MESSAGE`, `evt = (int)(lParam & 0xFFFF)`;
`WM_LBUTTONDBLCLK (0x0203)` → `callback("restore")`; `WM_RBUTTONUP (0x0205)` →
`ShowMenu()`. Always chain to `DefSubclassProc`.

`ShowMenu`: `CreatePopupMenu`, append `"Restore"` (CMD 1001) and `"Exit"` (CMD
1002), `GetCursorPos`, `SetForegroundWindow(hwnd)` (so the menu dismisses
correctly), `TrackPopupMenu(TPM_RIGHTBUTTON | TPM_RETURNCMD)`, `DestroyMenu`,
dispatch the returned command to the callback.

`LoadAppIcon`: try `LoadImageW(0, "<BaseDirectory>/icon.ico", IMAGE_ICON, 0, 0,
LR_LOADFROMFILE | LR_DEFAULTSIZE)`; on failure fall back to `LoadIconW(0,
IDI_APPLICATION=32512)`.

### 9.2 Constants & interop

Constants: `WM_USER=0x0400`, `CALLBACK_MESSAGE=WM_USER+1`, `NIM_ADD=0`,
`NIM_DELETE=2`, `NIF_MESSAGE=1`, `NIF_ICON=2`, `NIF_TIP=4`,
`WM_LBUTTONDBLCLK=0x0203`, `WM_RBUTTONUP=0x0205`, `TPM_RIGHTBUTTON=0x0002`,
`TPM_RETURNCMD=0x0100`, `MF_STRING=0`, `IDI_APPLICATION=32512`, `IMAGE_ICON=1`,
`LR_LOADFROMFILE=0x10`, `LR_DEFAULTSIZE=0x40`, `CMD_RESTORE=1001`, `CMD_EXIT=1002`.

```csharp
delegate IntPtr SUBCLASSPROC(IntPtr hWnd, uint uMsg, UIntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData);

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
struct NOTIFYICONDATAW {
    uint cbSize; IntPtr hWnd; uint uID; uint uFlags; uint uCallbackMessage; IntPtr hIcon;
    [ByValTStr(128)] string szTip; uint dwState; uint dwStateMask;
    [ByValTStr(256)] string szInfo; uint uTimeoutOrVersion; [ByValTStr(64)] string szInfoTitle;
    uint dwInfoFlags; Guid guidItem; IntPtr hBalloonIcon;
}

[DllImport("shell32.dll", CharSet=Unicode)]  bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);
[DllImport("comctl32.dll")]  bool   SetWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfn, UIntPtr id, UIntPtr refData);
[DllImport("comctl32.dll")]  bool   RemoveWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfn, UIntPtr id);
[DllImport("comctl32.dll")]  IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, UIntPtr wParam, IntPtr lParam);
[DllImport("user32.dll")]    IntPtr CreatePopupMenu();
[DllImport("user32.dll", CharSet=Unicode)] bool AppendMenuW(IntPtr hMenu, uint flags, UIntPtr id, string item);
[DllImport("user32.dll")]    int    TrackPopupMenu(IntPtr hMenu, uint flags, int x, int y, int reserved, IntPtr hWnd, IntPtr rect);
[DllImport("user32.dll")]    bool   DestroyMenu(IntPtr hMenu);
[DllImport("user32.dll")]    bool   GetCursorPos(out POINT pt);
[DllImport("user32.dll")]    bool   SetForegroundWindow(IntPtr hWnd);
[DllImport("user32.dll", CharSet=Unicode)] IntPtr LoadImageW(IntPtr hinst, string name, int type, int cx, int cy, uint load);
[DllImport("user32.dll", CharSet=Unicode)] IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);
```

(`POINT` = sequential `int x, y`. Keep a field reference to the `SUBCLASSPROC`
delegate so it is not GC'd.)

---

## 10. Settings Persistence — `Settings/AppSettings.cs`

### 10.1 Types

```csharp
public sealed class WindowGeometry { public int X, Y, Width, Height; }   // (auto-props)

public sealed class AppSettings
{
    // Same scalar fields/defaults as ConfigData §5.2, plus:
    public ManualMode ManualMode { get; set; } = ManualMode.Manual;
    public WindowGeometry? WindowGeometry { get; set; }
}
```

### 10.2 Serialization

`System.Text.Json` with:

```csharp
new JsonSerializerOptions {
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
}
```

So `ManualMode` serializes as `"manual"`/`"min"`/`"max"`, and keys are snake_case
(`min_lux`, `max_brightness_rate`, `manual_control`, `window_geometry`, …).

### 10.3 Path & I/O

- `GetSettingsPath()`: `baseDir = %APPDATA% ?? UserProfile`; `dir =
  Path.Combine(baseDir, "BrightnessDimmer")`; `Directory.CreateDirectory(dir)`;
  return `Path.Combine(dir, "settings.json")`. (Note the on-disk folder/name is
  **"BrightnessDimmer"**, preserved from the original app.)
- `static AppSettings Load()`: read & deserialize if the file exists; **return
  `new AppSettings()` (defaults) on any error** — never throw.
- `void Save()`: create dir, serialize `this`, `File.WriteAllText`. **Throws on
  failure** (callers wrap in try/catch and treat saving as best-effort).

### 10.4 Example `settings.json`

```json
{
  "min_lux": 50,
  "max_lux": 300,
  "min_brightness": 20,
  "max_brightness": 100,
  "interval": 1,
  "max_brightness_rate": 25,
  "lux_hysteresis": 10,
  "show_lux": false,
  "minimize_to_tray": false,
  "start_minimized": false,
  "run_at_startup": false,
  "remote_hub": "usb",
  "manual_control": false,
  "manual_mode": "manual",
  "manual_brightness": 50,
  "manual_absolute": false,
  "window_geometry": { "x": 100, "y": 100, "width": 460, "height": 720 }
}
```

---

## 11. Command-Line Interface — `CommandLineOptions.cs`

`sealed class` with nullable value-bearing properties so "unset" falls through to
settings/defaults. `static CommandLineOptions Parse(string[] argv)` skips
`argv[0]` (the exe path) and matches:

| Option | Maps to | Parse |
|---|---|---|
| `--min-lux` | `MinLux` | double (InvariantCulture) |
| `--max-lux` | `MaxLux` | double |
| `--min-brightness` | `MinBrightness` | int |
| `--max-brightness` | `MaxBrightness` | int |
| `--remote-hub` | `RemoteHub` | string |
| `--update-rate` / `--interval` | `Interval` | double (aliases) |
| `--max-brightness-rate` | `MaxBrightnessRate` | double |
| `--lux-hysteresis` | `LuxHysteresis` | double |
| `--show-lux` | `ShowLux = true` | flag |
| `--minimize-to-tray` | `MinimizeToTray = true` | flag |
| `--start-minimized` | `StartMinimized = true` | flag |
| `--debug` | `Debug = true` | flag |

Numeric parsing uses `TryParse(..., NumberStyles.Float/Integer,
CultureInfo.InvariantCulture, …)`; a failed parse leaves the option null.
Value-taking options read the next argv token via a `NextValue(argv, ref i)`
helper. Unknown args are ignored. The manual-override settings have **no** CLI
option.

---

## 12. MAUI Application Bootstrap

### 12.1 `Platforms/Windows/App.xaml.cs`

```csharp
namespace AutoBright.WinUI;
public partial class App : MauiWinUIApplication
{
    public App() => InitializeComponent();
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
```

### 12.2 `MauiProgram.cs`

```csharp
public static MauiApp CreateMauiApp()
{
    var builder = MauiApp.CreateBuilder();
    builder.UseMauiApp<App>()
           .ConfigureFonts(fonts => {
               fonts.AddFont("OpenSans-Regular.ttf",  "OpenSansRegular");
               fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
           });
#if DEBUG
    builder.Logging.AddDebug();
#endif
    return builder.Build();
}
```

### 12.3 `App.xaml.cs` (cross-platform Application)

```csharp
public partial class App : Application
{
    public App() => InitializeComponent();
    protected override Window CreateWindow(IActivationState? activationState)
    {
        var options = CommandLineOptions.Parse(Environment.GetCommandLineArgs());
        return new Window(new MainPage(options)) { Title = "AutoBright" };
    }
}
```

`App.xaml` merges `Resources/Styles/Colors.xaml`, `Styles.xaml`, and the
app-specific `AppStyles.xaml`.

---

## 13. UI — `MainPage.xaml` + `MainPage.xaml.cs`

A single `ContentPage` (`Title="AutoBright"`) wrapped in a `ScrollView`
(`Padding=20`) containing a `VerticalStackLayout` (`Spacing=16`) of cards. The
page background uses `AppThemeBinding` to `PageBgLight/Dark` (overridden to
transparent at runtime when Mica is applied).

### 13.1 Visual tree (named controls an AI must wire up)

- **Header card** — primary-coloured rounded `Border` with a ☀ glyph, "AutoBright"
  title, "Ambient-light brightness control" subtitle.
- **Live readout** — two-column `Grid` of `Card` borders:
  - `LuxValue` (caption "CURRENT LUX"), `TargetValue` (caption "TARGET BRIGHTNESS").
- **Manual override card** (`SectionHeader` "MANUAL OVERRIDE"):
  - `ManualCheck` (CheckBox) `CheckedChanged=OnManualCheckChanged`, label "Manual brightness control".
  - `ManualPanel` (VerticalStackLayout, `IsVisible=False`):
    - `AbsoluteCheck` `CheckedChanged=OnAbsoluteChanged`, label "Use absolute 0–100% range (ignore configured min/max)".
    - Radio group `GroupName="ManualMode"`: `MinRadio` "Min", `MaxRadio` "Max",
      `ManualRadio` "Manual" — all `CheckedChanged=OnManualModeChanged`.
    - `Grid`: `ManualSlider` (`Minimum=0 Maximum=100`, `ValueChanged=OnManualSliderChanged`)
      + `ManualValueLabel` (right-aligned `"50%"`).
- **Settings card** (`SectionHeader` "SETTINGS"):
  - Two-column grid of numeric `Entry` fields (`Keyboard="Numeric"`), each with
    `Completed=OnSettingCompleted` and `Unfocused=OnSettingCommitted`:
    `MinLuxBox`, `MaxLuxBox`, `MinBrightnessBox`, `MaxBrightnessBox`,
    `IntervalBox`, `MaxRateBox`, `HysteresisBox`.
  - A 1px divider `BoxView`.
  - Three checkboxes, all `CheckedChanged=OnSettingCommitted`: `TrayCheck`
    "Minimize to tray on close", `RunAtStartupCheck` "Run at Windows startup",
    `StartMinimizedCheck` "Start minimized to tray".
  - `SettingsStatus` label (`IsVisible=False`) for inline validation/save feedback.
- **Monitors card** (`SectionHeader` "MONITORS"):
  - `WorkerStatus` label.
  - `MonitorList` `CollectionView` (`HeightRequest=180`, Consolas 13, items are
    `string`) inside a rounded list-background border.
  - Buttons: `RefreshButton` "Refresh monitors" (`Clicked=OnRefreshClick`),
    `ReconnectButton` "Reconnect sensor" (`Clicked=OnReconnectClick`, outline
    style), `HideButton` "Hide to tray" (`Clicked=OnHideClick`, outline style).

### 13.2 Code-behind state

```csharp
private readonly BrightnessController _controller;
private readonly AppSettings _settings;
private Microsoft.UI.Xaml.Window? _nativeWindow;
private AppWindow? _appWindow;
private IntPtr _hwnd;
private IDispatcherTimer? _uiTimer, _geometrySaveTimer, _settingsSaveTimer;
private TrayIcon? _tray;
private WindowGeometry? _lastGoodGeometry;
private bool _shuttingDown;
private bool _suppressCommit = true;     // gate auto-save during programmatic field population
private readonly ObservableCollection<string> _monitorLines = new();
private static readonly Color ErrorColor = Color.FromArgb("#E74C3C");
private const int MinRestoreSize = 200;        // reject smaller saved sizes
private const int OffScreenSentinel = -30000;  // reject Windows' (-32000,-32000) minimized park
```

### 13.3 Construction & load sequence

Constructor: `_settings = AppSettings.Load()`; `_controller = new
BrightnessController(options, _settings)`; `InitializeComponent()`;
`MonitorList.ItemsSource = _monitorLines`; seed `_lastGoodGeometry` from
`_settings.WindowGeometry` **only if valid** (width/height ≥ `MinRestoreSize`, x/y
> `OffScreenSentinel`); `PopulateFields()`; subscribe `Loaded += OnPageLoaded`.

`OnPageLoaded` (once): unsubscribe; `WireNativeWindow()`; create the two
debounced save timers (400 ms, non-repeating) → `SaveSettings`; `_controller.Start()`;
set `_suppressCommit = false`; create the repeating 500 ms `_uiTimer` →
`RefreshUi` and start it; reconcile the OS startup entry with the saved setting
(`StartupManager.Apply(Config.RunAtStartup, Config.StartMinimized)`, §17); if
`Config.StartMinimized` → `HideToTray()`.

`WireNativeWindow`: resolve `this.Window?.Handler?.PlatformView as
Microsoft.UI.Xaml.Window`; get `_hwnd` via `WinRT.Interop.WindowNative.GetWindowHandle`;
`_appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd))`;
`TrySetWindowIcon()` (`_appWindow.SetIcon("<BaseDirectory>/icon.ico")`);
`TrySetMicaBackdrop()` (if `MicaController.IsSupported()` set
`_nativeWindow.SystemBackdrop = new MicaBackdrop()` and `BackgroundColor =
Transparent`); `RestoreGeometry()`; subscribe `_appWindow.Closing +=
OnAppWindowClosing` and `_appWindow.Changed += OnAppWindowChanged`. Each step is
try/catch-guarded so an unsupported feature degrades gracefully.

`RestoreGeometry`: if a valid saved rectangle exists, clamp it onto a current
display — `DisplayArea.GetFromRect(saved, Nearest).WorkArea`, then
`width=min(geom.W, work.W)`, `height=min(geom.H, work.H)`,
`x=clamp(geom.X, work.X, work.X+work.W-width)`, similarly `y` — and
`MoveAndResize`. Otherwise first-run default `Resize(460×720)`.

### 13.4 Field population

`PopulateFields()` writes every entry from `_controller.Config` using
`CultureInfo.InvariantCulture`, sets `TrayCheck/RunAtStartupCheck/StartMinimizedCheck/
AbsoluteCheck/ManualCheck`, selects the right manual-mode radio, calls
`UpdateManualSliderRange()`, clamps and sets `ManualSlider.Value`, updates
`ManualValueLabel`, and calls `UpdateManualVisibility()`. Runs while
`_suppressCommit == true`.

### 13.5 Auto-commit & validation

`OnSettingCommitted` / `OnSettingCompleted` → `CommitSettings()`.

`CommitSettings()`: bail if `_suppressCommit`; `TryBuildConfig(out updated)` —
bail on failure; bail if `ConfigEquals(_controller.Config, updated)` (no change);
else `_controller.ApplyConfig(updated)`,
`StartupManager.Apply(updated.RunAtStartup, updated.StartMinimized)` (§17),
`SaveSettings()`, `SetSettingsStatus("Settings saved.", error:false)`,
`UpdateManualSliderRange()`.

`TryBuildConfig(out ConfigData)`: parse all seven numeric fields
(`TryParseField` doubles, `TryParseInt` ints, InvariantCulture). On any parse
failure: `SetSettingsStatus("Enter valid numeric values for all fields.", error:true)`,
return false. Then validate (first failing message wins):

- `minLux >= maxLux` → "Minimum lux must be lower than maximum lux."
- brightness out of 0–100 → "Brightness values must be between 0 and 100."
- `minBrightness >= maxBrightness` → "Minimum brightness must be lower than maximum brightness."
- `interval <= 0` → "Update rate must be greater than zero."
- `maxRate < 0` → "Max change rate cannot be negative (use 0 to disable smoothing)."
- `hysteresis < 0` → "Lux hysteresis cannot be negative (use 0 to disable it)."

On success build a new `ConfigData` carrying the parsed numerics, the checkboxes
(`MinimizeToTray=TrayCheck`, `RunAtStartup=RunAtStartupCheck`,
`StartMinimized=StartMinimizedCheck`, `ManualControl=ManualCheck`,
`ManualAbsolute=AbsoluteCheck`), `ManualMode=GetSelectedManualMode()`,
`ManualBrightness=round(ManualSlider.Value)`, and preserving non-UI fields
(`ShowLux`, `RemoteHub`) from the previous config.

`ConfigEquals` compares every field **except** `ShowLux`/`RemoteHub` (those aren't
UI-editable).

`SetSettingsStatus(msg, error)` sets text/visibility and colour (`ErrorColor`
when error, else the theme secondary-text colour from `SecondaryTextColor()`).

### 13.6 Manual-override handlers

- `OnManualCheckChanged`: `UpdateManualVisibility()`; if not suppressed and
  manual is now on, **seed** the slider with `GetSnapshot().TargetBrightness ??
  ManualSlider.Value` (clamped, written under a temporary `_suppressCommit`),
  then `CommitSettings()`.
- `OnManualModeChanged`: ignore the de-selected radio (`if (!e.Value) return;`);
  `UpdateManualVisibility()`; `CommitSettings()`.
- `OnAbsoluteChanged`: `UpdateManualSliderRange()`; `CommitSettings()`.
- `UpdateManualSliderRange()`: target range is absolute `0..100` or
  `Config.MinBrightness..MaxBrightness` (force `max = min+1` if degenerate). Set
  Min/Max in an order that never passes through `max < min`, clamp the current
  value, refresh `ManualValueLabel`. Wrapped in `_suppressCommit` save/restore.
- `OnManualSliderChanged`: update `ManualValueLabel`; if not suppressed, apply
  **live** (`TryBuildConfig` + `ApplyConfig` if changed) and **debounce** the disk
  write (`_settingsSaveTimer.Stop(); Start()`).
- `UpdateManualVisibility()`: `ManualPanel.IsVisible = ManualCheck.IsChecked`;
  `ManualSlider.IsEnabled = ManualCheck.IsChecked && ManualRadio.IsChecked`.

### 13.7 Buttons

`OnRefreshClick → _controller.RequestRefreshMonitors()`;
`OnReconnectClick → _controller.RequestReconnectSensor()`;
`OnHideClick → HideToTray()`.

### 13.8 Tray & window lifecycle

`HideToTray()`: lazily create `_tray = new TrayIcon(_hwnd, "Brightness Dimmer",
OnTrayAction)` and `Show()`; `_appWindow.Hide()`.

`RestoreFromTray()`: `_tray.Cleanup(); _tray = null; _appWindow.Show();
_nativeWindow.Activate()`.

`OnTrayAction(action)`: `Dispatcher.Dispatch(() => { "restore" → RestoreFromTray();
"quit" → Shutdown(quit:true); })` — marshals off the message-pump thread.

`OnAppWindowClosing`: if `_shuttingDown` let it close; else if
`Config.MinimizeToTray` → `args.Cancel = true; HideToTray();`; else
`Shutdown(quit:false)` (cleanup, then let the window close).

`OnAppWindowChanged`: if not suppressed/shutting down and
`DidPositionChange||DidSizeChange`, debounce `_geometrySaveTimer`.

`Shutdown(quit)`: idempotent via `_shuttingDown`; stop all three timers;
`_controller.Stop()`; `_controller.DestroyMonitors()`; `_tray?.Cleanup()`; if
`quit` → `Application.Current?.Quit()`.

### 13.9 Persistence helper

`SaveSettings()`: capture geometry **only when on-screen** — read
`_appWindow.Position/Size`, detect minimized via
`(_appWindow.Presenter as OverlappedPresenter)?.State ==
OverlappedPresenterState.Minimized`, and accept only if not minimized, position >
`OffScreenSentinel`, and size ≥ `MinRestoreSize` → update `_lastGoodGeometry`.
Build an `AppSettings` from `_controller.Config` + `_lastGoodGeometry` and
`toSave.Save()` in a try/catch (best-effort).

### 13.10 UI refresh

`RefreshUi()` (every 500 ms): snapshot via `GetSnapshot()`.
- `LuxValue.Text` = `Config.ShowLux ? (CurrentLux?.ToString("F1") ?? "n/a") : "hidden"`.
- `TargetValue.Text` = `TargetBrightness is int t ? $"{t}%" : "n/a"`.
- `WorkerStatus.Text` = `LastError` present ? `"{MonitorStatus}  •  {LastError}"` :
  `MonitorStatus`; colour `ErrorColor` on error else secondary.
- `UpdateMonitorList(MonitorList)`.

`UpdateMonitorList(lines)`: diff against `_monitorLines` **in place** — if counts
match and all equal, return (no-op); otherwise overwrite changed indices, append
extras, and trim surplus from the tail. This prevents `CollectionView` flicker and
scroll-position loss.

`SecondaryTextColor()`: pick `SecondaryTextDark`/`SecondaryTextLight` resource by
`Application.Current.RequestedTheme`, falling back to LightGray/Gray.

---

## 14. Styling — `Resources/Styles/AppStyles.xaml`

App-specific palette (light/dark pairs) layered on the MAUI template's
`Colors.xaml`/`Styles.xaml`:

| Key | Light | Dark |
|---|---|---|
| `PageBgLight/Dark` | `#F3F3F3` | `#141414` |
| `CardBgLight/Dark` | `#FFFFFF` | `#212121` |
| `CardStrokeLight/Dark` | `#E1E1E1` | `#383838` |
| `SecondaryTextLight/Dark` | `#6E6E6E` | `#ACACAC` |
| `ListBgLight/Dark` | `#FAFAFA` | `#1A1A1A` |
| `ErrorColor` | `#E74C3C` | — |

Styles: `Card` (Border, themed bg/stroke, 1px stroke, 16 padding, 8 corner
radius), `SectionHeader` (12pt bold, CharacterSpacing 1, secondary colour),
`StatCaption` (11pt bold, spacing 1.2), `StatValue` (26pt bold), `FieldCaption`
(12pt secondary), `SecondaryText` (secondary colour label).

---

## 15. Behavioural Constants (single source of truth)

| Constant | Value | Where |
|---|---|---|
| Default min/max lux | 50.0 / 300.0 | `ConfigData` |
| Default min/max brightness | 20 / 100 % | `ConfigData` |
| Default interval | 1.0 s | `ConfigData` |
| Default max brightness rate | 25.0 %/s (0 disables) | `ConfigData` |
| Default lux hysteresis | 10.0 lux (0 disables) | `ConfigData` |
| Default manual control / mode / brightness / absolute | off / manual / 50 % / off | `ConfigData` |
| Default run at startup / start minimized | off / off | `ConfigData` |
| Monitor-retain grace period | 10.0 s | `RetainMonitorTimeout` |
| Stable cycles before publishing | 2 | `MonitorStableCycles` |
| UI refresh period | 500 ms | `_uiTimer` |
| Settings/geometry save debounce | 400 ms | save timers |
| Min worker sleep (interval floor) | 0.05 s | `WorkerLoop` |
| Worker-join timeout on shutdown | 2.0 s | `Stop()` |
| Sensor report frequency | 60/m | `YoctoSensorManager` |
| Min restorable window size | 200 px | `MinRestoreSize` |
| Off-screen sentinel reject threshold | −30000 | `OffScreenSentinel` |
| First-run window size | 460 × 720 | `RestoreGeometry` |
| Settings folder / file | `%APPDATA%/BrightnessDimmer/settings.json` | `AppSettings` |
| Window title / tray tooltip | "AutoBright" / "Brightness Dimmer" | App / TrayIcon |

---

## 16. Reconstruction Checklist

1. Create the MAUI single-project with the §3 csproj; add the three NuGet refs;
   drop `icon.ico` next to the project and mark it Content/copy-to-output.
2. Implement the pure data types: `ManualMode`, `ConfigData` (+`Normalized`),
   `AppState` (+`Clone`).
3. Implement `MonitorManager` with the §7 P/Invoke and caching semantics.
4. Implement `YoctoSensorManager` with the §8 lifecycle and once-per-connection
   LED suppression.
5. Implement `BrightnessController` with the §6 algorithms and worker loop,
   matching the exact status/line strings and the success/failure/retain logic.
6. Implement `AppSettings` JSON load/save (§10) and `CommandLineOptions` (§11).
7. Implement `TrayIcon` (§9) and `StartupManager` (§17).
8. Wire the MAUI bootstrap (§12) and build `MainPage` XAML + code-behind (§13),
   including auto-commit, manual override, the run-at-startup / start-minimized
   options, geometry persistence, tray, and the flicker-free monitor list.
9. Add `AppStyles.xaml` (§14).
10. Verify against the behavioural contract in SPECIFICATION.md, especially the
    resilience requirements (sensor absent, monitors lost, write failures, worker
    exceptions — all non-fatal).
11. (Optional, for distribution) Build the WiX MSI (§18) via `build-installer.ps1`.

---

## 17. `StartupManager` — `StartupManager.cs`

Manages the per-user "run at OS startup" entry. Static helper, best-effort
(registry failures are swallowed and never crash the app).

```csharp
private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
private const string ValueName  = "AutoBright";

public static void Apply(bool runAtStartup, bool startMinimized);
public static bool IsEnabled();
```

`Apply`:

- Open/create `HKCU\…\Run` (writable).
- If `runAtStartup`: `exe = Environment.ProcessPath` (the running executable); if
  empty, return. Write `ValueName` = `"\"{exe}\""`, with ` --start-minimized`
  appended when `startMinimized` is true (so a login launch goes straight to the
  tray; the switch is parsed by `CommandLineOptions`, §11).
- Else: delete `ValueName` if present (`throwOnMissingValue: false`).

`IsEnabled`: true if the `Run` value exists.

Called from `CommitSettings` (every settings change) and once in `OnPageLoaded`
(§13.3/§13.5), so the stored command tracks the current exe path across moves and
reinstalls. Uses `Microsoft.Win32.Registry` (available on the `-windows` TFM; no
extra package reference).

---

## 18. MSI Installer — `installer/` + `build-installer.ps1`

A WiX v5 installer that wraps a **framework-dependent** publish. The .NET runtime
is deliberately not bundled; the Windows App SDK is (so the only external
dependency is the .NET Desktop Runtime), and the installer recognises that runtime
as a prerequisite.

### 18.1 `build-installer.ps1`

Params `-Configuration Release -Rid win-x64 -Version 1.0.0.0`. Steps:

1. Clean `artifacts/publish`, then publish framework-dependent:
   `dotnet publish … -r $Rid --self-contained false -p:WindowsAppSDKSelfContained=true -o artifacts/publish`
   (the `--self-contained false` overrides the csproj's self-contained default).
2. Force a clean MSI rebuild (WiX's incremental build keys off the `.wxs`/
   `.wixproj`, **not** the harvested folder's contents, so stale payloads must be
   avoided): delete `installer/bin` + `installer/obj`, then
   `dotnet build installer/AutoBright.Installer.wixproj -c $Configuration -t:Rebuild -p:ProductVersion=$Version -p:AppPublishDir=artifacts/publish`.
3. Report the produced `.msi` under `installer/bin`.

### 18.2 `AutoBright.Installer.wixproj`

`<Project Sdk="WixToolset.Sdk/5.0.2">`, `OutputType=Package`. Key properties:

- `InstallerPlatform=x64` — so harvested components default to 64-bit and install
  cleanly under the 64-bit Program Files (avoids ICE80).
- `SuppressValidation=true` — the harvested Microsoft binaries trip benign
  ICE03/ICE60 language warnings; the hand-authored parts are kept ICE-clean.
- `DefineConstants=ProductVersion=…;AppPublishDir=…` — exposed to the WiX
  preprocessor as `$(var.ProductVersion)` / `$(var.AppPublishDir)`.
- `PackageReference WixToolset.UI.wixext 5.0.2` — the setup-wizard dialog set.

Only the .NET SDK is needed; the WiX SDK is restored from NuGet by `dotnet build`.

### 18.3 `Package.wxs`

- `Package` perMachine, x64, UpgradeCode fixed; `MajorUpgrade`; ARP icon
  (`icon.ico`) + help link.
- **Runtime dependency check:** a `Property DOTNETDESKTOPRUNTIME` with nested
  `DirectorySearch` walking `[ProgramFiles64Folder]dotnet` → `shared` →
  `Microsoft.WindowsDesktop.App`; a `Launch` condition
  `Installed OR DOTNETDESKTOPRUNTIME` whose message points at
  `https://dotnet.microsoft.com/download/dotnet/10.0` when the runtime is absent.
- **UI:** `<ui:WixUI Id="WixUI_Minimal" />` + `WixUILicenseRtf` = `License.rtf`
  (GPL notice). Gives welcome/license → progress → **"Setup completed"** dialog.
- **Payload:** `<Files Include="$(var.AppPublishDir)\**" />` harvested into
  `INSTALLFOLDER` (`ProgramFiles64Folder\AutoBright`); `AutoBright.exe` lands at
  the root.
- **Shortcut:** a Start Menu shortcut component (`ProgramMenuFolder\AutoBright`)
  targeting `[INSTALLFOLDER]AutoBright.exe`, keyed off an HKCU registry value
  (standard non-advertised-shortcut pattern), with `RemoveFolder` on uninstall.

Output: `installer/bin/Release/AutoBright-<version>-win-x64.msi`.
