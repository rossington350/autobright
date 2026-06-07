import ctypes
import ctypes.wintypes


class MonitorInfo:
    def __init__(self, handle, name, min_brightness, cur_brightness, max_brightness):
        self.handle = handle
        self.name = name
        self.min_brightness = min_brightness
        self.cur_brightness = cur_brightness
        self.max_brightness = max_brightness


class MonitorManager:
    def __init__(self):
        self._user32 = ctypes.WinDLL('user32')
        self._dxva2 = ctypes.WinDLL('dxva2')
        self._setup_windows_functions()
        self._cached_monitors = []

    def _setup_windows_functions(self):
        self._user32.EnumDisplayMonitors.argtypes = [
            ctypes.wintypes.HDC,
            ctypes.POINTER(ctypes.wintypes.RECT),
            ctypes.WINFUNCTYPE(ctypes.wintypes.BOOL, ctypes.wintypes.HMONITOR, ctypes.wintypes.HDC, ctypes.POINTER(ctypes.wintypes.RECT), ctypes.wintypes.LPARAM),
            ctypes.wintypes.LPARAM,
        ]
        self._user32.EnumDisplayMonitors.restype = ctypes.wintypes.BOOL

        self._dxva2.GetNumberOfPhysicalMonitorsFromHMONITOR.argtypes = [
            ctypes.wintypes.HMONITOR,
            ctypes.POINTER(ctypes.wintypes.DWORD),
        ]
        self._dxva2.GetNumberOfPhysicalMonitorsFromHMONITOR.restype = ctypes.wintypes.BOOL

        self._dxva2.GetPhysicalMonitorsFromHMONITOR.argtypes = [
            ctypes.wintypes.HMONITOR,
            ctypes.wintypes.DWORD,
            ctypes.POINTER(self._PhysicalMonitor),
        ]
        self._dxva2.GetPhysicalMonitorsFromHMONITOR.restype = ctypes.wintypes.BOOL

        self._dxva2.GetMonitorBrightness.argtypes = [
            ctypes.wintypes.HANDLE,
            ctypes.POINTER(ctypes.wintypes.DWORD),
            ctypes.POINTER(ctypes.wintypes.DWORD),
            ctypes.POINTER(ctypes.wintypes.DWORD),
        ]
        self._dxva2.GetMonitorBrightness.restype = ctypes.wintypes.BOOL

        self._dxva2.SetMonitorBrightness.argtypes = [
            ctypes.wintypes.HANDLE,
            ctypes.wintypes.DWORD,
        ]
        self._dxva2.SetMonitorBrightness.restype = ctypes.wintypes.BOOL

        self._dxva2.DestroyPhysicalMonitors.argtypes = [
            ctypes.wintypes.DWORD,
            ctypes.POINTER(self._PhysicalMonitor),
        ]
        self._dxva2.DestroyPhysicalMonitors.restype = ctypes.wintypes.BOOL

    class _PhysicalMonitor(ctypes.Structure):
        _fields_ = [
            ('hPhysicalMonitor', ctypes.wintypes.HANDLE),
            ('szPhysicalMonitorDescription', ctypes.c_wchar * 128),
        ]

    def _destroy_cached_monitors(self):
        if not self._cached_monitors:
            return
        array_type = (self._PhysicalMonitor * len(self._cached_monitors))
        arr = array_type()
        for idx, monitor in enumerate(self._cached_monitors):
            arr[idx].hPhysicalMonitor = monitor.handle
        self._dxva2.DestroyPhysicalMonitors(len(self._cached_monitors), arr)
        self._cached_monitors = []

    def _collect_monitors(self):
        monitors = []
        callback_type = ctypes.WINFUNCTYPE(
            ctypes.wintypes.BOOL,
            ctypes.wintypes.HMONITOR,
            ctypes.wintypes.HDC,
            ctypes.POINTER(ctypes.wintypes.RECT),
            ctypes.wintypes.LPARAM,
        )

        def callback(hMonitor, hdcMonitor, lprcMonitor, dwData):
            count = ctypes.wintypes.DWORD()
            if not self._dxva2.GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, ctypes.byref(count)):
                return True
            if count.value == 0:
                return True

            monitors_array = (self._PhysicalMonitor * count.value)()
            if not self._dxva2.GetPhysicalMonitorsFromHMONITOR(hMonitor, count, monitors_array):
                return True

            for i in range(count.value):
                ph = monitors_array[i]
                name = ph.szPhysicalMonitorDescription
                min_brightness = ctypes.wintypes.DWORD()
                cur_brightness = ctypes.wintypes.DWORD()
                max_brightness = ctypes.wintypes.DWORD()
                if not self._dxva2.GetMonitorBrightness(ph.hPhysicalMonitor, ctypes.byref(min_brightness), ctypes.byref(cur_brightness), ctypes.byref(max_brightness)):
                    continue
                monitors.append(MonitorInfo(
                    ph.hPhysicalMonitor,
                    name,
                    min_brightness.value,
                    cur_brightness.value,
                    max_brightness.value,
                ))
            return True

        self._user32.EnumDisplayMonitors(None, None, callback_type(callback), 0)
        return monitors

    def _refresh_cached_brightness(self):
        valid = []
        for monitor in self._cached_monitors:
            min_brightness = ctypes.wintypes.DWORD()
            cur_brightness = ctypes.wintypes.DWORD()
            max_brightness = ctypes.wintypes.DWORD()
            if not self._dxva2.GetMonitorBrightness(monitor.handle, ctypes.byref(min_brightness), ctypes.byref(cur_brightness), ctypes.byref(max_brightness)):
                continue
            monitor.min_brightness = min_brightness.value
            monitor.cur_brightness = cur_brightness.value
            monitor.max_brightness = max_brightness.value
            valid.append(monitor)
        self._cached_monitors = valid

    def enumerate_monitors(self, refresh=False):
        if refresh or not self._cached_monitors:
            self._destroy_cached_monitors()
            self._cached_monitors = self._collect_monitors()
        else:
            self._refresh_cached_brightness()
            if not self._cached_monitors:
                self._cached_monitors = self._collect_monitors()
        return list(self._cached_monitors)

    def refresh_monitors(self):
        self._destroy_cached_monitors()
        self._cached_monitors = self._collect_monitors()
        return list(self._cached_monitors)

    def set_brightness(self, handle, brightness):
        return self._dxva2.SetMonitorBrightness(handle, ctypes.wintypes.DWORD(brightness)) != 0

    def destroy_monitors(self, monitors=None):
        # Keep compatibility for callers; cached monitors are closed via cleanup.
        if monitors is None:
            self._destroy_cached_monitors()
            return
        if not monitors:
            return
        array_type = (self._PhysicalMonitor * len(monitors))
        arr = array_type()
        for idx, monitor in enumerate(monitors):
            arr[idx].hPhysicalMonitor = monitor.handle
        self._dxva2.DestroyPhysicalMonitors(len(monitors), arr)
