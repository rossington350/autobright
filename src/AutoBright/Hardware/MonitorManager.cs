using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace AutoBright.Hardware;

/// <summary>
/// A single physical monitor that supports hardware brightness control.
/// Min/Cur/Max are the device-defined raw brightness values (not percentages).
/// </summary>
public sealed class MonitorInfo
{
    public MonitorInfo(IntPtr handle, string name, int minBrightness, int curBrightness, int maxBrightness)
    {
        Handle = handle;
        Name = name;
        MinBrightness = minBrightness;
        CurBrightness = curBrightness;
        MaxBrightness = maxBrightness;
    }

    public IntPtr Handle { get; }
    public string Name { get; }
    public int MinBrightness { get; set; }
    public int CurBrightness { get; set; }
    public int MaxBrightness { get; set; }
}

/// <summary>
/// Enumerates physical monitors and reads/writes their brightness through the
/// Windows DDC/CI monitor configuration API (user32 + dxva2). This is a direct
/// port of the original Python ctypes implementation.
/// </summary>
public sealed class MonitorManager
{
    private List<MonitorInfo> _cachedMonitors = new();
    // Keep a reference to the enumeration callback so the GC cannot collect the
    // marshalled delegate while it is being invoked from native code.
    private MonitorEnumProc? _enumProc;

    public IReadOnlyList<MonitorInfo> EnumerateMonitors(bool refresh = false)
    {
        if (refresh || _cachedMonitors.Count == 0)
        {
            DestroyCachedMonitors();
            _cachedMonitors = CollectMonitors();
        }
        else
        {
            RefreshCachedBrightness();
            if (_cachedMonitors.Count == 0)
            {
                _cachedMonitors = CollectMonitors();
            }
        }
        return new List<MonitorInfo>(_cachedMonitors);
    }

    public IReadOnlyList<MonitorInfo> RefreshMonitors()
    {
        DestroyCachedMonitors();
        _cachedMonitors = CollectMonitors();
        return new List<MonitorInfo>(_cachedMonitors);
    }

    public bool SetBrightness(IntPtr handle, int brightness)
    {
        return SetMonitorBrightness(handle, (uint)brightness);
    }

    public void DestroyMonitors() => DestroyCachedMonitors();

    private List<MonitorInfo> CollectMonitors()
    {
        var monitors = new List<MonitorInfo>();

        bool Callback(IntPtr hMonitor, IntPtr hdc, IntPtr lprc, IntPtr data)
        {
            uint count = 0;
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, ref count) || count == 0)
            {
                return true;
            }

            var array = new PHYSICAL_MONITOR[count];
            if (!GetPhysicalMonitorsFromHMONITOR(hMonitor, count, array))
            {
                return true;
            }

            foreach (var ph in array)
            {
                uint min = 0, cur = 0, max = 0;
                if (!GetMonitorBrightness(ph.hPhysicalMonitor, ref min, ref cur, ref max))
                {
                    // Excluded from the cache, so nothing will ever destroy this
                    // handle later — release it now to avoid leaking it.
                    DestroyPhysicalMonitors(1, new[] { ph });
                    continue;
                }
                monitors.Add(new MonitorInfo(
                    ph.hPhysicalMonitor,
                    ph.szPhysicalMonitorDescription,
                    (int)min,
                    (int)cur,
                    (int)max));
            }
            return true;
        }

        _enumProc = Callback;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, _enumProc, IntPtr.Zero);
        return monitors;
    }

    private void RefreshCachedBrightness()
    {
        var valid = new List<MonitorInfo>();
        foreach (var monitor in _cachedMonitors)
        {
            uint min = 0, cur = 0, max = 0;
            if (!GetMonitorBrightness(monitor.Handle, ref min, ref cur, ref max))
            {
                // The monitor no longer responds and is being dropped from the
                // cache, so nothing will destroy this handle later — release it
                // now to avoid leaking it.
                DestroyPhysicalMonitors(1, new[]
                {
                    new PHYSICAL_MONITOR { hPhysicalMonitor = monitor.Handle, szPhysicalMonitorDescription = monitor.Name },
                });
                continue;
            }
            monitor.MinBrightness = (int)min;
            monitor.CurBrightness = (int)cur;
            monitor.MaxBrightness = (int)max;
            valid.Add(monitor);
        }
        _cachedMonitors = valid;
    }

    private void DestroyCachedMonitors()
    {
        if (_cachedMonitors.Count == 0)
        {
            return;
        }
        var array = new PHYSICAL_MONITOR[_cachedMonitors.Count];
        for (int i = 0; i < _cachedMonitors.Count; i++)
        {
            array[i].hPhysicalMonitor = _cachedMonitors[i].Handle;
        }
        DestroyPhysicalMonitors((uint)array.Length, array);
        _cachedMonitors = new List<MonitorInfo>();
    }

    // ---- Win32 interop ----------------------------------------------------

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("dxva2.dll")]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, ref uint pdwNumberOfPhysicalMonitors);

    [DllImport("dxva2.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize, [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll")]
    private static extern bool GetMonitorBrightness(IntPtr hMonitor, ref uint pdwMinimumBrightness, ref uint pdwCurrentBrightness, ref uint pdwMaximumBrightness);

    [DllImport("dxva2.dll")]
    private static extern bool SetMonitorBrightness(IntPtr hMonitor, uint dwNewBrightness);

    [DllImport("dxva2.dll")]
    private static extern bool DestroyPhysicalMonitors(uint dwPhysicalMonitorArraySize, [In] PHYSICAL_MONITOR[] pPhysicalMonitorArray);
}
