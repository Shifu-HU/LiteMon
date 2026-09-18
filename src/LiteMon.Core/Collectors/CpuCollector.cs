using System;
using System.Collections.Generic;
using LiteMon.Core.Interop;

namespace LiteMon.Core.Collectors;

/// <summary>
/// CPU 采集器：总/每核使用率走 PDH 英文计数器（Processor Information(*)% Processor Time），
/// 失败退回 Processor(*)；频率/温度/功耗走 LibreHardwareMonitor（需要管理员，失败静默降级）。
/// 注意：PDH 要求同一 query 的调用都发生在创建线程（PDH 线程亲和限制），
/// MonitorService 用单线程循环驱动，因此安全。
/// </summary>
public sealed class CpuCollector : IDisposable
{
    private PdhQuery? _perCoreQuery;      // 每核 + 总
    private readonly List<(int CoreIndex, IntPtr Handle)> _coreHandles = new();
    private IntPtr _totalHandle;
    private int _logicalCores;
    private string _cpuName = "";
    private double _baseClockMHz;          // 标称频率（Win32_Processor.MaxClockSpeed）
    private PdhQuery? _freqQuery;          // 非管理员时的频率推算：% Processor Performance
    private IntPtr _perfHandle;

    // LibreHardwareMonitor
    private LibreHardwareMonitor.Hardware.Computer? _lhb;
    private LibreHardwareMonitor.Hardware.ISensor? _lhbTempSensor;
    private LibreHardwareMonitor.Hardware.ISensor? _lhbPowerSensor;
    private LibreHardwareMonitor.Hardware.ISensor? _lhbClockSensor;
    private bool _lhbTried;

    private const double PDH_MORE_DATA = unchecked((int)0x800007D2);

    public void Initialize()
    {
        _logicalCores = NativeMethods.LogicalProcessorCount;
        _cpuName = QueryCpuName();
        _baseClockMHz = QueryBaseClock();

        // ---- 每核使用率（Processor Information，Win7+；含 _Total）----
        // PDH 的 AddCounter 不支持实例通配符——必须先 Expand 再逐条添加
        _perCoreQuery = BuildCoreQuery("\\Processor Information(*)\\% Processor Time")
                        ?? BuildCoreQuery("\\Processor(*)\\% Processor Time");

        // ---- 频率（% of Maximum Frequency 不稳定；用 % Processor Performance × 标称频率）----
        _freqQuery = PdhQuery.TryCreate();
        if (_freqQuery != null && !_freqQuery.TryAdd("\\Processor Information(_Total)\\% Processor Performance"))
        {
            _freqQuery.Dispose();
            _freqQuery = null;
        }
        if (_freqQuery != null)
        {
            foreach (var (_, h) in _freqQuery.Counters) _perfHandle = h;
        }
    }

    /// <summary>展开通配符并逐条添加计数器；每个实例一个句柄。</summary>
    private PdhQuery? BuildCoreQuery(string wildPath)
    {
        var q = PdhQuery.TryCreate();
        if (q == null) return null;
        try
        {
            var paths = PdhQuery.ExpandWildCard(wildPath);
            if (paths == null || paths.Count == 0) { q.Dispose(); return null; }
            foreach (var p in paths)
            {
                if (!q.TryAdd(p)) continue;
                var h = q.Counters[q.Counters.Count - 1].Handle;
                if (p.Contains("_Total", StringComparison.OrdinalIgnoreCase))
                {
                    // 优先保留纯 "_Total"（全机合计）；"(0,_Total)" 是单组小计
                    if (_totalHandle == IntPtr.Zero || p.Contains("(_Total)")) _totalHandle = h;
                    continue;
                }
                var idx = ParseCoreIndex(p);
                if (idx >= 0) _coreHandles.Add((idx, h));
            }
            _coreHandles.Sort((a, b) => a.CoreIndex.CompareTo(b.CoreIndex));
            if (_coreHandles.Count == 0 && _totalHandle == IntPtr.Zero) { q.Dispose(); return null; }
            return q;
        }
        catch
        {
            q.Dispose();
            return null;
        }
    }

