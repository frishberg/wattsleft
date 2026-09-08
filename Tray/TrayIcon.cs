using System.Runtime.InteropServices;

namespace BatteryChecker.Tray;

/// <summary>
/// A system-tray icon that shows text (the percent) and a right-click menu.
///
/// Plain Win32: a hidden message-only window receives the tray messages on the
/// UI thread, because WinUI's message loop pumps for every window on it.
/// </summary>
public sealed unsafe partial class TrayIcon : IDisposable
{
    public event Action? LeftClick;
    public event Action<int>? MenuPicked;   // index into the menu items you passed in

    private readonly (string Label, bool Checkable)[] _menu;
    private readonly Dictionary<int, bool> _checked = new();
    private readonly nint _hwnd;
    private readonly uint _taskbarCreated;
    private nint _icon;
    private bool _added;
    private string _text = "";
    private string _tip = "";
    private uint _color = 0x00FFFFFF;

    private static TrayIcon? _instance;   // one tray icon per app; the WNDPROC is static

    private const uint WM_APP = 0x8000;
    private const uint WM_TRAY = WM_APP + 1;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_CONTEXTMENU = 0x007B;
    private const uint WM_DPICHANGED = 0x02E0;
    private const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4;
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const uint MF_STRING = 0x0, MF_SEPARATOR = 0x800, MF_CHECKED = 0x8;
    private const uint TPM_RIGHTBUTTON = 0x2, TPM_RETURNCMD = 0x100;
    private const int SM_CXSMICON = 49;

    public TrayIcon((string Label, bool Checkable)[] menu)
    {
        _menu = menu;
        _instance = this;
        _hwnd = CreateMessageWindow();
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
    }

    /// <summary>Show `text` as the icon (2-3 characters look right) with a hover tooltip.</summary>
    public void Set(string text, string tip, uint colorBgr = 0x00FFFFFF)
    {
        if (_added && text == _text && tip == _tip && colorBgr == _color)
            return;   // nothing changed; don't redraw the icon four times a second
        _text = text;
        _tip = tip;
        _color = colorBgr;
        Refresh(NIM_MODIFY);
    }

    public void SetChecked(int menuIndex, bool value) => _checked[menuIndex] = value;

    private void Refresh(uint message)
    {
        nint fresh = RenderTextIcon(_text, _color);
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAY,
            hIcon = fresh,
        };
        var tip = _tip.Length > 127 ? _tip[..127] : _tip;
        fixed (char* src = tip)
            Buffer.MemoryCopy(src, data.szTip, 128 * 2, tip.Length * 2);

