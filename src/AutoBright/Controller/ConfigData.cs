using System;

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
///
/// A record so MainPage can compare "did anything actually change" with plain
/// value equality (<c>==</c>/<c>!=</c>) instead of a hand-rolled comparer.
/// <see cref="ShowLux"/> and <see cref="RemoteHub"/> are included in that
/// equality even though they aren't surfaced as editable UI controls: every
/// caller that builds a comparison candidate (MainPage.TryBuildConfig) always
/// carries those two fields over unchanged from the previous config, so they
/// never differ and including them is safe.
/// </summary>
public sealed record ConfigData
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
    public bool RunAtStartup { get; init; }
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
    /// Upper bound for lux thresholds. Direct sunlight is ~100k lux, so this is
    /// far above anything meaningful, while keeping <c>MinLux + 1</c> exactly
    /// representable (no precision collapse at huge magnitudes).
    /// </summary>
    internal const double MaxLuxLimit = 1_000_000.0;

    /// <summary>
    /// Upper bound for the update interval (seconds). Also keeps
    /// <c>TimeSpan.FromSeconds(Interval)</c> well clear of overflow.
    /// </summary>
    internal const double MaxIntervalSeconds = 3600.0;

    /// <summary>
    /// Apply the same auto-correction the original used when values arrive from
    /// the CLI or a settings file at startup. Afterwards every value is finite
    /// (no NaN/Infinity), lux thresholds lie in [0, <see cref="MaxLuxLimit"/>]
    /// with MinLux &lt; MaxLux, brightness limits lie in 0-100 with
    /// MinBrightness &lt; MaxBrightness, and Interval lies in
    /// (0, <see cref="MaxIntervalSeconds"/>].
    /// </summary>
    public ConfigData Normalized()
    {
        // Non-finite values fall back to defaults. Every guard below is written
        // so NaN fails it: comparisons with NaN are always false.
        double minLux = double.IsFinite(MinLux) ? Math.Clamp(MinLux, 0.0, MaxLuxLimit) : 50.0;
        double maxLux = double.IsFinite(MaxLux) ? Math.Clamp(MaxLux, 0.0, MaxLuxLimit) : 300.0;
        if (minLux >= maxLux)
        {
            maxLux = minLux + 1.0;
        }

        // Enforce the documented 0-100 domain (not just the min<max relationship)
        // so a hand-edited or corrupted settings file can't carry an out-of-range
        // brightness value past this point. When min is already 100 there is no
        // room above it, so lower min instead of leaving min == max.
        int minBrightness = Math.Clamp(MinBrightness, 0, 100);
        int maxBrightness = Math.Clamp(MaxBrightness, 0, 100);
        if (minBrightness >= maxBrightness)
        {
            if (minBrightness >= 100)
            {
                minBrightness = 99;
                maxBrightness = 100;
            }
            else
            {
                maxBrightness = minBrightness + 1;
            }
        }

        double interval = Interval > 0 && double.IsFinite(Interval)
            ? Math.Min(Interval, MaxIntervalSeconds)
            : 1.0;

        // For both of these, 0 means "disabled". Negative and non-finite values
        // are meaningless, so they map to 0 too.
        double rate = MaxBrightnessRate >= 0 && double.IsFinite(MaxBrightnessRate) ? MaxBrightnessRate : 0.0;
        double hysteresis = LuxHysteresis >= 0 && double.IsFinite(LuxHysteresis) ? LuxHysteresis : 0.0;

        return this with
        {
            MinLux = minLux,
            MaxLux = maxLux,
            MinBrightness = minBrightness,
            MaxBrightness = maxBrightness,
            Interval = interval,
            MaxBrightnessRate = rate,
            LuxHysteresis = hysteresis,
            ManualBrightness = Math.Clamp(ManualBrightness, 0, 100),
        };
    }
}
