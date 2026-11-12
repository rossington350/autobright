using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using AutoBright.Hardware;
using AutoBright.Settings;

namespace AutoBright.Controller;

/// <summary>
/// Owns the worker thread that reads ambient light, maps it to a target
/// brightness, and drives every connected monitor. All UI-independent behaviour
/// of the original BrightnessDimmerApp lives here; the UI only reads snapshots
/// and pushes configuration changes.
/// </summary>
public sealed class BrightnessController
{
    private const double RetainMonitorTimeout = 10.0;
    private const int MonitorStableCycles = 2;

    private readonly bool _debug;
    private readonly MonitorManager _monitorManager = new();
    private readonly YoctoSensorManager _sensorManager;

    private readonly object _stateLock = new();
    private readonly AppState _state = new();

    private volatile ConfigData _config;
    private volatile bool _running;
    private Thread? _worker;

    // Worker-only state.
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _monitorSuccessCount;
    private IReadOnlyList<MonitorInfo> _lastGoodMonitors = new List<MonitorInfo>();
    private double _lastGoodMonitorsTime;
    private double? _smoothedBrightness;
    private double? _effectiveLux;
    private readonly Dictionary<IntPtr, int> _lastSentValues = new();

    // Cross-thread requests.
    private int _refreshRequested;
    private int _reconnectRequested;

    public BrightnessController(CommandLineOptions options, AppSettings settings)
    {
        _debug = options.Debug;

        _config = new ConfigData
        {
            MinLux = options.MinLux ?? settings.MinLux,
            MaxLux = options.MaxLux ?? settings.MaxLux,
            MinBrightness = options.MinBrightness ?? settings.MinBrightness,
            MaxBrightness = options.MaxBrightness ?? settings.MaxBrightness,
            Interval = options.Interval ?? settings.Interval,
            MaxBrightnessRate = options.MaxBrightnessRate ?? settings.MaxBrightnessRate,
            LuxHysteresis = options.LuxHysteresis ?? settings.LuxHysteresis,
            ShowLux = options.ShowLux ?? settings.ShowLux,
            MinimizeToTray = options.MinimizeToTray ?? settings.MinimizeToTray,
            StartMinimized = options.StartMinimized ?? settings.StartMinimized,
            RunAtStartup = settings.RunAtStartup,
            RemoteHub = options.RemoteHub ?? (string.IsNullOrWhiteSpace(settings.RemoteHub) ? "usb" : settings.RemoteHub),
            ManualControl = settings.ManualControl,
            ManualMode = settings.ManualMode,
            ManualBrightness = settings.ManualBrightness,
            ManualAbsolute = settings.ManualAbsolute,
        }.Normalized();

        _sensorManager = new YoctoSensorManager(_config.RemoteHub, _debug);
    }

    public ConfigData Config => _config;

    public void ApplyConfig(ConfigData config) => _config = config.Normalized();

