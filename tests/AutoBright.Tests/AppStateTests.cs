using System.Collections.Generic;
using AutoBright.Controller;
using Xunit;

namespace AutoBright.Tests;

public class AppStateTests
{
    private static MonitorRow Row(string name) => new(name, 50, 50, 0, 100);

    [Fact]
    public void FreshlyConstructedAppState_HasDefaultValues()
    {
        var appState = new AppState();

        Assert.Null(appState.CurrentLux);
        Assert.Null(appState.TargetBrightness);
        Assert.Equal("Starting...", appState.MonitorStatus);
        Assert.Empty(appState.LastError);
        Assert.Empty(appState.Monitors);
    }

    [Fact]
    public void Clone_CopiesScalarFields()
    {
        var original = new AppState
        {
            CurrentLux = 100.5,
            TargetBrightness = 75,
            MonitorStatus = "Active",
            LastError = "An error occurred"
        };

        var clone = original.Clone();

        Assert.Equal(original.CurrentLux, clone.CurrentLux);
        Assert.Equal(original.TargetBrightness, clone.TargetBrightness);
        Assert.Equal(original.MonitorStatus, clone.MonitorStatus);
        Assert.Equal(original.LastError, clone.LastError);
    }

    [Fact]
    public void Clone_CopiesMonitorsContents()
    {
        var original = new AppState
        {
            Monitors = new List<MonitorRow> { Row("Monitor1"), Row("Monitor2") }
        };

        var clone = original.Clone();

        Assert.Equal(original.Monitors.Count, clone.Monitors.Count);
        for (int i = 0; i < original.Monitors.Count; i++)
        {
            Assert.Equal(original.Monitors[i], clone.Monitors[i]);
        }
    }

    [Fact]
    public void Clone_AllocatesNewMonitorsInstance()
    {
        var original = new AppState
        {
            Monitors = new List<MonitorRow> { Row("Monitor1"), Row("Monitor2") }
        };

        var clone = original.Clone();

        Assert.NotSame(original.Monitors, clone.Monitors);

        // Mutate the clone's list and verify it doesn't affect the original
        clone.Monitors.Add(Row("Monitor3"));
        Assert.Equal(2, original.Monitors.Count);
        Assert.Equal(3, clone.Monitors.Count);

        // Mutate the original's list and verify it doesn't affect the clone
        original.Monitors.Add(Row("Monitor4"));
        Assert.Equal(3, original.Monitors.Count);
        Assert.Equal(3, clone.Monitors.Count);
    }
}