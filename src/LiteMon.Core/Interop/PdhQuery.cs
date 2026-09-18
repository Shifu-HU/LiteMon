using System;
using System.Runtime.InteropServices;
using System.Text;

namespace LiteMon.Core.Interop;

/// <summary>
/// PDH（性能数据助手）封装：使用 PdhAddEnglishCounterW，计数器名与系统语言无关，
/// 在中文 Windows 上同样可用（System.Diagnostics.PerformanceCounter 依赖本地化名称，故不用）。
/// 线程模型：一个 PdhQuery 只能由创建线程访问（PDH 限制）；调用方保证。
/// </summary>
internal sealed class PdhQuery : IDisposable
{
    private IntPtr _query;
    private readonly List<(string Path, IntPtr Handle)> _counters = new();

    private PdhQuery(IntPtr query) { _query = query; }

    public static PdhQuery? TryCreate()
    {
        var st = PdhOpenQueryW(IntPtr.Zero, 0, out var q);
        if (st != 0) return null;
        return new PdhQuery(q);
    }

    /// <summary>添加一个英文计数器路径。失败（类别缺失等）返回 false。</summary>
    public bool TryAdd(string path)
    {
        var st = PdhAddEnglishCounterW(_query, path, 0, out var h);
        if (st != 0) return false;
        _counters.Add((path, h));
        return true;
    }

    public IReadOnlyList<(string Path, IntPtr Handle)> Counters => _counters;

    /// <summary>收集一次数据。返回 false 表示本次采集失败（可稍后重试）。</summary>
    public bool Collect()
    {
        // 两次 Collect 之间间隔太短会拿到 PDH_INVALID_DATA / 负值，调用方控制间隔 ≥500ms
        return PdhCollectQueryData(_query) == 0;
    }

    /// <summary>读取计数器当前值（双精度）。失败返回 null。</summary>
    public double? ReadDouble(IntPtr counterHandle)
    {
        const int PDH_FMT_DOUBLE = 0x200;
        var st = PdhGetFormattedCounterValue(counterHandle, PDH_FMT_DOUBLE, out _, out var v);
        if (st != 0) return null;
        return v.doubleValue;
    }

    /// <summary>读取计数器原始 64 位值（如 GPU Process Memory 的字节数）。</summary>
    public double? ReadLarge(IntPtr counterHandle)
    {
        const int PDH_FMT_LARGE = 0x400;
        var st = PdhGetFormattedCounterValue(counterHandle, PDH_FMT_LARGE, out _, out var v);
        if (st != 0) return null;
        return (double)v.largeValue;
    }

    public void Dispose()
    {
        foreach (var (_, h) in _counters)
        {
            if (h != IntPtr.Zero) PdhRemoveCounter(h);
        }
        _counters.Clear();
        if (_query != IntPtr.Zero)
        {
            PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>展开通配符实例路径（如 \Network Interface(*)\Bytes Sent/sec）为具体实例列表。</summary>
    public static List<string>? ExpandWildCard(string wildPath)
    {
        try
        {
            unsafe
            {
                // 第一次调用取所需长度（PDH_MORE_DATA = 0x800007D2）
                uint need = 0;
                var st = PdhExpandWildCardPathW(null, wildPath, null, ref need, 0);
                if (st != 0 && st != PdhMoreData) return null;
                if (need == 0) return new List<string>();

                // 用原始 char[] 缓冲（multisz：NUL 分隔、双 NUL 结尾）
                var buf = new char[need];
                fixed (char* p = buf)
                {
                    st = PdhExpandWildCardPathW(null, wildPath, p, ref need, 0);
                }
                if (st != 0) return null;

                var result = new List<string>(8);
                int i = 0, len = (int)Math.Min(need, (uint)buf.Length);
                while (i < len)
                {
                    int start = i;
                    while (i < len && buf[i] != '\0') i++;
                    if (i > start) result.Add(new string(buf, start, i - start));
                    i++; // 跳过 NUL
                }
                return result;
            }
        }
        catch { return null; }
    }

    private const int PdhMoreData = unchecked((int)0x800007D2);

    [StructLayout(LayoutKind.Explicit)]
    private struct PDH_FMT_COUNTERVALUE
    {
        [FieldOffset(0)] public uint status;
        [FieldOffset(8)] public double doubleValue;
        [FieldOffset(8)] public long largeValue;
        [FieldOffset(8)] public int longValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhOpenQueryW(IntPtr source, IntPtr flags, out IntPtr queryHandle);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhAddEnglishCounterW(IntPtr queryHandle, string counterPath, IntPtr flags, out IntPtr counterHandle);

    [DllImport("pdh.dll")]
    private static extern int PdhCollectQueryData(IntPtr queryHandle);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern int PdhGetFormattedCounterValue(IntPtr counterHandle, int format, out uint type, out PDH_FMT_COUNTERVALUE value);

    [DllImport("pdh.dll")]
    private static extern int PdhRemoveCounter(IntPtr counterHandle);

    [DllImport("pdh.dll")]
    private static extern int PdhCloseQuery(IntPtr queryHandle);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern unsafe int PdhExpandWildCardPathW(string? source, string? wildCardPath, char* expandedPathList, ref uint bufferLength, uint flags);
}
