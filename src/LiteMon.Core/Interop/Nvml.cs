using System;
using System.Runtime.InteropServices;

namespace LiteMon.Core.Interop;

/// <summary>
/// NVIDIA NVML 最小封装（仅本工具需要的只读函数）。
/// 任何 NVML 调用失败都不会抛出——返回 null / false，由上层降级。
/// nvml.dll 由 NVIDIA 驱动安装到 System32；无 N 卡机器上 LoadLibrary 失败即视为不可用。
/// </summary>
internal static class Nvml
{
    private const string Dll = "nvml.dll";
    private static bool _resolved;

    /// <summary>NVML 是否可用（驱动已装、库能加载、nvmlInit 成功）。</summary>
    public static bool IsAvailable { get; private set; }

    // ---- 函数指针表：避免直接 DllImport 在缺 DLL 时抛 EntryPointNotFoundException ----
    private static class F
    {
        public delegate int NvmlInit();
        public delegate int NvmlShutdown();
        public delegate int NvmlDeviceGetCount(out uint count);
        public delegate int NvmlDeviceGetHandle(uint index, out IntPtr device);
        public delegate int NvmlDeviceGetName(IntPtr device, byte[] name, uint len);
        public delegate int NvmlDeviceGetUtil(IntPtr device, out Utilization util);
        public delegate int NvmlDeviceGetMem(IntPtr device, out MemoryInfo info);
        public delegate int NvmlDeviceGetMemV2(IntPtr device, int version, out MemoryInfoV2 info);
        public delegate int NvmlDeviceGetTemp(IntPtr device, int sensorType, out int temp);
        public delegate int NvmlDeviceGetPower(IntPtr device, out uint mW);
        public delegate int NvmlDeviceGetClock(IntPtr device, int clockType, out uint mhz);
        public delegate int NvmlDeviceGetFan(IntPtr device, out uint speed);

        public static readonly NvmlInit? Init;
        public static readonly NvmlShutdown? Shutdown;
        public static readonly NvmlDeviceGetCount? GetCount;
        public static readonly NvmlDeviceGetHandle? GetHandle;
        public static readonly NvmlDeviceGetName? GetName;
        public static readonly NvmlDeviceGetUtil? GetUtil;
        public static readonly NvmlDeviceGetMem? GetMem;
        public static readonly NvmlDeviceGetMemV2? GetMemV2;
        public static readonly NvmlDeviceGetTemp? GetTemp;
        public static readonly NvmlDeviceGetPower? GetPower;
        public static readonly NvmlDeviceGetClock? GetClock;
        public static readonly NvmlDeviceGetFan? GetFan;

        static F()
        {
            try
            {
                var h = LoadLibrary(Dll);
                if (h == IntPtr.Zero) return;
                Init = GetProc<NvmlInit>(h, "nvmlInit_v2");
                Shutdown = GetProc<NvmlShutdown>(h, "nvmlShutdown");
                GetCount = GetProc<NvmlDeviceGetCount>(h, "nvmlDeviceGetCount_v2") ?? GetProc<NvmlDeviceGetCount>(h, "nvmlDeviceGetCount");
                GetHandle = GetProc<NvmlDeviceGetHandle>(h, "nvmlDeviceGetHandleByIndex_v2") ?? GetProc<NvmlDeviceGetHandle>(h, "nvmlDeviceGetHandleByIndex");
                GetName = GetProc<NvmlDeviceGetName>(h, "nvmlDeviceGetName") ?? GetProc<NvmlDeviceGetName>(h, "nvmlDeviceGetName_v2");
                GetUtil = GetProc<NvmlDeviceGetUtil>(h, "nvmlDeviceGetUtilizationRates");
                GetMem = GetProc<NvmlDeviceGetMem>(h, "nvmlDeviceGetMemoryInfo");
                GetMemV2 = GetProc<NvmlDeviceGetMemV2>(h, "nvmlDeviceGetMemoryInfo_v2");
                GetTemp = GetProc<NvmlDeviceGetTemp>(h, "nvmlDeviceGetTemperature");
                GetPower = GetProc<NvmlDeviceGetPower>(h, "nvmlDeviceGetPowerUsage");
                GetClock = GetProc<NvmlDeviceGetClock>(h, "nvmlDeviceGetClockInfo");
                GetFan = GetProc<NvmlDeviceGetFan>(h, "nvmlDeviceGetFanSpeed");
            }
            catch { /* 任何异常都视为不可用 */ }
        }

