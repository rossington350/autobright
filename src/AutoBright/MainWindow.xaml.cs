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
    private TrayIcon? _tray;
    private bool _shuttingDown;

    public MainWindow(CommandLineOptions options)
    {
        _settings = AppSettings.Load();
        _controller = new BrightnessController(options, _settings);

        this.InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);

        PopulateFields();
        RestoreGeometry();

        this.AppWindow.Closing += OnAppWindowClosing;

        _controller.Start();

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
    }

    private void RestoreGeometry()
    {
        WindowGeometry? geom = _settings.WindowGeometry;
        if (geom != null && geom.Width > 0 && geom.Height > 0)
        {
            this.AppWindow.MoveAndResize(new RectInt32(geom.X, geom.Y, geom.Width, geom.Height));
        }
    }

    // ---- Button handlers --------------------------------------------------

    private async void OnApplyClick(object sender, RoutedEventArgs e)
    {
        if (!TryParseField(MinLuxBox.Text, out double minLux)
            || !TryParseField(MaxLuxBox.Text, out double maxLux)
            || !TryParseInt(MinBrightnessBox.Text, out int minBrightness)
            || !TryParseInt(MaxBrightnessBox.Text, out int maxBrightness)
            || !TryParseField(IntervalBox.Text, out double interval)
            || !TryParseField(MaxRateBox.Text, out double maxRate)
            || !TryParseField(HysteresisBox.Text, out double hysteresis))
        {
            await ShowError("Please enter valid numeric values for min/max lux, brightness, update rate, change rate, and hysteresis.");
            return;
        }

        if (minLux >= maxLux)
        {
            await ShowError("Minimum lux must be lower than maximum lux.");
            return;
        }
        if (!(minBrightness is >= 0 and <= 100 && maxBrightness is >= 0 and <= 100))
        {
            await ShowError("Brightness values must be between 0 and 100.");
            return;
        }
        if (minBrightness >= maxBrightness)
        {
            await ShowError("Minimum brightness must be lower than maximum brightness.");
            return;
        }
        if (interval <= 0)
        {
            await ShowError("Update rate must be greater than zero.");
            return;
        }
        if (maxRate < 0)
        {
            await ShowError("Max change rate cannot be negative (use 0 to disable smoothing).");
            return;
        }
        if (hysteresis < 0)
        {
            await ShowError("Lux hysteresis cannot be negative (use 0 to disable it).");
            return;
        }

        ConfigData previous = _controller.Config;
        var updated = new ConfigData
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
        };
        _controller.ApplyConfig(updated);
        SaveSettings();
    }

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

    private void Shutdown()
    {
        if (_shuttingDown)
        {
            return;
        }
        _shuttingDown = true;
        _uiTimer?.Stop();
        _controller.Stop();
        _controller.DestroyMonitors();
        _tray?.Cleanup();
        Application.Current.Exit();
    }

    // ---- Settings ---------------------------------------------------------

    private void SaveSettings()
    {
        ConfigData cfg = _controller.Config;
        PointInt32 pos = this.AppWindow.Position;
        SizeInt32 size = this.AppWindow.Size;
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
            WindowGeometry = new WindowGeometry { X = pos.X, Y = pos.Y, Width = size.Width, Height = size.Height },
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

        string statusText = state.MonitorStatus;
        if (!string.IsNullOrEmpty(state.LastError))
        {
            statusText += " | " + state.LastError;
        }

        if (_controller.Config.ShowLux)
        {
            LuxLabel.Text = state.CurrentLux is double lux
                ? $"Current lux: {lux.ToString("F2", CultureInfo.InvariantCulture)}"
                : "Current lux: n/a";
        }
        else
        {
            LuxLabel.Text = "Current lux: hidden";
        }

        TargetLabel.Text = state.TargetBrightness is int target
            ? $"Target brightness: {target}%"
            : "Target brightness: n/a";

        var lines = new System.Collections.Generic.List<string>();
        if (!string.IsNullOrEmpty(statusText))
        {
            lines.Add(statusText);
            lines.Add("---");
        }
        lines.AddRange(state.MonitorList);
        UpdateMonitorList(lines);
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

    private async System.Threading.Tasks.Task ShowError(string message)
    {
        var dialog = new ContentDialog
        {
            Title = "Input error",
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = this.Content.XamlRoot,
        };
        await dialog.ShowAsync();
    }
}
