using System;
using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinRT.Interop;
using AutoBright.Controller;
using AutoBright.Settings;

namespace AutoBright;

public sealed partial class MainWindow : Window
{
    private readonly BrightnessController _controller;
    private readonly AppSettings _settings;
    private readonly IntPtr _hwnd;
    private DispatcherQueueTimer? _uiTimer;
    private DispatcherQueueTimer? _geometrySaveTimer;
    private DispatcherQueueTimer? _settingsSaveTimer;
    private TrayIcon? _tray;
    private WindowGeometry? _lastGoodGeometry;
    private bool _shuttingDown;
    // Suppress auto-save while we are programmatically populating fields or
    // restoring the window, so init does not trigger a commit/save.
    private bool _suppressCommit = true;
    private Microsoft.UI.Xaml.Media.Brush? _defaultStatusBrush;
    private Microsoft.UI.Xaml.Media.Brush? _defaultWorkerBrush;
    private readonly Microsoft.UI.Xaml.Media.SolidColorBrush _errorStatusBrush =
        new(Microsoft.UI.Colors.OrangeRed);

    public MainWindow(CommandLineOptions options)
    {
        _settings = AppSettings.Load();
        _controller = new BrightnessController(options, _settings);

        this.InitializeComponent();

        // The root grid paints a solid themed background by default so the window
        // is always visible. A Mica backdrop is layered on top only when the
        // platform actually supports it; if it cannot render we keep the solid
        // fill rather than ending up with an invisible, transparent window.
        TrySetMicaBackdrop();

        _hwnd = WindowNative.GetWindowHandle(this);
        TrySetWindowIcon();
        _defaultStatusBrush = SettingsStatus.Foreground;
        _defaultWorkerBrush = WorkerStatus.Foreground;

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
        RestoreGeometry();

        this.AppWindow.Closing += OnAppWindowClosing;
        this.AppWindow.Changed += OnAppWindowChanged;

        _geometrySaveTimer = this.DispatcherQueue.CreateTimer();
        _geometrySaveTimer.Interval = TimeSpan.FromMilliseconds(400);
        _geometrySaveTimer.IsRepeating = false;
        _geometrySaveTimer.Tick += (_, _) => SaveSettings();

        // The slider raises ValueChanged continuously while dragged; apply live but
        // debounce the disk write so we persist once the drag settles.
        _settingsSaveTimer = this.DispatcherQueue.CreateTimer();
        _settingsSaveTimer.Interval = TimeSpan.FromMilliseconds(400);
        _settingsSaveTimer.IsRepeating = false;
        _settingsSaveTimer.Tick += (_, _) => SaveSettings();

        _controller.Start();

        // Field/window changes are now allowed to auto-save.
        _suppressCommit = false;

        _uiTimer = this.DispatcherQueue.CreateTimer();
        _uiTimer.Interval = TimeSpan.FromMilliseconds(500);
        _uiTimer.Tick += (_, _) => RefreshUi();
        _uiTimer.Start();

        if (_controller.Config.StartMinimized)
        {
            HideToTray();
        }
    }

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

