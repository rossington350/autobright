using System;
using Microsoft.Win32;

namespace AutoBright;

/// <summary>
/// Manages the per-user "run at Windows startup" entry under
/// HKCU\Software\Microsoft\Windows\CurrentVersion\Run. When enabled, the value
/// points at the current executable and, if requested, appends --start-minimized
/// so the app launches straight to the tray. All operations are best-effort: a
/// locked-down registry never crashes the app.
/// </summary>
public static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AutoBright";

    /// <summary>
    /// Write or remove the startup entry to match the saved settings, keeping the
    /// stored command (and exe path) current. Call on every settings change and at
    /// launch so a moved/reinstalled executable stays correctly registered.
    /// </summary>
    public static void Apply(bool runAtStartup, bool startMinimized)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (runAtStartup)
            {
                string exe = Environment.ProcessPath ?? "";
                if (string.IsNullOrEmpty(exe))
                {
                    return;
                }
                string command = startMinimized ? $"\"{exe}\" --start-minimized" : $"\"{exe}\"";
                key.SetValue(ValueName, command, RegistryValueKind.String);
            }
            else if (key.GetValue(ValueName) != null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // Best-effort; ignore registry access failures.
        }
    }

    /// <summary>True if a startup entry currently exists.</summary>
    public static bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) != null;
        }
        catch
        {
            return false;
        }
    }
}