        private static T? GetProc<T>(IntPtr h, string name) where T : Delegate
        {
            var p = GetProcAddress(h, name);
            return p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(p);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Utilization
    {
        public uint Gpu;
        public uint Memory;
        public uint Encoder;
        public uint Decoder;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryInfo
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct MemoryInfoV2
    {
        public uint Version;
        private uint _padding;
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    public const int TempGpu = 0;
    public const int ClockGraphics = 0;
    public const int ClockSM = 1;
    public const int ClockMem = 2;
    public const int ClockVideo = 3;

    static Nvml()
    {
        // 类构造中初始化，失败静默
        TryInit();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryShutdown();
    }

    private static void TryInit()
    {
        try
        {
            if (_resolved) return;
            _resolved = true;
            if (F.Init == null) return;
            if (F.Init() != 0) return;
            IsAvailable = true;
        }
        catch { }
    }

    private static void TryShutdown()
    {
        try
        {
            if (IsAvailable) F.Shutdown?.Invoke();
            IsAvailable = false;
        }
        catch { }
    }

    /// <summary>GPU 数量（0 = 没有 NVIDIA GPU 或不可用）。</summary>
    public static int DeviceCount
    {
        get
        {
            if (!IsAvailable) return 0;
            try { return F.GetCount!(out var c) == 0 ? (int)c : 0; }
            catch { return 0; }
        }
    }

    public static IntPtr GetDevice(int index)
    {
        if (!IsAvailable) return IntPtr.Zero;
        try { return F.GetHandle!((uint)index, out var d) == 0 ? d : IntPtr.Zero; }
        catch { return IntPtr.Zero; }
    }

    public static string? GetName(IntPtr d)
    {
        try
        {
            var buf = new byte[128];
            return F.GetName!(d, buf, (uint)buf.Length) == 0
                ? System.Text.Encoding.ASCII.GetString(buf).TrimEnd('\0')
                : null;
        }
        catch { return null; }
    }

    public static Utilization? GetUtilization(IntPtr d)
    {
        try { return F.GetUtil!(d, out var u) == 0 ? u : null; }
        catch { return null; }
    }

    /// <summary>
    /// 显存信息：优先 v1（本机 RTX 5060 实测 v1 返回正确，而 v2 带版本号调用会在该驱动上崩溃），
    /// v1 失败才试 v2。安全第一——宁可少拿到也不崩。
    /// </summary>
    public static MemoryInfo? GetMemory(IntPtr d)
    {
        try
        {
            if (F.GetMem != null && F.GetMem(d, out var v1) == 0)
                return v1;
            if (F.GetMemV2 != null)
            {
                const uint v2Size = 4 + 4 + 8 * 3; // sizeof(nvmlMemory_v2_t) = 32
                const uint versionV2 = 2 | (v2Size << 16);
                var st = F.GetMemV2(d, (int)versionV2, out var info);
                if (st == 0)
                    return new MemoryInfo { Total = info.Total, Free = info.Free, Used = info.Used };
            }
            return null;
        }
        catch { return null; }
    }

    public static double? GetTemperature(IntPtr d)
    {
        try { return F.GetTemp!(d, TempGpu, out var t) == 0 ? t : null; }
        catch { return null; }
    }

    public static double? GetPowerWatts(IntPtr d)
    {
        try { return F.GetPower!(d, out var mw) == 0 ? mw / 1000.0 : null; }
        catch { return null; }
    }

    public static double? GetClock(IntPtr d, int type)
    {
        try { return F.GetClock!(d, type, out var mhz) == 0 ? mhz : null; }
        catch { return null; }
    }

    public static double? GetFanPercent(IntPtr d)
    {
        try { return F.GetFan!(d, out var s) == 0 ? s : null; }
        catch { return null; }
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string name);

    [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);
}
