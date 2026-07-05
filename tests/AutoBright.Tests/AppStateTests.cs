using System.Collections.Generic;
using AutoBright.Controller;
using Xunit;

namespace AutoBright.Tests;

public class AppStateTests
{
    [Fact]
    public void FreshlyConstructedAppState_HasDefaultValues()
    {
        var appState = new AppState();

        Assert.Null(appState.CurrentLux);
        Assert.Null(appState.TargetBrightness);
        Assert.Equal("Starting...", appState.MonitorStatus);
        Assert.Empty(appState.LastError);
        Assert.Empty(appState.MonitorList);
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
    public void Clone_CopiesMonitorListContents()
    {
        var original = new AppState
        {
            MonitorList = new List<string> { "Monitor1", "Monitor2" }
        };

        var clone = original.Clone();

        Assert.Equal(original.MonitorList.Count, clone.MonitorList.Count);
        for (int i = 0; i < original.MonitorList.Count; i++)
        {
            Assert.Equal(original.MonitorList[i], clone.MonitorList[i]);
        }
    }

    [Fact]
    public void Clone_AllocatesNewMonitorListInstance()
    {
        var original = new AppState
        {
            MonitorList = new List<string> { "Monitor1", "Monitor2" }
        };

        var clone = original.Clone();

        Assert.NotSame(original.MonitorList, clone.MonitorList);

        // Mutate the clone's list and verify it doesn't affect the original
        clone.MonitorList.Add("Monitor3");
        Assert.Equal(2, original.MonitorList.Count);
        Assert.Equal(3, clone.MonitorList.Count);

        // Mutate the original's list and verify it doesn't affect the clone
        original.MonitorList.Add("Monitor4");
        Assert.Equal(3, original.MonitorList.Count);
        Assert.Equal(3, clone.MonitorList.Count);
    }
}