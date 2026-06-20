using System;
using System.Globalization;

namespace AutoBright;

/// <summary>
/// Parses command-line options. Every value-bearing option is nullable so that
/// "not supplied" can fall back to a saved setting and then a built-in default
/// (matching the original argparse precedence: CLI &gt; settings &gt; default).
/// </summary>
public sealed class CommandLineOptions
{
    public double? MinLux { get; private set; }
    public double? MaxLux { get; private set; }
    public int? MinBrightness { get; private set; }
    public int? MaxBrightness { get; private set; }
    public string? RemoteHub { get; private set; }
    public double? Interval { get; private set; }
    public double? MaxBrightnessRate { get; private set; }
    public double? LuxHysteresis { get; private set; }
    public bool? ShowLux { get; private set; }
    public bool? MinimizeToTray { get; private set; }
    public bool? StartMinimized { get; private set; }
    public bool Debug { get; private set; }

    public static CommandLineOptions Parse(string[] argv)
    {
        var options = new CommandLineOptions();
        // argv[0] is the executable path; skip it.
        for (int i = 1; i < argv.Length; i++)
        {
            string arg = argv[i];
            switch (arg)
            {
                case "--min-lux":
                    options.MinLux = ParseDouble(NextValue(argv, ref i));
                    break;
                case "--max-lux":
                    options.MaxLux = ParseDouble(NextValue(argv, ref i));
                    break;
                case "--min-brightness":
                    options.MinBrightness = ParseInt(NextValue(argv, ref i));
                    break;
                case "--max-brightness":
                    options.MaxBrightness = ParseInt(NextValue(argv, ref i));
                    break;
                case "--remote-hub":
                    options.RemoteHub = NextValue(argv, ref i);
                    break;
                case "--update-rate":
                case "--interval":
                    options.Interval = ParseDouble(NextValue(argv, ref i));
                    break;
                case "--max-brightness-rate":
                    options.MaxBrightnessRate = ParseDouble(NextValue(argv, ref i));
                    break;
                case "--lux-hysteresis":
                    options.LuxHysteresis = ParseDouble(NextValue(argv, ref i));
                    break;
                case "--show-lux":
                    options.ShowLux = true;
                    break;
                case "--minimize-to-tray":
                    options.MinimizeToTray = true;
                    break;
                case "--start-minimized":
                    options.StartMinimized = true;
                    break;
                case "--debug":
                    options.Debug = true;
                    break;
            }
        }
        return options;
    }

    private static string? NextValue(string[] argv, ref int i)
    {
        if (i + 1 < argv.Length)
        {
            return argv[++i];
        }
        return null;
    }

    private static double? ParseDouble(string? value)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result) ? result : null;

    private static int? ParseInt(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) ? result : null;
}
