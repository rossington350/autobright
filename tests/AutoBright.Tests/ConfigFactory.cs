using AutoBright.Controller;

namespace AutoBright.Tests;

/// <summary>Builds <see cref="ConfigData"/> instances for tests without going through Normalized().</summary>
internal static class ConfigFactory
{
    public static ConfigData Make(
        double minLux = 50.0,
        double maxLux = 300.0,
        int minBrightness = 20,
        int maxBrightness = 100,
        double interval = 1.0,
        double maxBrightnessRate = 25.0,
        double luxHysteresis = 10.0,
        bool manualControl = false,
        ManualMode manualMode = ManualMode.Manual,
        int manualBrightness = 50,
        bool manualAbsolute = false)
    {
        return new ConfigData
        {
            MinLux = minLux,
            MaxLux = maxLux,
            MinBrightness = minBrightness,
            MaxBrightness = maxBrightness,
            Interval = interval,
            MaxBrightnessRate = maxBrightnessRate,
            LuxHysteresis = luxHysteresis,
            ManualControl = manualControl,
            ManualMode = manualMode,
            ManualBrightness = manualBrightness,
            ManualAbsolute = manualAbsolute,
        };
    }
}
