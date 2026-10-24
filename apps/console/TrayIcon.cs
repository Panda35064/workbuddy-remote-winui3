using System.Runtime.InteropServices;

namespace WorkBuddyRemote.WinUI;

/// <summary>
/// 托盘图标：Win32 Shell_NotifyIcon + 窗口子类化，不引入 H.NotifyIcon。
/// 挂在主窗口 HWND 上，窗口隐藏后图标仍然存在。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint WM_APP = 0x8000;
    private const uint WM_TRAY_CALLBACK = WM_APP + 1;
    private const uint NIM_ADD = 0;
    private const uint NIM_MODIFY = 1;
    private const uint NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 0x1;
    private const uint NIF_ICON = 0x2;
    private const uint NIF_TIP = 0x4;
    private const uint NIF_INFO = 0x10;
    private const uint NIIF_INFO = 0x1;
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x10;
    private const uint LR_DEFAULTSIZE = 0x40;

    private readonly IntPtr _hwnd;
    private readonly uint _id;
    private readonly SUBCLASSPROC _proc;
    private IntPtr _hIcon = IntPtr.Zero;
    private bool _added;

    public event Action? DoubleClick;

    public event Action<POINT>? RightClick;

    public TrayIcon(IntPtr hwnd, string iconPath, string tooltip, uint id = 1)
    {
        _hwnd = hwnd;
        _id = id;
        _proc = OnMessage;

        if (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath))
        {
            _hIcon = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
        }

        SetWindowSubclass(_hwnd, _proc, (UIntPtr)id, IntPtr.Zero);
        AddIcon(tooltip);
    }

    public bool IsAdded => _added;

    private void AddIcon(string tooltip)
    {
        var data = CreateData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        data.uCallbackMessage = WM_TRAY_CALLBACK;
        data.hIcon = _hIcon;
        data.szTip = Truncate(tooltip, 127);
        _added = Shell_NotifyIcon(NIM_ADD, ref data);
        if (_added)
        {
            // 不设置 NOTIFYICON_VERSION_4：保持 lParam 直接是鼠标消息，处理更简单可靠。
            Shell_NotifyIcon(NIM_MODIFY, ref data);
        }
    }

    public void UpdateTooltip(string tooltip)
    {
        if (!_added) return;
        var data = CreateData();
        data.uFlags = NIF_TIP;
        data.szTip = Truncate(tooltip, 127);
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    /// <summary>第一次收进托盘时的提示气泡，只弹一次。</summary>
    public void ShowBalloon(string title, string message)
    {
        if (!_added) return;
        var data = CreateData();
        data.uFlags = NIF_INFO;
        data.szInfoTitle = Truncate(title, 63);
        data.szInfo = Truncate(message, 255);
        data.dwInfoFlags = NIIF_INFO;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private NOTIFYICONDATAW CreateData() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = _id,
        szTip = "",
        szInfo = "",
        szInfoTitle = "",
    };

    private IntPtr OnMessage(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, IntPtr dwRefData)
    {
        if (uMsg == WM_TRAY_CALLBACK)
        {
            switch ((uint)lParam)
            {
                case 0x0202: // WM_LBUTTONUP
                case 0x0203: // WM_LBUTTONDBLCLK
                    DoubleClick?.Invoke();
                    return IntPtr.Zero;
                case 0x0205: // WM_RBUTTONUP
                case 0x007B: // WM_CONTEXTMENU
                    GetCursorPos(out var pt);
                    RightClick?.Invoke(pt);
                    return IntPtr.Zero;
            }
        }

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = CreateData();
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
        }

        try
        {
            RemoveWindowSubclass(_hwnd, _proc, (UIntPtr)_id);
        }
        catch
        {
        }

        if (_hIcon != IntPtr.Zero)
        {
            DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }
    }

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Length <= max ? value : value[..max];
    }

    // ---------- P/Invoke ----------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    private delegate IntPtr SUBCLASSPROC(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATAW data);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SUBCLASSPROC callback, UIntPtr id, IntPtr refData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SUBCLASSPROC callback, UIntPtr id);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int cx, int cy, uint load);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);
}