    private void TrySetWindowIcon()
    {
        try
        {
            string path = System.IO.Path.Combine(AppContext.BaseDirectory, "icon.ico");
            if (System.IO.File.Exists(path))
            {
                this.AppWindow.SetIcon(path);
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
            if (Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported())
            {
                this.SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
                // Let the backdrop show through instead of the solid fill.
                RootGrid.Background = null;
            }
        }
        catch
        {
            // Unsupported / failed: keep the solid background from XAML.
        }
    }

    // Smallest restorable window; anything smaller is treated as a bad save.
    private const int MinRestoreSize = 200;
    // Windows parks minimized windows near (-32000, -32000); reject that sentinel.
    private const int OffScreenSentinel = -30000;

    private void RestoreGeometry()
    {
        WindowGeometry? geom = _settings.WindowGeometry;
        if (geom != null
            && geom.Width >= MinRestoreSize && geom.Height >= MinRestoreSize
            && geom.X > OffScreenSentinel && geom.Y > OffScreenSentinel)
        {
            // Clamp the saved rectangle onto a currently-connected display so a
            // monitor change can't leave the window stranded off-screen.
            var saved = new RectInt32(geom.X, geom.Y, geom.Width, geom.Height);
            Microsoft.UI.Windowing.DisplayArea area =
                Microsoft.UI.Windowing.DisplayArea.GetFromRect(saved, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
            RectInt32 work = area.WorkArea;

            int width = Math.Min(geom.Width, work.Width);
            int height = Math.Min(geom.Height, work.Height);
            int x = Math.Max(work.X, Math.Min(geom.X, work.X + work.Width - width));
            int y = Math.Max(work.Y, Math.Min(geom.Y, work.Y + work.Height - height));
            this.AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
        }
        else
        {
            // First run: a compact default that fits the content without scrolling.
            this.AppWindow.Resize(new SizeInt32(460, 720));
        }
    }

    // ---- Settings auto-commit --------------------------------------------

    private void OnSettingCommitted(object sender, RoutedEventArgs e) => CommitSettings();

    private void OnSettingKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            CommitSettings();
        }
    }

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
            MinimizeToTray = TrayCheck.IsChecked == true,
            StartMinimized = previous.StartMinimized,
            RemoteHub = previous.RemoteHub,
            ManualControl = ManualCheck.IsChecked == true,
            ManualMode = GetSelectedManualMode(),
            ManualBrightness = (int)Math.Round(ManualSlider.Value),
            ManualAbsolute = AbsoluteCheck.IsChecked == true,
        };
        return true;
    }

    private ManualMode GetSelectedManualMode()
    {
        if (MaxRadio.IsChecked == true)
        {
            return ManualMode.Max;
        }
        if (MinRadio.IsChecked == true)
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
        && a.ManualControl == b.ManualControl
        && a.ManualMode == b.ManualMode
        && a.ManualBrightness == b.ManualBrightness
        && a.ManualAbsolute == b.ManualAbsolute;

    private void SetSettingsStatus(string message, bool error)
    {
        SettingsStatus.Text = message;
        SettingsStatus.Foreground = error
            ? _errorStatusBrush
            : (_defaultStatusBrush ?? SettingsStatus.Foreground);
    }

    // ---- Manual override --------------------------------------------------

    private void OnManualCheckChanged(object sender, RoutedEventArgs e)
    {
        UpdateManualVisibility();
        if (_suppressCommit)
        {
            return;
        }

        // When manual control is switched on, seed the slider with the brightness
        // we are currently driving so there is no visible jump.
        if (ManualCheck.IsChecked == true)
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

    private void OnManualModeChanged(object sender, RoutedEventArgs e)
    {
        UpdateManualVisibility();
        CommitSettings();
    }

    private void OnAbsoluteChanged(object sender, RoutedEventArgs e)
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
        bool absolute = AbsoluteCheck.IsChecked == true;
        double min = absolute ? 0 : _controller.Config.MinBrightness;
        double max = absolute ? 100 : _controller.Config.MaxBrightness;
        if (max <= min)
        {
            max = min + 1;
        }

        bool previous = _suppressCommit;
        _suppressCommit = true;
        ManualSlider.Minimum = min;
        ManualSlider.Maximum = max;
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

    private void OnManualSliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (ManualValueLabel != null)
        {
            ManualValueLabel.Text = $"{(int)Math.Round(e.NewValue)}%";
        }
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
        bool manual = ManualCheck.IsChecked == true;
        ManualPanel.Visibility = manual ? Visibility.Visible : Visibility.Collapsed;
        ManualSlider.IsEnabled = manual && ManualRadio.IsChecked == true;
    }

    // ---- Button handlers --------------------------------------------------

    private void OnRefreshClick(object sender, RoutedEventArgs e) => _controller.RequestRefreshMonitors();

    private void OnReconnectClick(object sender, RoutedEventArgs e) => _controller.RequestReconnectSensor();

    private void OnHideClick(object sender, RoutedEventArgs e) => HideToTray();

    // ---- Tray / window lifecycle -----------------------------------------

    private void HideToTray()
    {
        if (_tray == null)
        {
            _tray = new TrayIcon(_hwnd, "Brightness Dimmer", OnTrayAction);
            _tray.Show();
        }
        this.AppWindow.Hide();
    }

    private void RestoreFromTray()
    {
        if (_tray != null)
        {
            _tray.Cleanup();
            _tray = null;
        }
        this.AppWindow.Show();
        this.Activate();
    }

    private void OnTrayAction(string action)
    {
        if (action == "restore")
        {
            RestoreFromTray();
        }
        else if (action == "quit")
        {
            Shutdown();
        }
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        // Always cancel the native close so we can run an orderly shutdown.
        args.Cancel = true;
        if (_controller.Config.MinimizeToTray && !_shuttingDown)
        {
            HideToTray();
            return;
        }
        Shutdown();
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

    private void Shutdown()
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
        Application.Current.Exit();
    }

    // ---- Settings ---------------------------------------------------------

    private void SaveSettings()
    {
        ConfigData cfg = _controller.Config;

        // Only capture geometry when the window is in a normal, on-screen state.
        // Saving while minimized/hidden would persist the (-32000,-32000) sentinel
        // and a tiny size, which would make the window un-restorable next launch.
        PointInt32 pos = this.AppWindow.Position;
        SizeInt32 size = this.AppWindow.Size;
        bool minimized = (this.AppWindow.Presenter as OverlappedPresenter)?.State == OverlappedPresenterState.Minimized;
        if (!minimized
            && pos.X > OffScreenSentinel && pos.Y > OffScreenSentinel
            && size.Width >= MinRestoreSize && size.Height >= MinRestoreSize)
        {
            _lastGoodGeometry = new WindowGeometry { X = pos.X, Y = pos.Y, Width = size.Width, Height = size.Height };
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
        WorkerStatus.Foreground = hasError
            ? _errorStatusBrush
            : (_defaultWorkerBrush ?? WorkerStatus.Foreground);

        UpdateMonitorList(state.MonitorList);
    }

    // Cache of the lines currently shown so we can skip no-op refreshes and,
    // when content does change, update only the rows that differ. This stops the
    // ListView from flickering / scrolling back to the top every 500 ms.
    private readonly System.Collections.Generic.List<string> _displayedLines = new();

    private void UpdateMonitorList(System.Collections.Generic.List<string> lines)
    {
        if (_displayedLines.Count == lines.Count)
        {
            bool identical = true;
            for (int i = 0; i < lines.Count; i++)
            {
                if (_displayedLines[i] != lines[i])
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

        var items = MonitorList.Items;
        for (int i = 0; i < lines.Count; i++)
        {
            if (i < items.Count)
            {
                if (items[i] is not string existing || existing != lines[i])
                {
                    items[i] = lines[i];
                }
            }
            else
            {
                items.Add(lines[i]);
            }
        }
        while (items.Count > lines.Count)
        {
            items.RemoveAt(items.Count - 1);
        }

        _displayedLines.Clear();
        _displayedLines.AddRange(lines);
    }

    // ---- Helpers ----------------------------------------------------------

    private static bool TryParseField(string text, out double value)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static bool TryParseInt(string text, out int value)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
}
