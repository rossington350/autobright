using AutoBright;
using Xunit;

namespace AutoBright.Tests;

public class CommandLineOptionsTests
{
    [Fact]
    public void Parse_IgnoresExecutablePath()
    {
        var options = CommandLineOptions.Parse(new[] { "AutoBright.exe", "--min-lux", "10" });
        Assert.Equal(10.0, options.MinLux);
    }

    [Fact]
    public void Parse_AllNumericOptions()
    {
        var argv = new[]
        {
            "AutoBright.exe",
            "--min-lux", "12.5",
            "--max-lux", "250",
            "--min-brightness", "10",
            "--max-brightness", "90",
            "--update-rate", "2.5",
            "--max-brightness-rate", "5",
            "--lux-hysteresis", "3",
            "--remote-hub", "192.168.1.5",
        };
        var options = CommandLineOptions.Parse(argv);

        Assert.Equal(12.5, options.MinLux);
        Assert.Equal(250.0, options.MaxLux);
        Assert.Equal(10, options.MinBrightness);
        Assert.Equal(90, options.MaxBrightness);
        Assert.Equal(2.5, options.Interval);
        Assert.Equal(5.0, options.MaxBrightnessRate);
        Assert.Equal(3.0, options.LuxHysteresis);
        Assert.Equal("192.168.1.5", options.RemoteHub);
    }

    [Fact]
    public void Parse_RemoteHubOption()
    {
        var options = CommandLineOptions.Parse(new[] { "AutoBright.exe", "--remote-hub", "http://example.com" });
        Assert.Equal("http://example.com", options.RemoteHub);
    }

    [Fact]
    public void Parse_UpdateRateAndIntervalAreAliases()
    {
        var options1 = CommandLineOptions.Parse(new[] { "AutoBright.exe", "--update-rate", "30.0" });
        var options2 = CommandLineOptions.Parse(new[] { "AutoBright.exe", "--interval", "40.0" });
        Assert.Equal(30.0, options1.Interval);
        Assert.Equal(40.0, options2.Interval);
    }

    [Fact]
    public void Parse_FlagOptionsSetTrueWhenPresent()
    {
        var options = CommandLineOptions.Parse(new[]
        {
            "AutoBright.exe", "--show-lux", "--minimize-to-tray", "--start-minimized", "--debug",
        });
        Assert.True(options.ShowLux);
        Assert.True(options.MinimizeToTray);
        Assert.True(options.StartMinimized);
        Assert.True(options.Debug);
    }

    [Fact]
    public void Parse_NoArguments_AllNullableOptionsAreNullAndDebugIsFalse()
    {
        var options = CommandLineOptions.Parse(new[] { "AutoBright.exe" });
        Assert.Null(options.MinLux);
        Assert.Null(options.MaxLux);
        Assert.Null(options.MinBrightness);
        Assert.Null(options.MaxBrightness);
        Assert.Null(options.RemoteHub);
        Assert.Null(options.Interval);
        Assert.Null(options.MaxBrightnessRate);
        Assert.Null(options.LuxHysteresis);
        Assert.Null(options.ShowLux);
        Assert.Null(options.MinimizeToTray);
        Assert.Null(options.StartMinimized);
        Assert.False(options.Debug);
    }

    [Fact]
    public void Parse_UnparsableDoubleValue_LeavesOptionNull()
    {
        var options = CommandLineOptions.Parse(new[] { "AutoBright.exe", "--min-lux", "notanumber" });
        Assert.Null(options.MinLux);
    }

    [Fact]
    public void Parse_UnparsableIntValue_LeavesOptionNull()
    {
        var options = CommandLineOptions.Parse(new[] { "AutoBright.exe", "--max-brightness", "notanumber" });
        Assert.Null(options.MaxBrightness);
    }

    [Fact]
    public void Parse_NoFollowingValue_LeavesOptionNullRatherThanThrowing()
    {
        var options = CommandLineOptions.Parse(new[] { "AutoBright.exe", "--min-lux" });
        Assert.Null(options.MinLux);
    }

    [Fact]
    public void Parse_UnrecognizedOption_IsIgnoredAndDoesNotAffectSubsequentOptions()
    {
        var options = CommandLineOptions.Parse(new[] { "AutoBright.exe", "--unknown-option", "value", "--min-lux", "10" });
        Assert.Equal(10.0, options.MinLux);
    }
}
