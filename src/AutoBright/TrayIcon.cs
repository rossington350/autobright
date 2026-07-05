using System;
using System.IO;
using System.Runtime.InteropServices;

namespace AutoBright;

/// <summary>
/// A Windows notification-area (system tray) icon implemented directly on the
/// Win32 Shell_NotifyIcon API, with the host window subclassed to receive the
/// icon's mouse messages. This mirrors the original application's hand-rolled
/// tray implementation and avoids any third-party dependency.
///
/// The callback is invoked with "restore" (double-click or menu Restore) or
/// "quit" (menu Exit).
/// </summary>
public sealed class TrayIcon
{
    private const uint WM_USER = 0x0400;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint CALLBACK_MESSAGE = WM_USER + 1;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint MF_STRING = 0x0000;
    private const int IDI_APPLICATION = 32512;
    private const int IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x00000010;
    private const uint LR_DEFAULTSIZE = 0x00000040;
    private const int CMD_RESTORE = 1001;
    private const int CMD_EXIT = 1002;

    private readonly IntPtr _hwnd;
    private readonly Action<string> _callback;
    private readonly SUBCLASSPROC _subclassProc;
    private readonly UIntPtr _subclassId = (UIntPtr)1;
    private readonly bool _ownsIcon;
    private NOTIFYICONDATAW _data;
    private bool _added;

    public TrayIcon(IntPtr hwnd, string tooltip, Action<string> callback)
    {
        _hwnd = hwnd;
        _callback = callback;
        _subclassProc = SubclassProc;
        SetWindowSubclass(_hwnd, _subclassProc, _subclassId, UIntPtr.Zero);

        (IntPtr icon, bool owned) = LoadAppIcon();
        _ownsIcon = owned;

        _data = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = CALLBACK_MESSAGE,
            hIcon = icon,
            szTip = tooltip,
        };
    }

    public void Show()
    {
        if (!_added)
        {
            Shell_NotifyIconW(NIM_ADD, ref _data);
            _added = true;
        }
    }

    public void Hide()
    {
        if (_added)
        {
            Shell_NotifyIconW(NIM_DELETE, ref _data);
            _added = false;
        }
    }

    public void Cleanup()
    {
        try { Hide(); } catch { }
        RemoveWindowSubclass(_hwnd, _subclassProc, _subclassId);
        // Only destroy an icon we loaded ourselves from icon.ico — the
        // IDI_APPLICATION fallback is a shared system resource that must not be.
        if (_ownsIcon && _data.hIcon != IntPtr.Zero)
        {
            try { DestroyIcon(_data.hIcon); } catch { }
        }
    }

    private IntPtr SubclassProc(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr refData)
    {
        if (msg == CALLBACK_MESSAGE)
        {
            int evt = (int)((long)lParam & 0xFFFF);
            if (evt == WM_LBUTTONDBLCLK)
            {
                _callback("restore");
            }
            else if (evt == WM_RBUTTONUP)
            {
                ShowMenu();
            }
        }
        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        IntPtr menu = CreatePopupMenu();
        AppendMenuW(menu, MF_STRING, (UIntPtr)CMD_RESTORE, "Restore");
        AppendMenuW(menu, MF_STRING, (UIntPtr)CMD_EXIT, "Exit");
        GetCursorPos(out POINT pt);
        // Required so the menu dismisses correctly when focus is elsewhere.
        SetForegroundWindow(_hwnd);
        int cmd = TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD, pt.x, pt.y, 0, _hwnd, IntPtr.Zero);
        DestroyMenu(menu);
        if (cmd == CMD_RESTORE)
        {
            _callback("restore");
        }
        else if (cmd == CMD_EXIT)
        {
            _callback("quit");
        }
    }

    /// <summary>
    /// Loads the tray icon, indicating whether the returned handle is one we
    /// privately own (loaded from icon.ico, must be released with DestroyIcon)
    /// or the shared IDI_APPLICATION system icon (must NOT be destroyed).
    /// </summary>
    private static (IntPtr Handle, bool Owned) LoadAppIcon()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "icon.ico");
            if (File.Exists(path))
            {
                IntPtr handle = LoadImageW(IntPtr.Zero, path, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
                if (handle != IntPtr.Zero)
                {
                    return (handle, true);
                }
            }
        }
        catch
        {
            // Fall through to the default application icon.
        }
        return (LoadIconW(IntPtr.Zero, (IntPtr)IDI_APPLICATION), false);
    }

    // ---- Win32 interop ----------------------------------------------------

    private delegate IntPtr SUBCLASSPROC(IntPtr hWnd, uint uMsg, UIntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, UIntPtr uIdSubclass, UIntPtr dwRefData);

    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, UIntPtr uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImageW(IntPtr hinst, string lpszName, int uType, int cxDesired, int cyDesired, uint fuLoad);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
