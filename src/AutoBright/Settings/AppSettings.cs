using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoBright.Controller;

namespace AutoBright.Settings;

public sealed class WindowGeometry
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

/// <summary>
/// Serialized application settings, persisted as JSON under
/// %APPDATA%/BrightnessDimmer/settings.json. Property names are written in
/// snake_case to mirror the original application's settings file.
/// </summary>
public sealed class AppSettings
{
    public double MinLux { get; set; } = 50.0;
    public double MaxLux { get; set; } = 300.0;
    public int MinBrightness { get; set; } = 20;
    public int MaxBrightness { get; set; } = 100;
    public double Interval { get; set; } = 1.0;
    public double MaxBrightnessRate { get; set; } = 25.0;
    public double LuxHysteresis { get; set; } = 10.0;
    public bool ShowLux { get; set; }
    public bool MinimizeToTray { get; set; }
    public bool StartMinimized { get; set; }
    public bool RunAtStartup { get; set; }
    public string RemoteHub { get; set; } = "usb";
    public bool ManualControl { get; set; }
    public ManualMode ManualMode { get; set; } = ManualMode.Manual;
    public int ManualBrightness { get; set; } = 50;
    public bool ManualAbsolute { get; set; }
    public WindowGeometry? WindowGeometry { get; set; }

    private const string AppName = "BrightnessDimmer";
    private const string FileName = "settings.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static string GetSettingsPath()
    {
        string baseDir = Environment.GetEnvironmentVariable("APPDATA")
                         ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string dir = Path.Combine(baseDir, AppName);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, FileName);
    }

    /// <summary>Load settings, returning defaults on any error.</summary>
    public static AppSettings Load()
    {
        try
        {
            string path = GetSettingsPath();
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(json, Options);
                if (loaded != null)
                {
                    return loaded;
                }
            }
        }
        catch
        {
            // Fall through to defaults.
        }
        return new AppSettings();
    }

    /// <summary>
    /// Save settings. Throws on failure so callers can surface the error. Writes
    /// to a temp file, flushes it to disk, and only then renames it over the
    /// real path. The flush matters: without it, NTFS can commit the rename
    /// before the file data, and a power cut would leave an empty settings.json.
    /// On failure the temp file is removed and the existing settings.json is
    /// left untouched.
    /// </summary>
    public void Save() => SaveTo(GetSettingsPath());

    internal void SaveTo(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(this, Options);
        string tempPath = path + ".tmp";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // Best effort; a stale .tmp is harmless and overwritten next save.
            }
            throw;
        }
    }
}
