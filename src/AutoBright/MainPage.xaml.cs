using System.Collections.ObjectModel;
using System.Globalization;
using AutoBright.Controller;
using AutoBright.Settings;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace AutoBright;

public partial class MainPage : ContentPage
{
    private readonly BrightnessController _controller;
    private readonly AppSettings _settings;

    private Microsoft.UI.Xaml.Window? _nativeWindow;
    private AppWindow? _appWindow;
    private IntPtr _hwnd;

    private IDispatcherTimer? _uiTimer;
    private IDispatcherTimer? _geometrySaveTimer;
    private IDispatcherTimer? _settingsSaveTimer;
    private TrayIcon? _tray;
    private WindowGeometry? _lastGoodGeometry;
    private bool _shuttingDown;

    // Suppress auto-save while we are programmatically populating fields or
    // restoring the window, so init does not trigger a commit/save.
    private bool _suppressCommit = true;

    private readonly ObservableCollection<string> _monitorLines = new();

    private static readonly Color ErrorColor = Color.FromArgb("#E74C3C");

    // Smallest restorable window; anything smaller is treated as a bad save.
    private const int MinRestoreSize = 200;
    // Windows parks minimized windows near (-32000, -32000); reject that sentinel.
    private const int OffScreenSentinel = -30000;

    public MainPage(CommandLineOptions options)
    {
        _settings = AppSettings.Load();
        _controller = new BrightnessController(options, _settings);

        InitializeComponent();

        MonitorList.ItemsSource = _monitorLines;

        // Seed the last-good geometry from settings only if it looks valid, so a
        // previously-saved bad rectangle is never written back out.
        WindowGeometry? savedGeom = _settings.WindowGeometry;
        if (savedGeom != null
            && savedGeom.Width >= MinRestoreSize && savedGeom.Height >= MinRestoreSize
            && savedGeom.X > OffScreenSentinel && savedGeom.Y > OffScreenSentinel)
        {
            _lastGoodGeometry = savedGeom;
        }

        PopulateFields();

        Loaded += OnPageLoaded;
    }

    // ---- Lifecycle --------------------------------------------------------

    private void OnPageLoaded(object? sender, EventArgs e)
    {
        Loaded -= OnPageLoaded;

        WireNativeWindow();

        _geometrySaveTimer = Dispatcher.CreateTimer();
        _geometrySaveTimer.Interval = TimeSpan.FromMilliseconds(400);
        _geometrySaveTimer.IsRepeating = false;
        _geometrySaveTimer.Tick += (_, _) => SaveSettings();

        // The slider raises ValueChanged continuously while dragged; apply live but
        // debounce the disk write so we persist once the drag settles.
        _settingsSaveTimer = Dispatcher.CreateTimer();
        _settingsSaveTimer.Interval = TimeSpan.FromMilliseconds(400);
        _settingsSaveTimer.IsRepeating = false;
        _settingsSaveTimer.Tick += (_, _) => SaveSettings();

        _controller.Start();

        // Field/window changes are now allowed to auto-save.
        _suppressCommit = false;

        _uiTimer = Dispatcher.CreateTimer();
        _uiTimer.Interval = TimeSpan.FromMilliseconds(500);
        _uiTimer.IsRepeating = true;
        _uiTimer.Tick += (_, _) => RefreshUi();
        _uiTimer.Start();

        // Keep the Windows "run at startup" entry in sync with the saved setting
        // and the current executable path (handles a moved/reinstalled exe).
        StartupManager.Apply(_controller.Config.RunAtStartup, _controller.Config.StartMinimized);

        if (_controller.Config.StartMinimized)
        {
            HideToTray();
        }
    }

    /// <summary>
    /// Resolve the underlying WinUI window / AppWindow from the MAUI window handler
    /// so the same Win32-backed window management the original used (geometry
    /// persistence, hide-to-tray, custom icon) can run unchanged.
    /// </summary>
    private void WireNativeWindow()
    {
        _nativeWindow = this.Window?.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
        if (_nativeWindow == null)
        {
            return;
        }

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_nativeWindow);
        Microsoft.UI.WindowId id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(id);

        TrySetWindowIcon();
        TrySetMicaBackdrop();
        RestoreGeometry();

