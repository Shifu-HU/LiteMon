using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using LiteMon.Wpf.Services;

namespace LiteMon.Wpf.Services;

/// <summary>
/// 托盘图标：Shell_NotifyIcon P/Invoke（无第三方依赖）。
/// 图标运行时绘制（主题感知）；左键单击显示主窗口，右键弹出菜单。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_INFO = 0x10;
    private const int WM_TRAY = 0x8000 + 1;      // 自定义回调
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
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
    }

    [DllImport("shell32", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIconW(uint msg, ref NOTIFYICONDATA data);
    [DllImport("user32")] private static extern bool DestroyIcon(IntPtr hIcon);

    private HwndSource? _source;
    private IntPtr _hwnd;
    private bool _added;
    private IntPtr _icon;
    private readonly Action _onLeftClick;
    private ContextMenu? _menu;

    public TrayIcon(Action onLeftClick, System.Windows.Threading.Dispatcher uiDispatcher)
    {
        _onLeftClick = onLeftClick;
        _uiDispatcher = uiDispatcher;
        CreateHiddenWindow();
        UpdateIcon("LiteMon");
    }

    private readonly System.Windows.Threading.Dispatcher _uiDispatcher;

    private void CreateHiddenWindow()
    {
        // 只收消息的隐藏消息窗口（HwndSource，不用真 WPF 窗口）
        var parameters = new HwndSourceParameters("LiteMon.Tray")
        {
            Width = 0,
            Height = 0,
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP
        };
        _source = new HwndSource(parameters);
        _hwnd = _source.Handle;
        _source.AddHook(WndProc);
    }

    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
    private readonly uint _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // explorer 重启/睡眠唤醒后托盘区重建：重新注册图标，否则图标永久消失
        if ((uint)msg == _taskbarCreated && _added)
        {
            try
            {
                var d0 = new NOTIFYICONDATA { hWnd = _hwnd, uID = 1 };
                Shell_NotifyIconW(NIM_DELETE, ref d0);
            }
            catch { }
            _added = false;
            UpdateIcon(_lastTooltip ?? "LiteMon");
            handled = true;
            return IntPtr.Zero;
        }
        if (msg != WM_TRAY) return IntPtr.Zero;
        var cmd = lParam.ToInt64() & 0xFFFF;
        if (cmd == WM_LBUTTONUP || cmd == WM_LBUTTONDBLCLK)
        {
            _onLeftClick();
            handled = true;
        }
        else if (cmd == WM_RBUTTONUP || cmd == WM_CONTEXTMENU)
        {
            ShowMenu();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private string? _lastTooltip;

    /// <summary>更新托盘图标与悬浮提示。</summary>
    public void UpdateIcon(string tooltip)
    {
        _lastTooltip = tooltip;
        var old = _icon;
        _icon = DrawIcon();
        var d = new NOTIFYICONDATA
        {
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_ICON | NIF_MESSAGE | NIF_TIP,
            uCallbackMessage = (uint)WM_TRAY,
            hIcon = _icon,
            szTip = tooltip.Length > 127 ? tooltip[..127] : tooltip,
        };
        if (!_added) { Shell_NotifyIconW(NIM_ADD, ref d); _added = true; }
        else Shell_NotifyIconW(NIM_MODIFY, ref d);
        if (old != IntPtr.Zero) DestroyIcon(old);
    }

    /// <summary>通知气泡。</summary>
    public void Notify(string title, string text)
    {
        if (!_added) return;
        var d = new NOTIFYICONDATA
        {
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_INFO,
            szInfo = text,
            szInfoTitle = title,
            dwInfoFlags = 1, // NIIF_INFO
            uTimeoutOrVersion = 10000,
        };
        Shell_NotifyIconW(NIM_MODIFY, ref d);
    }

    /// <summary>设置右键菜单（WPF ContextMenu）。</summary>
    public void SetMenu(ContextMenu menu) => _menu = menu;

    private void ShowMenu()
    {
        if (_menu == null) return;
        _uiDispatcher.BeginInvoke(() =>
        {
            try
            {
                NativeTray.SetForegroundWindow(_hwnd);
                _menu.PlacementTarget = null;
                _menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
                _menu.IsOpen = true;
            }
            catch { }
        });
    }

    /// <summary>绘制 16x16/32x32 矢量图标：圆角方块 + 折线脉冲。</summary>
    private IntPtr DrawIcon()
    {
        try
        {
            var size = 32;
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                var accent = (Color)Application.Current.Resources.FindCompatibleColor("Brush.Accent");
                var card = (Color)Application.Current.Resources.FindCompatibleColor("Brush.Card");
                var text = (Color)Application.Current.Resources.FindCompatibleColor("Brush.Text");
                var bg = new SolidColorBrush(Colors.Transparent);
                dc.DrawRectangle(bg, null, new Rect(0, 0, size, size));
                dc.DrawRoundedRectangle(new SolidColorBrush(card), new Pen(new SolidColorBrush(accent), 1.5),
                    new Rect(1, 1, size - 2, size - 2), 7, 7);
                var pen = new Pen(new SolidColorBrush(accent), 2.4);
                pen.StartLineCap = PenLineCap.Round;
                pen.EndLineCap = PenLineCap.Round;
                pen.LineJoin = PenLineJoin.Round;
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    ctx.BeginFigure(new Point(7, 20), false, false);
                    ctx.LineTo(new Point(12, 13), true, false);
                    ctx.LineTo(new Point(15, 17), true, false);
                    ctx.LineTo(new Point(19, 9), true, false);
                    ctx.LineTo(new Point(25, 16), true, false);
                }
                geo.Freeze();
                dc.DrawGeometry(null, pen, geo);
                var dot = new SolidColorBrush(text);
                dc.DrawEllipse(dot, null, new Point(25, 9), 2.2, 2.2);
            }
            var rt = new System.Windows.Media.Imaging.RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            rt.Render(dv);
            rt.Freeze();
            return rt.ToHicon();
        }
        catch { return IntPtr.Zero; }
    }

    public void Dispose()
    {
        try
        {
            if (_added)
            {
                var d = new NOTIFYICONDATA { hWnd = _hwnd, uID = 1 };
                Shell_NotifyIconW(NIM_DELETE, ref d);
            }
            if (_icon != IntPtr.Zero) DestroyIcon(_icon);
            _source?.RemoveHook(WndProc);
            _source?.Dispose();
        }
        catch { }
    }
}

