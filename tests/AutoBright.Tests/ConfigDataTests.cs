using AutoBright.Controller;
using Xunit;

namespace AutoBright.Tests;

public class ConfigDataTests
{
    [Fact]
    public void MinLux_GreaterThanOrEqualTo_MaxLux_CorrectsMaxLux()
    {
        var config = new ConfigData { MinLux = 100, MaxLux = 100 };
        var normalized = config.Normalized();
        Assert.Equal(100.0, normalized.MinLux);
        Assert.Equal(101.0, normalized.MaxLux);

        config = new ConfigData { MinLux = 200, MaxLux = 150 };
        normalized = config.Normalized();
        Assert.Equal(200.0, normalized.MinLux);
        Assert.Equal(201.0, normalized.MaxLux);
    }

    [Fact]
    public void MinBrightness_GreaterThanOrEqualTo_MaxBrightness_CorrectsMaxBrightness()
    {
        var config = new ConfigData { MinBrightness = 90, MaxBrightness = 90 };
        var normalized = config.Normalized();
        Assert.Equal(90, normalized.MinBrightness);
        Assert.Equal(91, normalized.MaxBrightness);

        config = new ConfigData { MinBrightness = 100, MaxBrightness = 50 };
        normalized = config.Normalized();
        Assert.Equal(100, normalized.MinBrightness);
        Assert.Equal(100, normalized.MaxBrightness);
    }

    [Fact]
    public void MinAndMaxBrightness_OutOfDomain_ClampTo0To100()
    {
        // A corrupted/hand-edited settings file could carry values outside the
        // documented 0-100 domain even when Min < Max relationally holds.
        var config = new ConfigData { MinBrightness = -500, MaxBrightness = 9000 };
        var normalized = config.Normalized();
        Assert.Equal(0, normalized.MinBrightness);
        Assert.Equal(100, normalized.MaxBrightness);
    }

    [Fact]
    public void MinBrightness_OutOfDomain_StillCorrectsMaxRelationally()
    {
        // MinBrightness clamps to 100 first, which then still exceeds MaxBrightness,
        // so the relational correction must run on the clamped value, not the raw one.
        var config = new ConfigData { MinBrightness = 500, MaxBrightness = 50 };
        var normalized = config.Normalized();
        Assert.Equal(100, normalized.MinBrightness);
        Assert.Equal(100, normalized.MaxBrightness);
    }

    [Fact]
    public void Interval_LessThanOrEqualToZero_ResetsIntervalToDefault()
    {
        var config = new ConfigData { Interval = 0 };
        var normalized = config.Normalized();
        Assert.Equal(1.0, normalized.Interval);

        config = new ConfigData { Interval = -5 };
        normalized = config.Normalized();
        Assert.Equal(1.0, normalized.Interval);
    }

    [Fact]
    public void MaxBrightnessRate_Negative_ClampsToZero()
    {
        var config = new ConfigData { MaxBrightnessRate = -10 };
        var normalized = config.Normalized();
        Assert.Equal(0.0, normalized.MaxBrightnessRate);

        config = new ConfigData { MaxBrightnessRate = 25.0 };
        normalized = config.Normalized();
        Assert.Equal(25.0, normalized.MaxBrightnessRate);
    }

    [Fact]
    public void LuxHysteresis_Negative_ClampsToZero()
    {
        var config = new ConfigData { LuxHysteresis = -10 };
        var normalized = config.Normalized();
        Assert.Equal(0.0, normalized.LuxHysteresis);

        config = new ConfigData { LuxHysteresis = 15.0 };
        normalized = config.Normalized();
        Assert.Equal(15.0, normalized.LuxHysteresis);
    }

    [Fact]
    public void ManualBrightness_ClampedIntoRange()
    {
        var config = new ConfigData { ManualBrightness = -10 };
        var normalized = config.Normalized();
        Assert.Equal(0, normalized.ManualBrightness);

        config = new ConfigData { ManualBrightness = 110 };
        normalized = config.Normalized();
        Assert.Equal(100, normalized.ManualBrightness);

        config = new ConfigData { ManualBrightness = 50 };
        normalized = config.Normalized();
        Assert.Equal(50, normalized.ManualBrightness);
    }

    [Fact]
    public void FullyValidConfigData_ReturnsUnchanged()
    {
        var config = new ConfigData
        {
            MinLux = 50.0,
            MaxLux = 300.0,
            MinBrightness = 20,
            MaxBrightness = 100,
            Interval = 1.0,
            MaxBrightnessRate = 25.0,
            LuxHysteresis = 10.0,
            ShowLux = true,
            MinimizeToTray = false,
            StartMinimized = true,
            RunAtStartup = false,
            RemoteHub = "usb",
            ManualControl = true,
            ManualMode = ManualMode.Manual,
            ManualBrightness = 50,
            ManualAbsolute = false
        };

        var normalized = config.Normalized();
        Assert.Equal(config.MinLux, normalized.MinLux);
        Assert.Equal(config.MaxLux, normalized.MaxLux);
        Assert.Equal(config.MinBrightness, normalized.MinBrightness);
        Assert.Equal(config.MaxBrightness, normalized.MaxBrightness);
        Assert.Equal(config.Interval, normalized.Interval);
        Assert.Equal(config.MaxBrightnessRate, normalized.MaxBrightnessRate);
        Assert.Equal(config.LuxHysteresis, normalized.LuxHysteresis);
        Assert.Equal(config.ShowLux, normalized.ShowLux);
        Assert.Equal(config.MinimizeToTray, normalized.MinimizeToTray);
        Assert.Equal(config.StartMinimized, normalized.StartMinimized);
        Assert.Equal(config.RunAtStartup, normalized.RunAtStartup);
        Assert.Equal(config.RemoteHub, normalized.RemoteHub);
        Assert.Equal(config.ManualControl, normalized.ManualControl);
        Assert.Equal(config.ManualMode, normalized.ManualMode);
        Assert.Equal(config.ManualBrightness, normalized.ManualBrightness);
        Assert.Equal(config.ManualAbsolute, normalized.ManualAbsolute);
    }
}