        if (!_added || message == NIM_ADD)
        {
            _added = Shell_NotifyIconW(NIM_ADD, &data);
        }
        else
        {
            Shell_NotifyIconW(NIM_MODIFY, &data);
        }
        if (_icon != 0)
            DestroyIcon(_icon);
        _icon = fresh;
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = new NOTIFYICONDATAW { cbSize = (uint)sizeof(NOTIFYICONDATAW), hWnd = _hwnd, uID = 1 };
            Shell_NotifyIconW(NIM_DELETE, &data);
            _added = false;
        }
        if (_icon != 0)
            DestroyIcon(_icon);
        DestroyWindow(_hwnd);
    }

    // ------------------------------------------------------------ messages

    private nint CreateMessageWindow()
    {
        var className = "WattsLeftTray";
        fixed (char* name = className)
        {
            var wc = new WNDCLASSW { lpfnWndProc = &WndProc, hInstance = GetModuleHandleW(null), lpszClassName = name };
            RegisterClassW(&wc);
            const nint HWND_MESSAGE = -3;
            return CreateWindowExW(0, name, name, 0, 0, 0, 0, 0, HWND_MESSAGE, 0, wc.hInstance, 0);
        }
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        var self = _instance;
        if (self is not null)
        {
            if (msg == WM_TRAY)
            {
                uint mouse = (uint)(lParam & 0xFFFF);
                if (mouse == WM_LBUTTONUP)
                    self.LeftClick?.Invoke();
                else if (mouse is WM_RBUTTONUP or WM_CONTEXTMENU)
                    self.ShowMenu();
                return 0;
            }
            if (msg == self._taskbarCreated || msg == WM_DPICHANGED)
            {
                self.Refresh(NIM_ADD);   // Explorer restarted or scale changed: put the icon back, redrawn
                return 0;
            }
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        nint menu = CreatePopupMenu();
        for (int i = 0; i < _menu.Length; i++)
        {
            var (label, checkable) = _menu[i];
            if (label == "-")
            {
                AppendMenuW(menu, MF_SEPARATOR, 0, null);
                continue;
            }
            uint flags = MF_STRING;
            if (checkable && _checked.GetValueOrDefault(i))
                flags |= MF_CHECKED;
            AppendMenuW(menu, flags, (nuint)(i + 1), label);
        }
        GetCursorPos(out var point);
        SetForegroundWindow(_hwnd);   // without this the menu doesn't close when you click away
        int choice = TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD, point.X, point.Y, 0, _hwnd, 0);
        PostMessageW(_hwnd, 0, 0, 0);
        DestroyMenu(menu);
        if (choice > 0)
            MenuPicked?.Invoke(choice - 1);
    }

    // ------------------------------------------------------------ drawing the icons

    /// <summary>
    /// The app mark: a ring, bright where charge is left, faint where it's gone.
    /// Drawn pixel by pixel with soft edges so it stays crisp at 16 px.
    /// </summary>
    public static nint RenderRing(int size, double fraction, uint colorBgr = 0x00FFFFFF)
    {
        var header = new BITMAPINFOHEADER { biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = size, biHeight = -size, biPlanes = 1, biBitCount = 32 };
        nint screen = GetDC(0);
        void* bits;
        nint bitmap = CreateDIBSection(screen, &header, 0, &bits, 0, 0);
        ReleaseDC(0, screen);

        byte b = (byte)(colorBgr & 0xFF), g = (byte)((colorBgr >> 8) & 0xFF), r = (byte)((colorBgr >> 16) & 0xFF);
        double c = size / 2.0, outer = size * 0.47, inner = size * 0.26;
        double sweep = Math.Clamp(fraction, 0, 1) * Math.Tau;
        byte* px = (byte*)bits;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            double dx = x + 0.5 - c, dy = y + 0.5 - c, d = Math.Sqrt(dx * dx + dy * dy);
            double band = Math.Clamp(Math.Min(d - inner, outer - d) + 0.5, 0, 1);   // 1 inside the ring, soft at both edges
            if (band <= 0) continue;
            double angle = Math.Atan2(dx, -dy);                                         // 0 at 12 o'clock, clockwise
            if (angle < 0) angle += Math.Tau;
            double lit = angle <= sweep ? 1 : 0.28;                                     // faint track past the level
            double a = band * lit;
            byte alpha = (byte)(a * 255);
            int i = (y * size + x) * 4;
            px[i] = (byte)(b * alpha / 255); px[i + 1] = (byte)(g * alpha / 255); px[i + 2] = (byte)(r * alpha / 255); px[i + 3] = alpha;
        }

        nint mask = CreateBitmap(size, size, 1, 1, null);
        var info = new ICONINFO { fIcon = 1, hbmMask = mask, hbmColor = bitmap };
        nint icon = CreateIconIndirect(&info);
        DeleteObject(mask);
        DeleteObject(bitmap);
        return icon;
    }

    public static void Destroy(nint icon) => DestroyIcon(icon);

    /// <summary>Draw text onto a transparent 32-bit bitmap and turn it into an HICON.</summary>
    private static nint RenderTextIcon(string text, uint colorBgr)
    {
        int size = GetSystemMetrics(SM_CXSMICON);   // 16 at 100 %, 32 at 200 %

        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(BITMAPINFOHEADER),
            biWidth = size,
            biHeight = -size,
            biPlanes = 1,
            biBitCount = 32,
        };
        nint screen = GetDC(0);
        nint dc = CreateCompatibleDC(screen);
        void* bits;
        nint bitmap = CreateDIBSection(screen, &header, 0, &bits, 0, 0);
        ReleaseDC(0, screen);
        nint oldBitmap = SelectObject(dc, bitmap);

        int height = (int)(size * (text.Length <= 2 ? 1.15 : 0.9));
        nint font = CreateFontW(-height, 0, 0, 0, 700, 0, 0, 0, 0, 0, 0, 4 /* ANTIALIASED */, 0, "Segoe UI");
        nint oldFont = SelectObject(dc, font);
        SetBkMode(dc, 1 /* TRANSPARENT */);
        SetTextColor(dc, 0x00FFFFFF);
        var rect = new RECT { Right = size, Bottom = size };
        DrawTextW(dc, text, -1, &rect, 0x1 | 0x4 | 0x20 | 0x100 /* CENTER|VCENTER|SINGLELINE|NOCLIP */);

        // GDI leaves alpha at 0. Brightness of the white text becomes alpha, tinted with the colour.
        byte* px = (byte*)bits;
        byte r = (byte)(colorBgr & 0xFF), g = (byte)((colorBgr >> 8) & 0xFF), b = (byte)((colorBgr >> 16) & 0xFF);   // COLORREF is 0x00BBGGRR
        for (int i = 0; i < size * size * 4; i += 4)
        {
            byte alpha = px[i];
            px[i] = (byte)(b * alpha / 255);
            px[i + 1] = (byte)(g * alpha / 255);
            px[i + 2] = (byte)(r * alpha / 255);
            px[i + 3] = alpha;
        }

        SelectObject(dc, oldFont);
        SelectObject(dc, oldBitmap);
        DeleteObject(font);
        DeleteDC(dc);

        nint mask = CreateBitmap(size, size, 1, 1, null);
        var info = new ICONINFO { fIcon = 1, hbmMask = mask, hbmColor = bitmap };
        nint icon = CreateIconIndirect(&info);
        DeleteObject(mask);
        DeleteObject(bitmap);
        return icon;
    }

    // ------------------------------------------------------------ win32

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSW
    {
        public uint style;
        public delegate* unmanaged<nint, uint, nuint, nint, nint> lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public char* lpszMenuName, lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public nint hIcon;
        public fixed char szTip[128];
        public uint dwState, dwStateMask;
        public fixed char szInfo[256];
        public uint uVersion;
        public fixed char szInfoTitle[64];
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO { public int fIcon; public uint xHotspot, yHotspot; public nint hbmMask, hbmColor; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial ushort RegisterClassW(WNDCLASSW* wc);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial nint CreateWindowExW(uint exStyle, char* className, char* title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DestroyWindow(nint hwnd);
    [LibraryImport("user32.dll")] private static partial nint DefWindowProcW(nint hwnd, uint msg, nuint wParam, nint lParam);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial uint RegisterWindowMessageW(string name);
    [LibraryImport("user32.dll")] private static partial nint CreatePopupMenu();
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool AppendMenuW(nint menu, uint flags, nuint id, string? text);
    [LibraryImport("user32.dll")] private static partial int TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DestroyMenu(nint menu);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetCursorPos(out POINT point);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetForegroundWindow(nint hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool PostMessageW(nint hwnd, uint msg, nuint wParam, nint lParam);
    [LibraryImport("user32.dll")] private static partial int GetSystemMetrics(int index);
    [LibraryImport("user32.dll")] private static partial nint GetDC(nint hwnd);
    [LibraryImport("user32.dll")] private static partial int ReleaseDC(nint hwnd, nint dc);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int DrawTextW(nint dc, string text, int count, RECT* rect, uint format);
    [LibraryImport("user32.dll")] private static partial nint CreateIconIndirect(ICONINFO* info);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DestroyIcon(nint icon);
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial nint GetModuleHandleW(string? name);
    [LibraryImport("shell32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool Shell_NotifyIconW(uint message, NOTIFYICONDATAW* data);
    [LibraryImport("gdi32.dll")] private static partial nint CreateCompatibleDC(nint dc);
    [LibraryImport("gdi32.dll")] private static partial nint CreateDIBSection(nint dc, BITMAPINFOHEADER* info, uint usage, void** bits, nint section, uint offset);
    [LibraryImport("gdi32.dll")] private static partial nint CreateBitmap(int w, int h, uint planes, uint bitCount, void* bits);
    [LibraryImport("gdi32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial nint CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);
    [LibraryImport("gdi32.dll")] private static partial nint SelectObject(nint dc, nint obj);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteObject(nint obj);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteDC(nint dc);
    [LibraryImport("gdi32.dll")] private static partial int SetBkMode(nint dc, int mode);
    [LibraryImport("gdi32.dll")] private static partial uint SetTextColor(nint dc, uint color);
}
