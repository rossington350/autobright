using AutoBright;
using AutoBright.Controller;
using AutoBright.Settings;
using Xunit;

namespace AutoBright.Tests;

/// <summary>
/// Tests for the stateful worker-loop helpers on BrightnessController: lux
/// hysteresis (SPECIFICATION.md §4.2) and brightness slew-rate smoothing (§4.3).
/// Each test uses a fresh controller instance so state does not leak between cases.
/// </summary>
public class BrightnessStatefulTests
{
    private static BrightnessController NewController() => new(new CommandLineOptions(), new AppSettings());

    [Fact]
    public void ApplyLuxHysteresis_FirstReading_CommitsImmediately()
    {
        var controller = NewController();
        var cfg = ConfigFactory.Make(luxHysteresis: 10);
        Assert.Equal(120.0, controller.ApplyLuxHysteresis(cfg, 120.0));
    }

    [Fact]
    public void ApplyLuxHysteresis_NaNReading_KeepsPreviousValue()
    {
        var controller = NewController();
        var cfg = ConfigFactory.Make(luxHysteresis: 10);
        controller.ApplyLuxHysteresis(cfg, 100.0);
        Assert.Equal(100.0, controller.ApplyLuxHysteresis(cfg, double.NaN));
        Assert.Equal(150.0, controller.ApplyLuxHysteresis(cfg, 150.0));
    }

    [Fact]
    public void ApplyLuxHysteresis_SmallChange_IsIgnored()
    {
        var controller = NewController();
        var cfg = ConfigFactory.Make(luxHysteresis: 10);
        controller.ApplyLuxHysteresis(cfg, 100.0);
        Assert.Equal(100.0, controller.ApplyLuxHysteresis(cfg, 105.0)); // delta 5 < 10
    }

    [Fact]
    public void ApplyLuxHysteresis_ChangeAtThreshold_Commits()
    {
        var controller = NewController();
        var cfg = ConfigFactory.Make(luxHysteresis: 10);
        controller.ApplyLuxHysteresis(cfg, 100.0);
        Assert.Equal(110.0, controller.ApplyLuxHysteresis(cfg, 110.0)); // delta 10 >= 10
    }

    [Fact]
    public void ApplyLuxHysteresis_ZeroDisablesFiltering()
    {
        var controller = NewController();
        var cfg = ConfigFactory.Make(luxHysteresis: 0);
        controller.ApplyLuxHysteresis(cfg, 100.0);
        Assert.Equal(100.5, controller.ApplyLuxHysteresis(cfg, 100.5));
    }

    [Fact]
    public void ApplyLuxHysteresis_NullReading_ReusesLastCommittedValue()
    {
        var controller = NewController();
        var cfg = ConfigFactory.Make(luxHysteresis: 10);
        controller.ApplyLuxHysteresis(cfg, 100.0);
        Assert.Equal(100.0, controller.ApplyLuxHysteresis(cfg, null));
    }

    [Fact]
    public void ApplyLuxHysteresis_NullBeforeAnyReading_ReturnsNull()
    {
        var controller = NewController();
        var cfg = ConfigFactory.Make();
        Assert.Null(controller.ApplyLuxHysteresis(cfg, null));
    }

    [Fact]
    public void SmoothBrightness_FirstApplication_JumpsToTarget()
    {
        var controller = NewController();
        var cfg = ConfigFactory.Make(maxBrightnessRate: 25, interval: 1);
        Assert.Equal(80, controller.SmoothBrightness(cfg, 80));
    }

    [Fact]
    public void SmoothBrightness_WithinMaxStep_JumpsToTarget()
    {
        var controller = NewController();
        var cfg = ConfigFactory.Make(maxBrightnessRate: 25, interval: 1); // maxStep = 25
        controller.SmoothBrightness(cfg, 50);
        Assert.Equal(60, controller.SmoothBrightness(cfg, 60)); // delta 10 <= 25
    }

    [Fact]
    public void SmoothBrightness_BeyondMaxStep_RampsUpwardByMaxStep()
    {
        var controller = NewController();
        var cfg = ConfigFactory.Make(maxBrightnessRate: 10, interval: 1); // maxStep = 10
        controller.SmoothBrightness(cfg, 20);
        Assert.Equal(30, controller.SmoothBrightness(cfg, 80)); // delta 60 -> +10
    }

    [Fact]
    public void SmoothBrightness_BeyondMaxStep_RampsDownwardByMaxStep()
    {
        var controller = NewController();
        var cfg = ConfigFactory.Make(maxBrightnessRate: 10, interval: 1);
        controller.SmoothBrightness(cfg, 80);
        Assert.Equal(70, controller.SmoothBrightness(cfg, 20)); // delta -60 -> -10
    }

    [Fact]
    public void SmoothBrightness_RateZero_DisablesSmoothing()
    {
        var controller = NewController();
        var cfg = ConfigFactory.Make(maxBrightnessRate: 0, interval: 1);
        controller.SmoothBrightness(cfg, 20);
        Assert.Equal(90, controller.SmoothBrightness(cfg, 90));
    }

    [Fact]
    public void SmoothBrightness_NullTarget_ReturnsNull()
    {
        var controller = NewController();
        var cfg = ConfigFactory.Make();
        Assert.Null(controller.SmoothBrightness(cfg, null));
    }

    [Fact]
    public void SmoothBrightness_MaxStepScalesWithInterval()
    {
        var controller = NewController();
        // rate 10 %/s * 2s interval = maxStep 20.
        var cfg = ConfigFactory.Make(maxBrightnessRate: 10, interval: 2);
        controller.SmoothBrightness(cfg, 0);
        Assert.Equal(20, controller.SmoothBrightness(cfg, 100)); // delta 100 -> +20
    }
}
