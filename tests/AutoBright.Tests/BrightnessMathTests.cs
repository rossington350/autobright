using System;
using AutoBright.Controller;
using AutoBright.Hardware;
using Xunit;

namespace AutoBright.Tests;

/// <summary>
/// Tests for the pure, stateless mapping helpers in BrightnessController:
/// lux -> brightness (SPECIFICATION.md §4.1), manual target selection (§4.5),
/// and percent <-> hardware raw-value translation (§4.4).
/// </summary>
public class BrightnessMathTests
{
    [Theory]
    [InlineData(0, 20)]     // below min_lux clamps to min_brightness
    [InlineData(50, 20)]    // exactly at min_lux
    [InlineData(300, 100)]  // exactly at max_lux
    [InlineData(1000, 100)] // above max_lux clamps to max_brightness
    [InlineData(175, 60)]   // midpoint: 20 + (175-50)*(100-20)/(300-50) = 60
    public void ComputeBrightness_LinearMapping(double lux, int expected)
    {
        var cfg = ConfigFactory.Make();
        Assert.Equal(expected, BrightnessController.ComputeBrightness(cfg, lux));
    }

    [Fact]
    public void ComputeBrightness_NullLux_ReturnsNull()
    {
        var cfg = ConfigFactory.Make();
        Assert.Null(BrightnessController.ComputeBrightness(cfg, null));
    }

    [Fact]
    public void ComputeBrightness_RoundsAwayFromZero()
    {
        // 0-10 lux -> 0-3 brightness; lux=5 -> raw 1.5, rounds away from zero to 2.
        var cfg = ConfigFactory.Make(minLux: 0, maxLux: 10, minBrightness: 0, maxBrightness: 3);
        Assert.Equal(2, BrightnessController.ComputeBrightness(cfg, 5));
    }

    [Fact]
    public void ManualTarget_Max_UsesConfiguredMaxWhenNotAbsolute()
    {
        var cfg = ConfigFactory.Make(manualMode: ManualMode.Max, manualAbsolute: false, maxBrightness: 80);
        Assert.Equal(80, BrightnessController.ManualTarget(cfg));
    }

    [Fact]
    public void ManualTarget_Max_Absolute_Is100()
    {
        var cfg = ConfigFactory.Make(manualMode: ManualMode.Max, manualAbsolute: true, maxBrightness: 80);
        Assert.Equal(100, BrightnessController.ManualTarget(cfg));
    }

    [Fact]
    public void ManualTarget_Min_UsesConfiguredMinWhenNotAbsolute()
    {
        var cfg = ConfigFactory.Make(manualMode: ManualMode.Min, manualAbsolute: false, minBrightness: 30);
        Assert.Equal(30, BrightnessController.ManualTarget(cfg));
    }

    [Fact]
    public void ManualTarget_Min_Absolute_Is0()
    {
        var cfg = ConfigFactory.Make(manualMode: ManualMode.Min, manualAbsolute: true, minBrightness: 30);
        Assert.Equal(0, BrightnessController.ManualTarget(cfg));
    }

    [Theory]
    [InlineData(50, false, 20, 100, 50)]   // within the min/max band
    [InlineData(5, false, 20, 100, 20)]    // clamped to band minimum
    [InlineData(150, false, 20, 100, 100)] // clamped to band maximum
    [InlineData(5, true, 20, 100, 5)]      // absolute: band ignored
    [InlineData(150, true, 20, 100, 100)]  // absolute: clamped to 100
    [InlineData(-5, true, 20, 100, 0)]     // absolute: clamped to 0
    public void ManualTarget_Manual_ClampsPerAbsoluteFlag(int manualBrightness, bool absolute, int minB, int maxB, int expected)
    {
        var cfg = ConfigFactory.Make(manualMode: ManualMode.Manual, manualAbsolute: absolute, manualBrightness: manualBrightness, minBrightness: minB, maxBrightness: maxB);
        Assert.Equal(expected, BrightnessController.ManualTarget(cfg));
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(50, 60)]
    [InlineData(100, 100)]
    public void PercentToMonitorValue_ScalesIntoHardwareRange(int percent, int expectedRaw)
    {
        var monitor = new MonitorInfo(IntPtr.Zero, "Test", minBrightness: 20, curBrightness: 50, maxBrightness: 100);
        Assert.Equal(expectedRaw, BrightnessController.PercentToMonitorValue(monitor, percent));
    }

    [Fact]
    public void PercentToMonitorValue_NoUsableRange_UsesPercentDirectly()
    {
        var monitor = new MonitorInfo(IntPtr.Zero, "Test", minBrightness: 50, curBrightness: 50, maxBrightness: 50);
        Assert.Equal(37, BrightnessController.PercentToMonitorValue(monitor, 37));
    }

    [Fact]
    public void PercentToMonitorValue_ClampsOutOfRangePercent()
    {
        var monitor = new MonitorInfo(IntPtr.Zero, "Test", minBrightness: 0, curBrightness: 0, maxBrightness: 100);
        Assert.Equal(100, BrightnessController.PercentToMonitorValue(monitor, 150));
        Assert.Equal(0, BrightnessController.PercentToMonitorValue(monitor, -20));
    }

    [Fact]
    public void PercentToMonitorValue_NullPercent_ReturnsNull()
    {
        var monitor = new MonitorInfo(IntPtr.Zero, "Test", 0, 0, 100);
        Assert.Null(BrightnessController.PercentToMonitorValue(monitor, null));
    }

    [Theory]
    [InlineData(20, 0)]
    [InlineData(60, 50)]
    [InlineData(100, 100)]
    public void MonitorValueToPercent_ScalesFromHardwareRange(int raw, int expectedPct)
    {
        var monitor = new MonitorInfo(IntPtr.Zero, "Test", minBrightness: 20, curBrightness: 0, maxBrightness: 100);
        Assert.Equal(expectedPct, BrightnessController.MonitorValueToPercent(monitor, raw));
    }

    [Fact]
    public void MonitorValueToPercent_NoUsableRange_ReturnsRawValue()
    {
        var monitor = new MonitorInfo(IntPtr.Zero, "Test", 50, 50, 50);
        Assert.Equal(42, BrightnessController.MonitorValueToPercent(monitor, 42));
    }
}
