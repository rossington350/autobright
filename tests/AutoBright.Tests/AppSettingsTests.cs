using System;
using System.IO;
using AutoBright.Controller;
using AutoBright.Settings;
using Xunit;

namespace AutoBright.Tests;

public class AppSettingsTests : IDisposable
{
    private readonly string? _originalAppData;
    private readonly string _tempDir;

    public AppSettingsTests()
    {
        _originalAppData = Environment.GetEnvironmentVariable("APPDATA");
        _tempDir = Path.Combine(Path.GetTempPath(), "AutoBrightTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
        Environment.SetEnvironmentVariable("APPDATA", _tempDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("APPDATA", _originalAppData);
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void GetSettingsPath_ReturnsCorrectPathAndCreatesDirectory()
    {
        string expectedPath = Path.Combine(_tempDir, "BrightnessDimmer", "settings.json");
        string actualPath = AppSettings.GetSettingsPath();
        Assert.Equal(expectedPath, actualPath);
        Assert.True(Directory.Exists(Path.GetDirectoryName(actualPath)!));
    }

    [Fact]
    public void Load_NoFile_ReturnsDefaultSettings()
    {
        AppSettings settings = AppSettings.Load();
        Assert.Equal(50.0, settings.MinLux);
        Assert.Equal(300.0, settings.MaxLux);
        Assert.Equal("usb", settings.RemoteHub);
    }

    [Fact]
    public void Load_InvalidJson_ReturnsDefaultSettings()
    {
        string path = AppSettings.GetSettingsPath();
        File.WriteAllText(path, "invalid json");
        AppSettings settings = AppSettings.Load();
        Assert.Equal(50.0, settings.MinLux);
        Assert.Equal(300.0, settings.MaxLux);
        Assert.Equal("usb", settings.RemoteHub);
    }

    [Fact]
    public void Save_RoundTripsAllFields()
    {
        var settingsToSave = new AppSettings
        {
            MinLux = 45.0,
            MaxLux = 290.0,
            MinBrightness = 15,
            MaxBrightness = 95,
            Interval = 0.5,
            MaxBrightnessRate = 30.0,
            LuxHysteresis = 8.0,
            ShowLux = true,
            MinimizeToTray = false,
            StartMinimized = true,
            RunAtStartup = false,
            RemoteHub = "bluetooth",
            ManualControl = true,
            ManualMode = ManualMode.Max,
            ManualBrightness = 40,
            ManualAbsolute = true,
            WindowGeometry = new WindowGeometry { X = 10, Y = 20, Width = 300, Height = 400 },
        };

        settingsToSave.Save();
        AppSettings loadedSettings = AppSettings.Load();

        Assert.Equal(settingsToSave.MinLux, loadedSettings.MinLux);
        Assert.Equal(settingsToSave.MaxLux, loadedSettings.MaxLux);
        Assert.Equal(settingsToSave.MinBrightness, loadedSettings.MinBrightness);
        Assert.Equal(settingsToSave.MaxBrightness, loadedSettings.MaxBrightness);
        Assert.Equal(settingsToSave.Interval, loadedSettings.Interval);
        Assert.Equal(settingsToSave.MaxBrightnessRate, loadedSettings.MaxBrightnessRate);
        Assert.Equal(settingsToSave.LuxHysteresis, loadedSettings.LuxHysteresis);
        Assert.Equal(settingsToSave.ShowLux, loadedSettings.ShowLux);
        Assert.Equal(settingsToSave.MinimizeToTray, loadedSettings.MinimizeToTray);
        Assert.Equal(settingsToSave.StartMinimized, loadedSettings.StartMinimized);
        Assert.Equal(settingsToSave.RunAtStartup, loadedSettings.RunAtStartup);
        Assert.Equal(settingsToSave.RemoteHub, loadedSettings.RemoteHub);
        Assert.Equal(settingsToSave.ManualControl, loadedSettings.ManualControl);
        Assert.Equal(settingsToSave.ManualMode, loadedSettings.ManualMode);
        Assert.Equal(settingsToSave.ManualBrightness, loadedSettings.ManualBrightness);
        Assert.Equal(settingsToSave.ManualAbsolute, loadedSettings.ManualAbsolute);

        Assert.NotNull(loadedSettings.WindowGeometry);
        Assert.Equal(settingsToSave.WindowGeometry.X, loadedSettings.WindowGeometry!.X);
        Assert.Equal(settingsToSave.WindowGeometry.Y, loadedSettings.WindowGeometry.Y);
        Assert.Equal(settingsToSave.WindowGeometry.Width, loadedSettings.WindowGeometry.Width);
        Assert.Equal(settingsToSave.WindowGeometry.Height, loadedSettings.WindowGeometry.Height);
    }

    [Fact]
    public void Save_WritesJsonWithSnakeCaseKeysAndEnumAsString()
    {
        var settings = new AppSettings
        {
            MinLux = 45.0,
            MaxBrightnessRate = 30.0,
            ManualMode = ManualMode.Max,
            WindowGeometry = new WindowGeometry { X = 10, Y = 20, Width = 300, Height = 400 },
        };

        settings.Save();
        string jsonContent = File.ReadAllText(AppSettings.GetSettingsPath());

        Assert.Contains("\"min_lux\"", jsonContent);
        Assert.Contains("\"max_brightness_rate\"", jsonContent);
        Assert.Contains("\"manual_mode\": \"max\"", jsonContent);
    }
}