        if (_appWindow != null)
        {
            _appWindow.Closing += OnAppWindowClosing;
            _appWindow.Changed += OnAppWindowChanged;
        }
    }

    private void TrySetWindowIcon()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "icon.ico");
            if (File.Exists(path))
            {
                _appWindow?.SetIcon(path);
            }
        }
        catch
        {
            // Non-fatal: fall back to the default window icon.
        }
    }

    private void TrySetMicaBackdrop()
    {
        try
        {
            if (_nativeWindow != null && Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported())
            {
                _nativeWindow.SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
                // Let the backdrop show through instead of the solid page fill.
                this.BackgroundColor = Colors.Transparent;
            }
        }
        catch
        {
            // Unsupported / failed: keep the solid background from XAML.
        }
    }

    private void RestoreGeometry()
    {
        if (_appWindow == null)
        {
            return;
        }

        WindowGeometry? geom = _settings.WindowGeometry;
        if (geom != null
            && geom.Width >= MinRestoreSize && geom.Height >= MinRestoreSize
            && geom.X > OffScreenSentinel && geom.Y > OffScreenSentinel)
        {
            // Clamp the saved rectangle onto a currently-connected display so a
            // monitor change can't leave the window stranded off-screen.
            var saved = new RectInt32(geom.X, geom.Y, geom.Width, geom.Height);
            DisplayArea area = DisplayArea.GetFromRect(saved, DisplayAreaFallback.Nearest);
            RectInt32 work = area.WorkArea;

            int width = Math.Min(geom.Width, work.Width);
            int height = Math.Min(geom.Height, work.Height);
            int x = Math.Max(work.X, Math.Min(geom.X, work.X + work.Width - width));
            int y = Math.Max(work.Y, Math.Min(geom.Y, work.Y + work.Height - height));
            _appWindow.MoveAndResize(new RectInt32(x, y, width, height));
        }
        else
        {
            // First run: a compact default that fits the content without scrolling.
            _appWindow.Resize(new SizeInt32(460, 720));
        }
    }

    // ---- Field population -------------------------------------------------

    private void PopulateFields()
    {
        ConfigData cfg = _controller.Config;
        MinLuxBox.Text = cfg.MinLux.ToString(CultureInfo.InvariantCulture);
        MaxLuxBox.Text = cfg.MaxLux.ToString(CultureInfo.InvariantCulture);
        MinBrightnessBox.Text = cfg.MinBrightness.ToString(CultureInfo.InvariantCulture);
        MaxBrightnessBox.Text = cfg.MaxBrightness.ToString(CultureInfo.InvariantCulture);
        IntervalBox.Text = cfg.Interval.ToString(CultureInfo.InvariantCulture);
        MaxRateBox.Text = cfg.MaxBrightnessRate.ToString(CultureInfo.InvariantCulture);
        HysteresisBox.Text = cfg.LuxHysteresis.ToString(CultureInfo.InvariantCulture);
        TrayCheck.IsChecked = cfg.MinimizeToTray;
        RunAtStartupCheck.IsChecked = cfg.RunAtStartup;
        StartMinimizedCheck.IsChecked = cfg.StartMinimized;

        AbsoluteCheck.IsChecked = cfg.ManualAbsolute;
        ManualCheck.IsChecked = cfg.ManualControl;
        switch (cfg.ManualMode)
        {
            case ManualMode.Max: MaxRadio.IsChecked = true; break;
            case ManualMode.Min: MinRadio.IsChecked = true; break;
            default: ManualRadio.IsChecked = true; break;
        }
        UpdateManualSliderRange();
        ManualSlider.Value = Math.Max(ManualSlider.Minimum, Math.Min(ManualSlider.Maximum, cfg.ManualBrightness));
        ManualValueLabel.Text = $"{(int)Math.Round(ManualSlider.Value)}%";
        UpdateManualVisibility();
    }

    // ---- Settings auto-commit --------------------------------------------

    private void OnSettingCommitted(object? sender, EventArgs e) => CommitSettings();

    private void OnSettingCompleted(object? sender, EventArgs e) => CommitSettings();

    /// <summary>
    /// Validate every field as a set and, when valid, apply and persist the
    /// settings. Invalid input is reported inline (no modal dialog) and leaves
    /// the previously applied configuration untouched.
    /// </summary>
    private void CommitSettings()
    {
        if (_suppressCommit)
        {
            return;
        }
        if (!TryBuildConfig(out ConfigData updated))
        {
            return;
        }

        // Nothing actually changed (e.g. focus left an untouched field).
        if (ConfigEquals(_controller.Config, updated))
        {
            return;
        }

        _controller.ApplyConfig(updated);
        StartupManager.Apply(updated.RunAtStartup, updated.StartMinimized);
        SaveSettings();
        SetSettingsStatus("Settings saved.", error: false);

        // A min/max brightness edit changes the configured slider range.
        UpdateManualSliderRange();
    }

    /// <summary>
    /// Validate every field as a set and, when valid, produce the resulting
    /// configuration. On invalid input the status line is updated and false is
    /// returned, leaving the caller to keep the previously applied config.
    /// </summary>
    private bool TryBuildConfig(out ConfigData updated)
    {
        ConfigData previous = _controller.Config;
        updated = previous;

        if (!TryParseField(MinLuxBox.Text, out double minLux)
            || !TryParseField(MaxLuxBox.Text, out double maxLux)
            || !TryParseInt(MinBrightnessBox.Text, out int minBrightness)
            || !TryParseInt(MaxBrightnessBox.Text, out int maxBrightness)
            || !TryParseField(IntervalBox.Text, out double interval)
            || !TryParseField(MaxRateBox.Text, out double maxRate)
            || !TryParseField(HysteresisBox.Text, out double hysteresis))
        {
            SetSettingsStatus("Enter valid numeric values for all fields.", error: true);
            return false;
        }

        string? error =
            minLux >= maxLux ? "Minimum lux must be lower than maximum lux."
            : !(minBrightness is >= 0 and <= 100 && maxBrightness is >= 0 and <= 100) ? "Brightness values must be between 0 and 100."
            : minBrightness >= maxBrightness ? "Minimum brightness must be lower than maximum brightness."
            : interval <= 0 ? "Update rate must be greater than zero."
            : maxRate < 0 ? "Max change rate cannot be negative (use 0 to disable smoothing)."
            : hysteresis < 0 ? "Lux hysteresis cannot be negative (use 0 to disable it)."
            : null;

        if (error != null)
        {
            SetSettingsStatus(error, error: true);
            return false;
        }

        updated = new ConfigData
        {
            MinLux = minLux,
            MaxLux = maxLux,
            MinBrightness = minBrightness,
            MaxBrightness = maxBrightness,
            Interval = interval,
            MaxBrightnessRate = maxRate,
            LuxHysteresis = hysteresis,
            ShowLux = previous.ShowLux,
            MinimizeToTray = TrayCheck.IsChecked,
            StartMinimized = StartMinimizedCheck.IsChecked,
            RunAtStartup = RunAtStartupCheck.IsChecked,
            RemoteHub = previous.RemoteHub,
            ManualControl = ManualCheck.IsChecked,
            ManualMode = GetSelectedManualMode(),
            ManualBrightness = (int)Math.Round(ManualSlider.Value),
            ManualAbsolute = AbsoluteCheck.IsChecked,
        };
        return true;
    }

    private ManualMode GetSelectedManualMode()
    {
        if (MaxRadio.IsChecked)
        {
            return ManualMode.Max;
        }
        if (MinRadio.IsChecked)
        {
            return ManualMode.Min;
        }
        return ManualMode.Manual;
    }

    private static bool ConfigEquals(ConfigData a, ConfigData b) =>
        a.MinLux == b.MinLux
        && a.MaxLux == b.MaxLux
        && a.MinBrightness == b.MinBrightness
        && a.MaxBrightness == b.MaxBrightness
        && a.Interval == b.Interval
        && a.MaxBrightnessRate == b.MaxBrightnessRate
        && a.LuxHysteresis == b.LuxHysteresis
        && a.MinimizeToTray == b.MinimizeToTray
        && a.StartMinimized == b.StartMinimized
        && a.RunAtStartup == b.RunAtStartup
        && a.ManualControl == b.ManualControl
        && a.ManualMode == b.ManualMode
        && a.ManualBrightness == b.ManualBrightness
        && a.ManualAbsolute == b.ManualAbsolute;

    private void SetSettingsStatus(string message, bool error)
    {
        SettingsStatus.Text = message;
        SettingsStatus.IsVisible = !string.IsNullOrEmpty(message);
        SettingsStatus.TextColor = error ? ErrorColor : SecondaryTextColor();
    }

    private static Color SecondaryTextColor()
    {
        bool dark = Application.Current?.RequestedTheme == AppTheme.Dark;
        object? key = Application.Current?.Resources is { } res
            && res.TryGetValue(dark ? "SecondaryTextDark" : "SecondaryTextLight", out object? v)
            ? v : null;
        return key as Color ?? (dark ? Colors.LightGray : Colors.Gray);
    }

    // ---- Manual override --------------------------------------------------

    private void OnManualCheckChanged(object? sender, CheckedChangedEventArgs e)
    {
        UpdateManualVisibility();
        if (_suppressCommit)
        {
            return;
        }

        // When manual control is switched on, seed the slider with the brightness
        // we are currently driving so there is no visible jump.
        if (ManualCheck.IsChecked)
        {
            double seed = _controller.GetSnapshot().TargetBrightness ?? ManualSlider.Value;
            seed = Math.Max(ManualSlider.Minimum, Math.Min(ManualSlider.Maximum, seed));
            _suppressCommit = true;
            ManualSlider.Value = seed;
            ManualValueLabel.Text = $"{(int)Math.Round(seed)}%";
            _suppressCommit = false;
        }
        CommitSettings();
    }

    private void OnManualModeChanged(object? sender, CheckedChangedEventArgs e)
    {
        // CheckedChanged fires for both the newly-selected and newly-cleared radio;
        // act only on the one that became selected.
        if (!e.Value)
        {
            return;
        }
        UpdateManualVisibility();
        CommitSettings();
    }

    private void OnAbsoluteChanged(object? sender, CheckedChangedEventArgs e)
    {
        UpdateManualSliderRange();
        CommitSettings();
    }

    /// <summary>
    /// Bound the slider to either the absolute 0-100% range or the configured
    /// min/max brightness, clamping the current value into the new range.
    /// </summary>
    private void UpdateManualSliderRange()
    {
        bool absolute = AbsoluteCheck.IsChecked;
        double min = absolute ? 0 : _controller.Config.MinBrightness;
        double max = absolute ? 100 : _controller.Config.MaxBrightness;
        if (max <= min)
        {
            max = min + 1;
        }

        bool previous = _suppressCommit;
        _suppressCommit = true;
        // Maximum must always be >= Minimum during assignment; order the writes so
        // the range never passes through an invalid (max < min) intermediate state.
        if (min <= ManualSlider.Maximum)
        {
            ManualSlider.Minimum = min;
            ManualSlider.Maximum = max;
        }
        else
        {
            ManualSlider.Maximum = max;
            ManualSlider.Minimum = min;
        }
        if (ManualSlider.Value < min)
        {
            ManualSlider.Value = min;
        }
        else if (ManualSlider.Value > max)
        {
            ManualSlider.Value = max;
        }
        ManualValueLabel.Text = $"{(int)Math.Round(ManualSlider.Value)}%";
        _suppressCommit = previous;
    }

    private void OnManualSliderChanged(object? sender, ValueChangedEventArgs e)
    {
        ManualValueLabel.Text = $"{(int)Math.Round(e.NewValue)}%";
        if (_suppressCommit)
        {
            return;
        }

        // Apply live for responsive feedback; debounce the disk write.
        if (TryBuildConfig(out ConfigData updated) && !ConfigEquals(_controller.Config, updated))
        {
            _controller.ApplyConfig(updated);
        }
        _settingsSaveTimer?.Stop();
        _settingsSaveTimer?.Start();
    }

    private void UpdateManualVisibility()
    {
        bool manual = ManualCheck.IsChecked;
        ManualPanel.IsVisible = manual;
        ManualSlider.IsEnabled = manual && ManualRadio.IsChecked;
    }

    // ---- Button handlers --------------------------------------------------

    private void OnRefreshClick(object? sender, EventArgs e) => _controller.RequestRefreshMonitors();

    private void OnReconnectClick(object? sender, EventArgs e) => _controller.RequestReconnectSensor();

    private void OnHideClick(object? sender, EventArgs e) => HideToTray();

    // ---- Tray / window lifecycle -----------------------------------------

    private void HideToTray()
    {
        if (_tray == null && _hwnd != IntPtr.Zero)
        {
            _tray = new TrayIcon(_hwnd, "Brightness Dimmer", OnTrayAction);
            _tray.Show();
        }
        _appWindow?.Hide();
    }

    private void RestoreFromTray()
    {
        if (_tray != null)
        {
            _tray.Cleanup();
            _tray = null;
        }
        _appWindow?.Show();
        _nativeWindow?.Activate();
    }

    private void OnTrayAction(string action)
    {
        // The tray callback arrives on a non-UI thread (the window message pump
        // for the subclassed HWND); marshal back to the UI thread.
        Dispatcher.Dispatch(() =>
        {
            if (action == "restore")
            {
                RestoreFromTray();
            }
            else if (action == "quit")
            {
                Shutdown(quit: true);
            }
        });
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_shuttingDown)
        {
            // Let the close proceed so the process can exit.
            return;
        }
        if (_controller.Config.MinimizeToTray)
        {
            args.Cancel = true;
            HideToTray();
            return;
        }
        // Full shutdown: run orderly cleanup, then allow the window to close.
        Shutdown(quit: false);
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_suppressCommit || _shuttingDown)
        {
            return;
        }
        if (args.DidPositionChange || args.DidSizeChange)
        {
            // Debounce so a drag/resize persists once, after it settles.
            _geometrySaveTimer?.Stop();
            _geometrySaveTimer?.Start();
        }
    }

    private void Shutdown(bool quit)
    {
        if (_shuttingDown)
        {
            return;
        }
        _shuttingDown = true;
        _uiTimer?.Stop();
        _geometrySaveTimer?.Stop();
        _settingsSaveTimer?.Stop();
        _controller.Stop();
        _controller.DestroyMonitors();
        _tray?.Cleanup();
        if (quit)
        {
            Application.Current?.Quit();
        }
    }

    // ---- Settings persistence --------------------------------------------

    private void SaveSettings()
    {
        ConfigData cfg = _controller.Config;

        // Only capture geometry when the window is in a normal, on-screen state.
        // Saving while minimized/hidden would persist the (-32000,-32000) sentinel
        // and a tiny size, which would make the window un-restorable next launch.
        if (_appWindow != null)
        {
            PointInt32 pos = _appWindow.Position;
            SizeInt32 size = _appWindow.Size;
            bool minimized = (_appWindow.Presenter as OverlappedPresenter)?.State == OverlappedPresenterState.Minimized;
            if (!minimized
                && pos.X > OffScreenSentinel && pos.Y > OffScreenSentinel
                && size.Width >= MinRestoreSize && size.Height >= MinRestoreSize)
            {
                _lastGoodGeometry = new WindowGeometry { X = pos.X, Y = pos.Y, Width = size.Width, Height = size.Height };
            }
        }

        var toSave = new AppSettings
        {
            MinLux = cfg.MinLux,
            MaxLux = cfg.MaxLux,
            MinBrightness = cfg.MinBrightness,
            MaxBrightness = cfg.MaxBrightness,
            Interval = cfg.Interval,
            MaxBrightnessRate = cfg.MaxBrightnessRate,
            LuxHysteresis = cfg.LuxHysteresis,
            ShowLux = cfg.ShowLux,
            MinimizeToTray = cfg.MinimizeToTray,
            StartMinimized = cfg.StartMinimized,
            RunAtStartup = cfg.RunAtStartup,
            RemoteHub = cfg.RemoteHub,
            ManualControl = cfg.ManualControl,
            ManualMode = cfg.ManualMode,
            ManualBrightness = cfg.ManualBrightness,
            ManualAbsolute = cfg.ManualAbsolute,
            WindowGeometry = _lastGoodGeometry,
        };
        try
        {
            toSave.Save();
        }
        catch
        {
            // Saving is best-effort; the next UI refresh will surface worker state.
        }
    }

    // ---- UI refresh -------------------------------------------------------

    private void RefreshUi()
    {
        AppState state = _controller.GetSnapshot();

        if (_controller.Config.ShowLux)
        {
            LuxValue.Text = state.CurrentLux is double lux
                ? lux.ToString("F1", CultureInfo.InvariantCulture)
                : "n/a";
        }
        else
        {
            LuxValue.Text = "hidden";
        }

        TargetValue.Text = state.TargetBrightness is int target ? $"{target}%" : "n/a";

        bool hasError = !string.IsNullOrEmpty(state.LastError);
        WorkerStatus.Text = hasError
            ? $"{state.MonitorStatus}  •  {state.LastError}"
            : state.MonitorStatus;
        WorkerStatus.TextColor = hasError ? ErrorColor : SecondaryTextColor();

        UpdateMonitorList(state.MonitorList);
    }

    private void UpdateMonitorList(List<string> lines)
    {
        // Update in place so the CollectionView keeps its scroll position and does
        // not flicker every refresh.
        if (_monitorLines.Count == lines.Count)
        {
            bool identical = true;
            for (int i = 0; i < lines.Count; i++)
            {
                if (_monitorLines[i] != lines[i])
                {
                    identical = false;
                    break;
                }
            }
            if (identical)
            {
                return;
            }
        }

        for (int i = 0; i < lines.Count; i++)
        {
            if (i < _monitorLines.Count)
            {
                if (_monitorLines[i] != lines[i])
                {
                    _monitorLines[i] = lines[i];
                }
            }
            else
            {
                _monitorLines.Add(lines[i]);
            }
        }
        while (_monitorLines.Count > lines.Count)
        {
            _monitorLines.RemoveAt(_monitorLines.Count - 1);
        }
    }

    // ---- Helpers ----------------------------------------------------------

    private static bool TryParseField(string? text, out double value)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static bool TryParseInt(string? text, out int value)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
}
