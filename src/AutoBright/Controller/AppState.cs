using System.Collections.Generic;

namespace AutoBright.Controller;

/// <summary>
/// A snapshot of live runtime state shared from the worker thread to the UI.
/// The controller hands out copies so the UI never touches mutable worker state.
/// </summary>
public sealed class AppState
{
    public double? CurrentLux { get; set; }
    public int? TargetBrightness { get; set; }
    public string MonitorStatus { get; set; } = "Starting...";
    public string LastError { get; set; } = "";
    public List<MonitorRow> Monitors { get; set; } = new();

    public AppState Clone() => new()
    {
        CurrentLux = CurrentLux,
        TargetBrightness = TargetBrightness,
        MonitorStatus = MonitorStatus,
        LastError = LastError,
        Monitors = new List<MonitorRow>(Monitors),
    };
}

/// <summary>
/// One monitor as shown in the UI. Immutable, so snapshots can share instances
/// and the UI can diff rows with value equality.
/// </summary>
/// <param name="CurrentPercent">Brightness read back this cycle, as 0-100%.</param>
/// <param name="IsStale">True while showing a last-known entry during a temporary monitor loss.</param>
/// <param name="Action">What this cycle did to the monitor (e.g. "Set to 60%"), or null.</param>
public sealed record MonitorRow(
    string Name,
    int CurrentPercent,
    int CurrentRaw,
    int HardwareMin,
    int HardwareMax,
    bool IsStale = false,
    string? Action = null,
    bool ActionFailed = false);
