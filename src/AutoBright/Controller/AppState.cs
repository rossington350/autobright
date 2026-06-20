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
    public List<string> MonitorList { get; set; } = new();

    public AppState Clone() => new()
    {
        CurrentLux = CurrentLux,
        TargetBrightness = TargetBrightness,
        MonitorStatus = MonitorStatus,
        LastError = LastError,
        MonitorList = new List<string>(MonitorList),
    };
}
