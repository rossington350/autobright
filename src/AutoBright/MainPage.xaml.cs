using System.Collections.ObjectModel;
using System.Globalization;
using AutoBright.Controller;
using AutoBright.Controls;
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

    private readonly ObservableCollection<MonitorItem> _monitorItems = new();
    private readonly MappingChart _chart = new();
    private bool? _wideLayout;

    private static readonly Color ErrorColor = Color.FromArgb("#E74C3C");

    // Window width (DIPs) at which the settings move into a second column.
    private const double WideLayoutThreshold = 860;

    // Smallest restorable window; anything smaller is treated as a bad save.
    private const int MinRestoreSize = 200;
    // Windows parks minimized windows near (-32000, -32000); reject that sentinel.
    private const int OffScreenSentinel = -30000;

    public MainPage(CommandLineOptions options)
    {
        _settings = AppSettings.Load();
        _controller = new BrightnessController(options, _settings);

        InitializeComponent();

        BindableLayout.SetItemsSource(MonitorList, _monitorItems);
        Chart.Drawable = _chart;
        SizeChanged += (_, _) => ApplyResponsiveLayout();
        ApplyResponsiveLayout();

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
        CompactSwitch(ManualSwitch);

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

        // A second launch of the app asks this instance to show itself.
        SingleInstance.ListenForShowRequests(() => Dispatcher.Dispatch(() =>
        {
            if (!_shuttingDown)
            {
                RestoreFromTray();
            }
        }));

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

    /// <summary>
    /// The WinUI ToggleSwitch reserves room for "On"/"Off" text beside the
    /// knob, which pushes it away from the card's right edge. Drop that text
    /// and its minimum width so the switch sits flush.
    /// </summary>
    private static void CompactSwitch(Switch control)
    {
        if (control.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.ToggleSwitch toggle)
        {
            toggle.OnContent = null;
            toggle.OffContent = null;
            toggle.MinWidth = 0;
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
            _appWindow.Resize(new SizeInt32(500, 820));
        }
    }

    // ---- Field population -------------------------------------------------

    private void PopulateFields()
    {
        ConfigData cfg = _controller.Config;
        MinLuxField.Text = cfg.MinLux.ToString(CultureInfo.InvariantCulture);
        MaxLuxField.Text = cfg.MaxLux.ToString(CultureInfo.InvariantCulture);
        MinBrightnessField.Text = cfg.MinBrightness.ToString(CultureInfo.InvariantCulture);
        MaxBrightnessField.Text = cfg.MaxBrightness.ToString(CultureInfo.InvariantCulture);
        IntervalField.Text = cfg.Interval.ToString(CultureInfo.InvariantCulture);
        MaxRateField.Text = cfg.MaxBrightnessRate.ToString(CultureInfo.InvariantCulture);
        HysteresisField.Text = cfg.LuxHysteresis.ToString(CultureInfo.InvariantCulture);
        TrayCheck.IsChecked = cfg.MinimizeToTray;
        RunAtStartupCheck.IsChecked = cfg.RunAtStartup;
        StartMinimizedCheck.IsChecked = cfg.StartMinimized;

        AbsoluteCheck.IsChecked = cfg.ManualAbsolute;
        ManualSwitch.IsToggled = cfg.ManualControl;
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
        // ConfigData is a record, so this is field-by-field value equality.
        if (_controller.Config == updated)
        {
            return;
        }

        _controller.ApplyConfig(updated);
        StartupManager.Apply(updated.RunAtStartup, updated.StartMinimized);
        if (SaveSettings())
        {
            SetSettingsStatus("Settings saved.", error: false);
        }

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

        NumberField[] fields =
        {
            MinLuxField, MaxLuxField, MinBrightnessField, MaxBrightnessField,
            IntervalField, MaxRateField, HysteresisField,
        };
        foreach (NumberField field in fields)
        {
            field.Error = null;
        }

        // Parse every field so all format errors show at once, not one at a time.
        bool parsed = ParseDouble(MinLuxField, out double minLux)
                      & ParseDouble(MaxLuxField, out double maxLux)
                      & ParseInt(MinBrightnessField, out int minBrightness)
                      & ParseInt(MaxBrightnessField, out int maxBrightness)
                      & ParseDouble(IntervalField, out double interval)
                      & ParseDouble(MaxRateField, out double maxRate)
                      & ParseDouble(HysteresisField, out double hysteresis);
        if (!parsed)
        {
            SetSettingsStatus("", error: false);
            return false;
        }

        string luxRange = $"Must be 0–{ConfigData.MaxLuxLimit:N0}";
        Check(MinLuxField, minLux >= 0 && minLux <= ConfigData.MaxLuxLimit, luxRange);
        Check(MaxLuxField, maxLux >= 0 && maxLux <= ConfigData.MaxLuxLimit, luxRange);
        Check(MaxLuxField, minLux < maxLux, "Must be above min lux");
        Check(MinBrightnessField, minBrightness is >= 0 and <= 100, "Must be 0–100");
        Check(MaxBrightnessField, maxBrightness is >= 0 and <= 100, "Must be 0–100");
        Check(MaxBrightnessField, minBrightness < maxBrightness, "Must be above min brightness");
        Check(IntervalField, interval > 0, "Must be above 0");
        Check(IntervalField, interval <= ConfigData.MaxIntervalSeconds, $"At most {ConfigData.MaxIntervalSeconds:N0} s");
        Check(MaxRateField, maxRate >= 0, "Can't be negative (0 turns smoothing off)");
        Check(HysteresisField, hysteresis >= 0, "Can't be negative (0 turns it off)");

        if (fields.Any(f => !string.IsNullOrEmpty(f.Error)))
        {
            // Advanced fields are hidden by default; don't let an error hide in there.
            if (!string.IsNullOrEmpty(IntervalField.Error) || !string.IsNullOrEmpty(MaxRateField.Error)
                || !string.IsNullOrEmpty(HysteresisField.Error))
            {
                SetAdvancedExpanded(true);
            }
            SetSettingsStatus("", error: false);
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
            ManualControl = ManualSwitch.IsToggled,
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

    private void OnManualToggled(object? sender, ToggledEventArgs e)
    {
        UpdateManualVisibility();
        if (_suppressCommit)
        {
            return;
        }

        // When manual control is switched on, seed the slider with the brightness
        // we are currently driving so there is no visible jump.
        if (ManualSwitch.IsToggled)
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
        if (TryBuildConfig(out ConfigData updated) && _controller.Config != updated)
        {
            _controller.ApplyConfig(updated);
        }
        _settingsSaveTimer?.Stop();
        _settingsSaveTimer?.Start();
    }

    private void UpdateManualVisibility()
    {
        bool manual = ManualSwitch.IsToggled;
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
        if (_appWindow?.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }
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

    /// <summary>
    /// Persist the current config and window geometry. Returns false (and shows
    /// the error in the settings status line) if the write failed.
    /// </summary>
    private bool SaveSettings()
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
            return true;
        }
        catch (Exception ex)
        {
            // Settings stay applied in memory; tell the user they weren't persisted.
            SetSettingsStatus($"Couldn't save settings: {ex.Message}", error: true);
            return false;
        }
    }

    // ---- UI refresh -------------------------------------------------------

    private void RefreshUi()
    {
        AppState state = _controller.GetSnapshot();
        ConfigData cfg = _controller.Config;
        bool hasLux = state.CurrentLux is double;

        // Ambient light
        if (!cfg.ShowLux)
        {
            LuxValue.Text = "Hidden";
            LuxUnit.IsVisible = false;
        }
        else
        {
            LuxValue.Text = state.CurrentLux is double lux
                ? lux.ToString(lux < 100 ? "F1" : "F0", CultureInfo.InvariantCulture)
                : "—";
            LuxUnit.IsVisible = hasLux;
        }

        // Brightness
        TargetValue.Text = state.TargetBrightness is int target ? $"{target}%" : "—";
        BrightnessBar.Progress = (state.TargetBrightness ?? 0) / 100.0;

        // Sensor badge: a reading this cycle means the sensor is up.
        SensorDot.Fill = new SolidColorBrush(ResourceColor(hasLux ? "OkColor" : "ErrorColor", hasLux ? Colors.Green : Colors.Red));
        SensorText.Text = hasLux ? "Sensor connected" : "No sensor reading";
        ReconnectButton.IsVisible = !hasLux;

        // Mode badge
        ModeText.Text = !cfg.ManualControl
            ? "Auto"
            : cfg.ManualMode switch
            {
                ManualMode.Min => "Manual · minimum",
                ManualMode.Max => "Manual · maximum",
                _ => "Manual",
            };
        ModeText.TextColor = cfg.ManualControl
            ? ResourceColor("WarnColor", Colors.Orange)
            : ResourceColor("Accent", Colors.SteelBlue);

        // Mapping chart
        _chart.Config = cfg;
        _chart.Lux = state.CurrentLux;
        _chart.TargetBrightness = state.TargetBrightness;
        _chart.Accent = ResourceColor("Accent", Colors.SteelBlue);
        _chart.Muted = SecondaryTextColor();
        _chart.Grid = ThemeColor("CardStrokeLight", "CardStrokeDark", Colors.LightGray);
        _chart.MarkerRing = ThemeColor("CardBgLight", "CardBgDark", Colors.White);
        Chart.Invalidate();

        // Monitors
        bool hasError = !string.IsNullOrEmpty(state.LastError);
        WorkerStatus.Text = hasError
            ? $"{state.MonitorStatus}  •  {state.LastError}"
            : state.MonitorStatus;
        WorkerStatus.TextColor = hasError ? ErrorColor : SecondaryTextColor();

        UpdateMonitorList(state.Monitors);
    }

    private void UpdateMonitorList(List<MonitorRow> rows)
    {
        Color actionColor = SecondaryTextColor();
        var items = rows.Select(r => MonitorItem.From(r, actionColor, ErrorColor)).ToList();

        // Update in place (records compare by value) so unchanged rows don't
        // get rebuilt and the list doesn't flicker on every refresh.
        for (int i = 0; i < items.Count; i++)
        {
            if (i >= _monitorItems.Count)
            {
                _monitorItems.Add(items[i]);
            }
            else if (_monitorItems[i] != items[i])
            {
                _monitorItems[i] = items[i];
            }
        }
        while (_monitorItems.Count > items.Count)
        {
            _monitorItems.RemoveAt(_monitorItems.Count - 1);
        }
    }

    // ---- Layout -----------------------------------------------------------

    /// <summary>
    /// Put the settings in a second column when the window is wide enough,
    /// otherwise stack them below the status column.
    /// </summary>
    private void ApplyResponsiveLayout()
    {
        if (Width <= 0)
        {
            return;
        }
        bool wide = Width >= WideLayoutThreshold;
        if (_wideLayout == wide)
        {
            return;
        }
        _wideLayout = wide;

        RootGrid.ColumnDefinitions.Clear();
        RootGrid.RowDefinitions.Clear();
        if (wide)
        {
            RootGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1.2, GridUnitType.Star)));
            RootGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            Grid.SetRow(RightColumn, 0);
            Grid.SetColumn(RightColumn, 1);
        }
        else
        {
            RootGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            RootGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(RightColumn, 1);
            Grid.SetColumn(RightColumn, 0);
        }
    }

    private void OnAdvancedToggle(object? sender, TappedEventArgs e) => SetAdvancedExpanded(!AdvancedPanel.IsVisible);

    private void SetAdvancedExpanded(bool expanded)
    {
        AdvancedPanel.IsVisible = expanded;
        // Segoe MDL2 Assets: ChevronDown / ChevronRight.
        AdvancedChevron.Text = expanded ? "" : "";
    }

    private static Color ResourceColor(string key, Color fallback)
        => Application.Current?.Resources is { } res && res.TryGetValue(key, out object? v) && v is Color c
            ? c
            : fallback;

    private static Color ThemeColor(string lightKey, string darkKey, Color fallback)
        => ResourceColor(Application.Current?.RequestedTheme == AppTheme.Dark ? darkKey : lightKey, fallback);

    // ---- Helpers ----------------------------------------------------------

    private static bool ParseDouble(NumberField field, out double value)
    {
        if (TryParseField(field.Text, out value))
        {
            return true;
        }
        field.Error = "Enter a number";
        return false;
    }

    private static bool ParseInt(NumberField field, out int value)
    {
        if (TryParseInt(field.Text, out value))
        {
            return true;
        }
        field.Error = "Enter a whole number";
        return false;
    }

    /// <summary>Set <paramref name="message"/> on the field unless it already has an error.</summary>
    private static void Check(NumberField field, bool ok, string message)
    {
        if (!ok && string.IsNullOrEmpty(field.Error))
        {
            field.Error = message;
        }
    }

    // NumberStyles.Float accepts "NaN" and "Infinity", and comparisons with
    // NaN are always false, so it would get past every range check. Reject
    // non-finite values here, at parse time.
    private static bool TryParseField(string? text, out double value)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
           && double.IsFinite(value);

    private static bool TryParseInt(string? text, out int value)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
}

/// <summary>Display model for one monitor row in the Monitors card.</summary>
public sealed record MonitorItem(
    string Name,
    string PercentText,
    double Progress,
    string Detail,
    string Action,
    bool HasAction,
    Color ActionColor,
    double Opacity)
{
    public static MonitorItem From(MonitorRow row, Color normal, Color error) => new(
        Name: row.Name,
        PercentText: $"{row.CurrentPercent}%",
        Progress: Math.Clamp(row.CurrentPercent / 100.0, 0, 1),
        Detail: row.IsStale
            ? $"Last known value · not responding · raw {row.CurrentRaw} (range {row.HardwareMin}–{row.HardwareMax})"
            : $"Raw {row.CurrentRaw} (range {row.HardwareMin}–{row.HardwareMax})",
        Action: row.Action ?? "",
        HasAction: !string.IsNullOrEmpty(row.Action),
        ActionColor: row.ActionFailed ? error : normal,
        Opacity: row.IsStale ? 0.55 : 1.0);
}
