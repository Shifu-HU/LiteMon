using System;
using System.Runtime.InteropServices;

namespace LiteMon.Core.Interop;

/// <summary>Win32 原生 API：内存状态、窗口样式、DPI、系统空闲时间。</summary>
public static class NativeMethods
{
    // ---------------- 内存 ----------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    public static MEMORYSTATUSEX GetMemoryStatus()
    {
        var s = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref s);
        return s;
    }

    // ---------------- 窗口样式（悬浮窗置顶 / 点击穿透） ----------------

    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32", SetLastError = true)]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32", SetLastError = true)]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32")]
    public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32")]
    public static extern bool SetForegroundWindow(IntPtr hwnd);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    public static bool IsWindowTopmost(IntPtr h) => (GetWindowLong(h, GWL_EXSTYLE) & 0x8 /*WS_EX_TOPMOST*/) != 0;

    /// <summary>切换“点击穿透”（WS_EX_TRANSPARENT）。</summary>
    public static void SetClickThrough(IntPtr h, bool enable)
    {
        var ex = GetWindowLong(h, GWL_EXSTYLE);
        var want = enable ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
        if (want != ex) SetWindowLong(h, GWL_EXSTYLE, want);
    }

    // ---------------- 高级电源/系统信息 ----------------

    [DllImport("kernel32")]
    public static extern void GetSystemInfo(out SYSTEM_INFO info);

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_INFO
    {
        public uint dwOemId;
        public uint dwPageSize;
        public IntPtr lpMinimumApplicationAddress;
        public IntPtr lpMaximumApplicationAddress;
        public IntPtr dwActiveProcessorMask;
        public uint dwNumberOfProcessors;
        public uint dwProcessorType;
        public uint dwAllocationGranularity;
        public ushort wProcessorLevel;
        public ushort wProcessorRevision;
    }

    public static int LogicalProcessorCount
    {
        get
        {
            GetSystemInfo(out var si);
            return (int)si.dwNumberOfProcessors;
        }
    }

    // ---------------- 系统电源状态（判断是否待机，用于暂停采集） ----------------
    public const int PBT_APMSUSPEND = 4, PBT_APMRESUMEAUTOMATIC = 18;

    // ---------------- 防休眠（24h 稳定性：其实不需要，闲置即可） ----------------

    // ---------------- 开机自启动注册表 ----------------

    public static bool GetAutoStart(string appName)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: false);
            return key?.GetValue(appName) is string v && !string.IsNullOrEmpty(v);
        }
        catch { return false; }
    }

    public static void SetAutoStart(string appName, string exePath, bool enable)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (enable) key?.SetValue(appName, "\"" + exePath + "\"");
            else key?.DeleteValue(appName, throwOnMissingValue: false);
        }
        catch { }
    }

    // ---------------- 管理员判断 ----------------

    public static bool IsElevated
    {
        get
        {
            try
            {
                using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                var p = new System.Security.Principal.WindowsPrincipal(id);
                return p.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }
}
