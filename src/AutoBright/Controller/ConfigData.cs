namespace AutoBright.Controller;

/// <summary>
/// How brightness is chosen while <see cref="ConfigData.ManualControl"/> is on.
/// </summary>
public enum ManualMode
{
    /// <summary>Drive the user-set slider value.</summary>
    Manual,
    /// <summary>Pin to the configured minimum brightness.</summary>
    Min,
    /// <summary>Pin to the configured maximum brightness.</summary>
    Max,
}

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

    // ---- Manual override --------------------------------------------------
    // When ManualControl is on, ambient-light mapping is bypassed and the target
    // brightness comes from ManualMode (and ManualBrightness for Manual mode).
    public bool ManualControl { get; init; }
    public ManualMode ManualMode { get; init; } = ManualMode.Manual;
    public int ManualBrightness { get; init; } = 50;
    // When true, Min/Max/slider span the absolute 0-100% range; when false they
    // are bounded by the configured MinBrightness/MaxBrightness.
    public bool ManualAbsolute { get; init; }

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

        int manualBrightness = System.Math.Max(0, System.Math.Min(100, ManualBrightness));

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
            ManualControl = ManualControl,
            ManualMode = ManualMode,
            ManualBrightness = manualBrightness,
            ManualAbsolute = ManualAbsolute,
        };
    }
}
