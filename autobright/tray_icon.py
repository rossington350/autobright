import ctypes
import ctypes.wintypes
import sys
from pathlib import Path

IDI_APPLICATION = 32512
WM_USER = 0x0400
WM_LBUTTONDBLCLK = 0x0203
WM_RBUTTONUP = 0x0205
GWL_WNDPROC = -4
TPM_RIGHTBUTTON = 0x0002
TPM_RETURNCMD = 0x0100
MF_STRING = 0x0000


class TrayIcon:
    class NOTIFYICONDATAW(ctypes.Structure):
        _fields_ = [
            ("cbSize", ctypes.wintypes.DWORD),
            ("hWnd", ctypes.wintypes.HWND),
            ("uID", ctypes.wintypes.UINT),
            ("uFlags", ctypes.wintypes.UINT),
            ("uCallbackMessage", ctypes.wintypes.UINT),
            ("hIcon", ctypes.wintypes.HICON),
            ("szTip", ctypes.c_wchar * 128),
            ("dwState", ctypes.wintypes.DWORD),
            ("dwStateMask", ctypes.wintypes.DWORD),
            ("szInfo", ctypes.c_wchar * 256),
            ("uTimeoutOrVersion", ctypes.wintypes.UINT),
            ("szInfoTitle", ctypes.c_wchar * 64),
            ("dwInfoFlags", ctypes.wintypes.DWORD),
            ("guidItem", ctypes.c_byte * 16),
            ("hBalloonIcon", ctypes.wintypes.HICON),
        ]

    def __init__(self, root, tooltip, callback):
        self.root = root
        self.tooltip = tooltip
        self.callback = callback
        self.hwnd = ctypes.wintypes.HWND(self.root.winfo_id())
        self.hicon = None
        self._wndproc_type = ctypes.WINFUNCTYPE(
            ctypes.c_long,
            ctypes.wintypes.HWND,
            ctypes.wintypes.UINT,
            ctypes.wintypes.WPARAM,
            ctypes.wintypes.LPARAM,
        )
        self._window_proc = self._wndproc_type(self._wndproc)
        user32 = ctypes.windll.user32
        user32.GetWindowLongPtrW.argtypes = [ctypes.wintypes.HWND, ctypes.c_int]
        user32.GetWindowLongPtrW.restype = ctypes.c_void_p
        user32.SetWindowLongPtrW.argtypes = [ctypes.wintypes.HWND, ctypes.c_int, ctypes.c_void_p]
        user32.SetWindowLongPtrW.restype = ctypes.c_void_p
        user32.CallWindowProcW.argtypes = [ctypes.c_void_p, ctypes.wintypes.HWND, ctypes.wintypes.UINT, ctypes.wintypes.WPARAM, ctypes.wintypes.LPARAM]
        user32.CallWindowProcW.restype = ctypes.c_long
        self.old_wndproc = user32.GetWindowLongPtrW(self.hwnd, GWL_WNDPROC)
        user32.SetWindowLongPtrW(self.hwnd, GWL_WNDPROC, ctypes.cast(self._window_proc, ctypes.c_void_p))
        self._data = self.NOTIFYICONDATAW()
        self._data.cbSize = ctypes.sizeof(self._data)
        self._data.hWnd = self.hwnd
        self._data.uID = 1
        self._data.uFlags = 0x00000007
        self._data.uCallbackMessage = WM_USER + 1
        self._data.hIcon = self._load_icon()
        self._data.szTip = self.tooltip

    def _load_icon(self):
        try:
            if getattr(sys, '_MEIPASS', None):
                bundle_path = Path(sys._MEIPASS)
            else:
                bundle_path = None
            project_root = Path(__file__).resolve().parents[1]
            script_path = Path(sys.argv[0]).resolve() if sys.argv and sys.argv[0] else None
            candidates = [
                bundle_path / 'icon.ico' if bundle_path is not None else None,
                script_path.with_suffix('.ico') if script_path is not None else None,
                script_path.parent / 'icon.ico' if script_path is not None else None,
                project_root / 'icon.ico',
                Path.cwd() / 'icon.ico',
            ]
            for path in candidates:
                if path is None:
                    continue
                if path.exists():
                    icon_handle = ctypes.windll.user32.LoadImageW(None, str(path), 1, 0, 0, 0x00000010)
                    if icon_handle:
                        return icon_handle
        except Exception:
            pass
        return ctypes.windll.user32.LoadIconW(None, ctypes.c_wchar_p(IDI_APPLICATION))

    def show(self):
        ctypes.windll.shell32.Shell_NotifyIconW(0x00000000, ctypes.byref(self._data))

    def hide(self):
        ctypes.windll.shell32.Shell_NotifyIconW(0x00000002, ctypes.byref(self._data))

    def cleanup(self):
        try:
            self.hide()
        except Exception:
            pass
        if self.old_wndproc:
            ctypes.windll.user32.SetWindowLongPtrW(self.hwnd, GWL_WNDPROC, self.old_wndproc)

    def _wndproc(self, hwnd, msg, wparam, lparam):
        if msg == self._data.uCallbackMessage:
            if lparam == WM_LBUTTONDBLCLK:
                self.callback('restore')
            elif lparam == WM_RBUTTONUP:
                self._show_menu()
        prev = self.old_wndproc
        if not isinstance(prev, ctypes.c_void_p):
            try:
                prev = ctypes.c_void_p(prev)
            except Exception:
                prev = ctypes.c_void_p(int(prev & ((1 << (ctypes.sizeof(ctypes.c_void_p) * 8)) - 1)))
        return ctypes.windll.user32.CallWindowProcW(prev, hwnd, msg, wparam, lparam)

    def _show_menu(self):
        menu = ctypes.windll.user32.CreatePopupMenu()
        ctypes.windll.user32.AppendMenuW(menu, MF_STRING, 1001, 'Restore')
        ctypes.windll.user32.AppendMenuW(menu, MF_STRING, 1002, 'Exit')
        pt = ctypes.wintypes.POINT()
        ctypes.windll.user32.GetCursorPos(ctypes.byref(pt))
        cmd = ctypes.windll.user32.TrackPopupMenu(
            menu,
            TPM_RIGHTBUTTON | TPM_RETURNCMD,
            pt.x,
            pt.y,
            0,
            self.hwnd,
            None,
        )
        ctypes.windll.user32.DestroyMenu(menu)
        if cmd == 1001:
            self.callback('restore')
        elif cmd == 1002:
            self.callback('quit')