    public void Start()
    {
        if (_running)
        {
            return;
        }
        _running = true;
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "BrightnessWorker" };
        _worker.Start();
    }

    public void Stop()
    {
        _running = false;
        Thread? worker = _worker;
        if (worker != null && worker.IsAlive)
        {
            worker.Join(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>
    /// Release the physical-monitor handles. Only safe once the worker has
    /// exited: <see cref="Stop"/> waits at most 2 s, and a slow DDC/CI call can
    /// outlast that. If the worker is still running, skip the cleanup instead of
    /// racing it over the same handles; the OS reclaims them at process exit.
    /// </summary>
    public void DestroyMonitors()
    {
        if (_worker is { IsAlive: true })
        {
            return;
        }
        _monitorManager.DestroyMonitors();
    }

    public AppState GetSnapshot()
    {
        lock (_stateLock)
        {
            return _state.Clone();
        }
    }

    public void RequestRefreshMonitors()
    {
        lock (_stateLock)
        {
            _state.MonitorStatus = "Refreshing monitor list...";
            _state.Monitors = new List<MonitorRow>();
        }
        Interlocked.Exchange(ref _refreshRequested, 1);
    }

    public void RequestReconnectSensor()
    {
        Interlocked.Exchange(ref _reconnectRequested, 1);
        lock (_stateLock)
        {
            _state.LastError = "Sensor reconnect requested.";
        }
    }

    // ---- Mapping / smoothing helpers --------------------------------------

    /// <summary>
    /// The target brightness while manual control is active. Min/Max reuse the
    /// configured brightness limits; Manual uses the slider value.
    /// </summary>
    internal static int ManualTarget(ConfigData cfg) => cfg.ManualMode switch
    {
        ManualMode.Max => cfg.ManualAbsolute ? 100 : cfg.MaxBrightness,
        ManualMode.Min => cfg.ManualAbsolute ? 0 : cfg.MinBrightness,
        _ => cfg.ManualAbsolute
            ? Math.Max(0, Math.Min(100, cfg.ManualBrightness))
            : Math.Max(cfg.MinBrightness, Math.Min(cfg.MaxBrightness, cfg.ManualBrightness)),
    };

    internal static int? ComputeBrightness(ConfigData cfg, double? luxValue)
    {
        if (luxValue is not double lux || double.IsNaN(lux))
        {
            return null;
        }
        if (lux <= cfg.MinLux)
        {
            return cfg.MinBrightness;
        }
        if (lux >= cfg.MaxLux)
        {
            return cfg.MaxBrightness;
        }
        double spanLux = cfg.MaxLux - cfg.MinLux;
        double spanBrightness = cfg.MaxBrightness - cfg.MinBrightness;
        if (!(spanLux > 0) || !double.IsFinite(spanLux))
        {
            // Normalized() guarantees finite MinLux < MaxLux, but this helper can
            // also be called with a raw config (e.g. from tests). Written as
            // !(x > 0) so a NaN span is caught as well.
            return cfg.MinBrightness;
        }
        double result = cfg.MinBrightness + (lux - cfg.MinLux) * spanBrightness / spanLux;
        int rounded = (int)Math.Round(result, MidpointRounding.AwayFromZero);
        return Math.Max(cfg.MinBrightness, Math.Min(cfg.MaxBrightness, rounded));
    }

    internal double? ApplyLuxHysteresis(ConfigData cfg, double? luxValue)
    {
        if (luxValue is not double lux || double.IsNaN(lux))
        {
            // Treat a NaN reading as "no reading" so it can't poison the
            // retained effective value (every later comparison would fail).
            return _effectiveLux;
        }
        if (_effectiveLux is not double effective || cfg.LuxHysteresis <= 0)
        {
            _effectiveLux = lux;
        }
        else if (Math.Abs(lux - effective) >= cfg.LuxHysteresis)
        {
            _effectiveLux = lux;
        }
        return _effectiveLux;
    }

    internal int? SmoothBrightness(ConfigData cfg, int? target)
    {
        if (target is not int targetValue)
        {
            return null;
        }
        if (_smoothedBrightness is not double current || cfg.MaxBrightnessRate <= 0)
        {
            _smoothedBrightness = targetValue;
            return (int)Math.Round(_smoothedBrightness.Value, MidpointRounding.AwayFromZero);
        }
        double maxStep = cfg.MaxBrightnessRate * cfg.Interval;
        double delta = targetValue - current;
        if (Math.Abs(delta) <= maxStep)
        {
            _smoothedBrightness = targetValue;
        }
        else
        {
            _smoothedBrightness = current + (delta > 0 ? maxStep : -maxStep);
        }
        return (int)Math.Round(_smoothedBrightness.Value, MidpointRounding.AwayFromZero);
    }

    internal static int? PercentToMonitorValue(MonitorInfo monitor, int? brightnessPercent)
    {
        if (brightnessPercent is not int percent)
        {
            return null;
        }
        int clamped = Math.Max(0, Math.Min(100, percent));
        if (monitor.MaxBrightness <= monitor.MinBrightness)
        {
            return clamped;
        }
        double fraction = clamped / 100.0;
        double raw = monitor.MinBrightness + fraction * (monitor.MaxBrightness - monitor.MinBrightness);
        return (int)Math.Round(raw, MidpointRounding.AwayFromZero);
    }

    internal static int MonitorValueToPercent(MonitorInfo monitor, int value)
    {
        if (monitor.MaxBrightness <= monitor.MinBrightness)
        {
            return value;
        }
        double percent = (value - monitor.MinBrightness) / (double)(monitor.MaxBrightness - monitor.MinBrightness) * 100.0;
        return (int)Math.Round(percent, MidpointRounding.AwayFromZero);
    }

    // ---- Worker loop ------------------------------------------------------

    private void WorkerLoop()
    {
        while (_running)
        {
            var updates = new AppState();
            bool hasMonitorInfo = false;
            bool hasTarget = false;
            bool hasCurrentLux = false;
            bool hasLastError = false;
            ConfigData cfg = _config;

            try
            {
                if (Interlocked.Exchange(ref _reconnectRequested, 0) == 1)
                {
                    _sensorManager.Reset();
                }
                bool forceRefresh = Interlocked.Exchange(ref _refreshRequested, 0) == 1;

                _sensorManager.Tick();
                double? lux = _sensorManager.GetLux();
                double? effectiveLux = ApplyLuxHysteresis(cfg, lux);
                int? rawTarget = cfg.ManualControl
                    ? ManualTarget(cfg)
                    : ComputeBrightness(cfg, effectiveLux);
                IReadOnlyList<MonitorInfo> monitors = _monitorManager.EnumerateMonitors(forceRefresh);

                hasMonitorInfo = monitors.Count == 0
                    ? HandleNoMonitors(rawTarget, updates)
                    : HandleMonitorsFound(cfg, monitors, rawTarget, updates);
                hasTarget = true;

                updates.CurrentLux = lux;
                hasCurrentLux = true;
                updates.LastError = _sensorManager.LastError ?? "";
                hasLastError = true;
            }
            catch (Exception exc)
            {
                updates.LastError = $"Worker error: {exc.Message}";
                hasLastError = true;
                if (_debug)
                {
                    Debug.WriteLine(exc);
                }
            }

            lock (_stateLock)
            {
                if (hasCurrentLux)
                {
                    _state.CurrentLux = updates.CurrentLux;
                }
                if (hasTarget)
                {
                    _state.TargetBrightness = updates.TargetBrightness;
                }
                if (hasLastError)
                {
                    _state.LastError = updates.LastError;
                }
                if (hasMonitorInfo)
                {
                    _state.MonitorStatus = updates.MonitorStatus;
                    _state.Monitors = updates.Monitors;
                }
            }

            Thread.Sleep(TimeSpan.FromSeconds(Math.Max(0.05, cfg.Interval)));
        }
    }

    /// <summary>
    /// No monitors responded this cycle. Shows the last-known monitor list while
    /// within the retain grace period, otherwise reports the loss. Smoothing
    /// state is deliberately left untouched so ramping resumes from the last
    /// applied value once monitors return. Always publishes monitor info, so it
    /// always returns true (the return value matches HandleMonitorsFound).
    /// </summary>
    private bool HandleNoMonitors(int? rawTarget, AppState updates)
    {
        _monitorSuccessCount = 0;
        double now = _clock.Elapsed.TotalSeconds;
        string targetText = rawTarget is int t ? $" Target brightness would be {t}%" : "";

        if (_lastGoodMonitors.Count > 0 && (now - _lastGoodMonitorsTime) < RetainMonitorTimeout)
        {
            var rows = new List<MonitorRow>();
            foreach (var monitor in _lastGoodMonitors)
            {
                rows.Add(ToRow(monitor) with { IsStale = true });
            }
            int timeoutLeft = Math.Max(0, (int)(RetainMonitorTimeout - (now - _lastGoodMonitorsTime)));
            updates.Monitors = rows;
            updates.MonitorStatus = $"Temporary monitor loss detected. Retaining last known monitor list for {timeoutLeft}s while retrying...{targetText}";
        }
        else
        {
            updates.MonitorStatus = $"No compatible monitors found. Ensure your monitor supports Windows DDC/CI brightness control. Waiting for reconnect...{targetText}";
            updates.Monitors = new List<MonitorRow>();
        }
        updates.TargetBrightness = rawTarget;
        return true;
    }

    /// <summary>
    /// At least one monitor responded this cycle: compute the applied target
    /// (bypassing smoothing under manual control), drive every monitor, and
    /// publish the monitor list once the success-streak stabilization
    /// threshold is reached. Returns whether monitor info was published.
    /// </summary>
    private bool HandleMonitorsFound(ConfigData cfg, IReadOnlyList<MonitorInfo> monitors, int? rawTarget, AppState updates)
    {
        _monitorSuccessCount++;
        _lastGoodMonitors = monitors;
        _lastGoodMonitorsTime = _clock.Elapsed.TotalSeconds;

        // Manual control applies immediately, bypassing the change-rate limit;
        // keep the smoothing baseline in sync so a later switch back to
        // automatic ramps from the current value.
        int? appliedTarget;
        if (cfg.ManualControl)
        {
            appliedTarget = rawTarget;
            if (rawTarget is int manualValue)
            {
                _smoothedBrightness = manualValue;
            }
        }
        else
        {
            appliedTarget = SmoothBrightness(cfg, rawTarget);
        }

        PruneLastSentValues(monitors);

        var rows = new List<MonitorRow>();
        foreach (var monitor in monitors)
        {
            MonitorRow row = ToRow(monitor);
            if (appliedTarget is int applied)
            {
                row = DriveMonitor(monitor, applied, row);
            }
            rows.Add(row);
        }

        updates.TargetBrightness = appliedTarget;
        if (_monitorSuccessCount < MonitorStableCycles)
        {
            return false;
        }
        updates.Monitors = rows;
        updates.MonitorStatus = "Monitors adjusted.";
        return true;
    }

    private static MonitorRow ToRow(MonitorInfo monitor) => new(
        Name: string.IsNullOrWhiteSpace(monitor.Name) ? "Unnamed monitor" : monitor.Name.Trim(),
        CurrentPercent: MonitorValueToPercent(monitor, monitor.CurBrightness),
        CurrentRaw: monitor.CurBrightness,
        HardwareMin: monitor.MinBrightness,
        HardwareMax: monitor.MaxBrightness);

    /// <summary>Drop tracked "last sent" values for handles no longer present.</summary>
    private void PruneLastSentValues(IReadOnlyList<MonitorInfo> monitors)
    {
        var currentHandles = new HashSet<IntPtr>();
        foreach (var monitor in monitors)
        {
            currentHandles.Add(monitor.Handle);
        }
        foreach (var handle in new List<IntPtr>(_lastSentValues.Keys))
        {
            if (!currentHandles.Contains(handle))
            {
                _lastSentValues.Remove(handle);
            }
        }
    }

    /// <summary>
    /// Writes the applied brightness to a single monitor (skipping redundant
    /// writes) and returns <paramref name="row"/> annotated with what happened.
    /// </summary>
    private MonitorRow DriveMonitor(MonitorInfo monitor, int appliedPercent, MonitorRow row)
    {
        if (PercentToMonitorValue(monitor, appliedPercent) is not int rawValue)
        {
            return row;
        }
        bool lastMatches = _lastSentValues.TryGetValue(monitor.Handle, out int lastSent) && lastSent == rawValue;
        if (lastMatches && monitor.CurBrightness == rawValue)
        {
            return row with { Action = $"Holding at {appliedPercent}%" };
        }
        if (_monitorManager.SetBrightness(monitor.Handle, rawValue))
        {
            _lastSentValues[monitor.Handle] = rawValue;
            return row with { Action = $"Set to {appliedPercent}% (raw {rawValue})" };
        }
        _lastSentValues.Remove(monitor.Handle);
        return row with { Action = "Failed to set brightness", ActionFailed = true };
    }
}