    private static string QueryCpuName()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
            foreach (var o in searcher.Get())
                return o["Name"]?.ToString()?.Trim() ?? "";
        }
        catch { }
        return "";
    }

    private static double QueryBaseClock()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher("SELECT MaxClockSpeed FROM Win32_Processor");
            foreach (var o in searcher.Get())
                return Convert.ToDouble(o["MaxClockSpeed"] ?? 0);
        }
        catch { }
        return 0;
    }

    private static int ParseCoreIndex(string counterPath)
    {
        // 路径形如 \ComputerProcessor Information(0,3)% Processor Time
        var open = counterPath.IndexOf('(');
        var close = counterPath.IndexOf(')', open + 1);
        if (open < 0 || close < 0) return -1;
        var inst = counterPath[(open + 1)..close];
        var comma = inst.IndexOf(',');
        if (comma >= 0 && int.TryParse(inst[(comma + 1)..], out var n)) return n;
        return int.TryParse(inst, out var m) ? m : -1;
    }

    /// <summary>采样一次。前两帧（首次启动/系统刚恢复）PDH 会给无效值，返回 false 让调用方丢弃。</summary>
    public bool TrySample(out double total, out double[] perCore, out double? freqMHz)
    {
        total = double.NaN; perCore = Array.Empty<double>(); freqMHz = null;
        if (_perCoreQuery == null) return false;

        if (!_perCoreQuery.Collect()) return false;

        if (_totalHandle != IntPtr.Zero)
        {
            var v = _perCoreQuery.ReadDouble(_totalHandle);
            if (v.HasValue && v.Value >= 0) total = Math.Min(100, v.Value);
        }
        if (double.IsNaN(total))
        {
            // 没拿到 _Total：把每核平均
            double sum = 0; int n = 0;
            var arr = new double[Math.Max(_coreHandles.Count, 1)];
            foreach (var (idx, h) in _coreHandles)
            {
                var v = _perCoreQuery.ReadDouble(h);
                if (v.HasValue) { sum += v.Value; n++; if (idx < arr.Length) arr[idx] = v.Value; }
            }
            if (n > 0) total = Math.Min(100, sum / n);
        }
        else
        {
            perCore = new double[_coreHandles.Count];
            for (int i = 0; i < _coreHandles.Count; i++)
            {
                var v = _perCoreQuery.ReadDouble(_coreHandles[i].Handle);
                perCore[i] = v.HasValue ? Math.Max(0, Math.Min(100, v.Value)) : 0;
            }
        }

        // 频率：优先 LHb（管理员），否则 % Processor Performance × 标称频率
        freqMHz = TrySampleLhbClock() ?? TrySamplePdhFrequency();
        return !double.IsNaN(total);
    }

    private double? TrySamplePdhFrequency()
    {
        if (_freqQuery == null || _perfHandle == IntPtr.Zero || _baseClockMHz <= 0) return null;
        try
        {
            if (!_freqQuery.Collect()) return null;
            var v = _freqQuery.ReadDouble(_perfHandle);
            return v is > 0 ? v * _baseClockMHz / 100.0 : null;
        }
        catch { return null; }
    }

    // ---------------- LibreHardwareMonitor（温度/功耗/实际频率；需要管理员）----------------

    private void EnsureLhb()
    {
        if (_lhbTried) return;
        _lhbTried = true;
        try
        {
            var c = new LibreHardwareMonitor.Hardware.Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = false,
                IsMemoryEnabled = false,
                IsMotherboardEnabled = false,
                IsStorageEnabled = false,
                IsNetworkEnabled = false,
                IsControllerEnabled = false,
                IsPsuEnabled = false,
                IsBatteryEnabled = false,
            };
            c.Open();
            _lhb = c;
        }
        catch { _lhb = null; }
    }

    private DateTime _lastLhbUpdate = DateTime.MinValue;

    /// <summary>刷新 LHb 的 CPU 硬件传感器并返回首个 CPU 硬件（3 秒节流，温度变化慢）。</summary>
    private LibreHardwareMonitor.Hardware.IHardware? UpdateLhbCpu()
    {
        if (_lhb == null) return null;
        foreach (var hw in _lhb.Hardware)
        {
            if (hw.HardwareType != LibreHardwareMonitor.Hardware.HardwareType.Cpu) continue;
            if ((DateTime.UtcNow - _lastLhbUpdate).TotalSeconds >= 3)
            {
                hw.Update();
                _lastLhbUpdate = DateTime.UtcNow;
            }
            return hw;
        }
        return null;
    }

    public double? TrySampleLhbTemperature()
    {
        try
        {
            EnsureLhb();
            var hw = UpdateLhbCpu();
            if (hw == null) return null;
            if (_lhbTempSensor == null)
            {
                foreach (var s in hw.Sensors)
                {
                    if (s.SensorType != LibreHardwareMonitor.Hardware.SensorType.Temperature) continue;
                    var n = s.Name;
                    if (n.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
                        n.Contains("Core", StringComparison.OrdinalIgnoreCase))
                    { _lhbTempSensor = s; break; }
                }
            }
            return _lhbTempSensor?.Value;
        }
        catch { return null; }
    }

    public double? TrySampleLhbPower()
    {
        try
        {
            EnsureLhb();
            var hw = UpdateLhbCpu();
            if (hw == null) return null;
            if (_lhbPowerSensor == null)
            {
                foreach (var s in hw.Sensors)
                {
                    if (s.SensorType == LibreHardwareMonitor.Hardware.SensorType.Power &&
                        s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                    { _lhbPowerSensor = s; break; }
                }
            }
            return _lhbPowerSensor?.Value;
        }
        catch { return null; }
    }

    private double? TrySampleLhbClock()
    {
        try
        {
            EnsureLhb();
            var hw = UpdateLhbCpu();
            if (hw == null) return null;
            if (_lhbClockSensor == null)
            {
                foreach (var s in hw.Sensors)
                {
                    if (s.SensorType != LibreHardwareMonitor.Hardware.SensorType.Clock) continue;
                    // 平均核心频率（"CPU Core #1" 之类的首个核心时钟）
                    if (s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))
                    { _lhbClockSensor = s; break; }
                }
            }
            return _lhbClockSensor?.Value;
        }
        catch { return null; }
    }

    public string CpuName => _cpuName;
    public int LogicalCores => _logicalCores;
    public double BaseClockMHz => _baseClockMHz;
    public bool IsElevated => NativeMethods.IsElevated;

    public void Dispose()
    {
        try { _lhb?.Close(); } catch { }
        try { _perCoreQuery?.Dispose(); } catch { }
        try { _freqQuery?.Dispose(); } catch { }
        _lhb = null; _perCoreQuery = null; _freqQuery = null;
    }
}
