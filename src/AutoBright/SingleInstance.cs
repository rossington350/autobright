using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace AutoBright;

/// <summary>
/// Ensures only one AutoBright process runs per session. A second instance
/// would spin up its own worker driving the same monitors via DDC/CI and
/// saving the same settings.json. Instead, a later launch signals the running
/// instance to show its window (e.g. restore it from the tray) and exits.
/// </summary>
internal static class SingleInstance
{
    private const string MutexName = "Local\\AutoBright_SingleInstance";
    private const string ShowEventName = "Local\\AutoBright_ShowWindow";

    // Held for the process lifetime and never disposed; the OS releases both
    // when the process exits.
    private static Mutex? _mutex;
    private static EventWaitHandle? _showEvent;

    /// <summary>
    /// Returns true if this process is the primary instance. Otherwise it asks
    /// the primary instance to show itself and returns false, and the caller
    /// should exit.
    /// </summary>
    public static bool TryClaim()
    {
        try
        {
            var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
            if (!createdNew)
            {
                mutex.Dispose();
                SignalPrimary();
                return false;
            }
            _mutex = mutex;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or IOException)
        {
            // The mutex exists but belongs to a different security context (for
            // example an elevated instance), so another instance is running.
            SignalPrimary();
            return false;
        }

        try
        {
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or IOException)
        {
            // Not fatal: later launches just won't be able to bring this window forward.
            _showEvent = null;
        }
        return true;
    }

    /// <summary>
    /// Invoke <paramref name="onShowRequested"/> (on a thread-pool thread)
    /// whenever another launch asks this instance to show itself. A request
    /// made before this is called is kept and delivered right away.
    /// </summary>
    public static void ListenForShowRequests(Action onShowRequested)
    {
        if (_showEvent == null)
        {
            return;
        }
        ThreadPool.RegisterWaitForSingleObject(
            _showEvent,
            (_, _) => onShowRequested(),
            state: null,
            millisecondsTimeOutInterval: Timeout.Infinite,
            executeOnlyOnce: false);
    }

    private static void SignalPrimary()
    {
        try
        {
            // This process was launched by the user and so may take the
            // foreground. Pass that permission on so the primary instance's
            // Activate() actually brings its window to the front.
            AllowSetForegroundWindow(ASFW_ANY);
            using var showEvent = EventWaitHandle.OpenExisting(ShowEventName);
            showEvent.Set();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or IOException)
        {
            // Primary is in another security context or hasn't created the
            // event yet; nothing more we can do.
        }
    }

    private const uint ASFW_ANY = unchecked((uint)-1);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint dwProcessId);
}