internal static class NativeTray
{
    [DllImport("user32")] public static extern bool SetForegroundWindow(IntPtr h);
}

internal static class TrayIconExtensions
{
    /// <summary>BitmapSource → HICON。</summary>
    public static IntPtr ToHicon(this System.Windows.Media.Imaging.BitmapSource src)
    {
        try
        {
            int w = src.PixelWidth, h = src.PixelHeight;
            var bytes = new byte[w * h * 4];
            src.CopyPixels(Int32RectHelper(w, h), bytes, w * 4, 0);
            return CreateHiconFromRgba(bytes, w, h);
        }
        catch { return IntPtr.Zero; }
    }

    private static System.Windows.Int32Rect Int32RectHelper(int w, int h) => new(0, 0, w, h);

    private static IntPtr CreateHiconFromRgba(byte[] rgba, int w, int h)
    {
        // BGRA 掩码 + AND 掩码
        var xor = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            byte r = rgba[i * 4], g = rgba[i * 4 + 1], b = rgba[i * 4 + 2], a = rgba[i * 4 + 3];
            xor[i * 4] = b; xor[i * 4 + 1] = g; xor[i * 4 + 2] = r; xor[i * 4 + 3] = a;
        }
        var and = new byte[((w + 31) / 32) * 4 * h];

        var icon = new ICONINFO { fIcon = true, xHotspot = 0, yHotspot = 0 };
        icon.hbmColor = CreateBitmap(w, h, 1, 32, xor);
        icon.hbmMask = CreateBitmap(w, h, 1, 1, and);
        var hIcon = CreateIconIndirect(ref icon);
        return hIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot, yHotspot;
        public IntPtr hbmMask, hbmColor;
    }

    [DllImport("user32")] private static extern IntPtr CreateIconIndirect(ref ICONINFO info);
    [DllImport("gdi32")] private static extern IntPtr CreateBitmap(int w, int h, int planes, int bpp, byte[]? data);

    /// <summary>从资源键取颜色（支持 SolidColorBrush 与 Color）。</summary>
    public static Color FindCompatibleColor(this System.Windows.ResourceDictionary res, string key)
    {
        try
        {
            if (res[key] is SolidColorBrush b) return b.Color;
            if (res[key] is Color c) return c;
        }
        catch { }
        return Colors.SteelBlue;
    }
}
