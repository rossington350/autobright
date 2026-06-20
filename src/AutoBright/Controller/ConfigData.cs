namespace AutoBright.Controller;

/// <summary>
/// An immutable snapshot of all tunable parameters. The controller publishes a
/// new instance on every change and the worker reads one reference per cycle, so
/// configuration updates are lock-free and atomic.
/// </summary>
public sealed class ConfigData
{
    public double MinLux { get; init; } = 50.0;
    public double MaxLux { get; init; } = 300.0;
    public int MinBrightness { get; init; } = 20;
    public int MaxBrightness { get; init; } = 100;
    public double Interval { get; init; } = 1.0;
    public double MaxBrightnessRate { get; init; } = 25.0;
    public double LuxHysteresis { get; init; } = 10.0;
    public bool ShowLux { get; init; }
    public bool MinimizeToTray { get; init; }
    public bool StartMinimized { get; init; }
    public string RemoteHub { get; init; } = "usb";

    /// <summary>
    /// Apply the same auto-correction the original used when values arrive from
    /// the CLI or a settings file at startup.
    /// </summary>
    public ConfigData Normalized()
    {
        double maxLux = MaxLux;
        int maxBrightness = MaxBrightness;
        double interval = Interval;
        double rate = MaxBrightnessRate;
        double hysteresis = LuxHysteresis;

        if (MinLux >= maxLux)
        {
            maxLux = MinLux + 1.0;
        }
        if (MinBrightness >= maxBrightness)
        {
            maxBrightness = System.Math.Min(100, MinBrightness + 1);
        }
        if (interval <= 0)
        {
            interval = 1.0;
        }
        if (rate < 0)
        {
            rate = 0.0;
        }
        if (hysteresis < 0)
        {
            hysteresis = 0.0;
        }

        return new ConfigData
        {
            MinLux = MinLux,
            MaxLux = maxLux,
            MinBrightness = MinBrightness,
            MaxBrightness = maxBrightness,
            Interval = interval,
            MaxBrightnessRate = rate,
            LuxHysteresis = hysteresis,
            ShowLux = ShowLux,
            MinimizeToTray = MinimizeToTray,
            StartMinimized = StartMinimized,
            RemoteHub = RemoteHub,
        };
    }
}
