using System;
using System.Collections.Generic;
using LiteMon.Core.Interop;
using LiteMon.Core.Models;

namespace LiteMon.Core.Collectors;

/// <summary>
/// GPU 采集器——降级链：NVML（NVIDIA，无需管理员）→ LibreHardwareMonitor（N/A 也可读部分）
/// → PDH GPU 计数器（GPU Engine / GPU Adapter Memory，Win10+，任何厂商）→ 全部不可用。
/// 每条链路失败后标记降级，不再反复尝试造成异常风暴；不抛出任何异常。
/// </summary>
public sealed class GpuCollector : IDisposable
{
    private IntPtr _nvmlDevice;
    private string _nvmlName = "";
    private bool _nvmlTried;

    private LibreHardwareMonitor.Hardware.Computer? _lhb;
    private bool _lhbTried;
    private string _lhbName = "";
    private DateTime _lastLhbUpdate = DateTime.MinValue;

    // PDH 回退（GPU Engine 总利用率没有 _Total；GPU Adapter Memory 可给显存）
    private PdhQuery? _pdhAdapterMem;
    private readonly List<IntPtr> _pdhAdapterMemHandles = new();
    private bool _pdhTried;

    public void Initialize()
    {
        TryInitNvml();
    }

    // ---------------- NVML ----------------

    private void TryInitNvml()
    {
        if (_nvmlTried) return;
        _nvmlTried = true;
        if (!Nvml.IsAvailable || Nvml.DeviceCount == 0) return;
        _nvmlDevice = Nvml.GetDevice(0);
        if (_nvmlDevice == IntPtr.Zero) return;
        _nvmlName = Nvml.GetName(_nvmlDevice) ?? "NVIDIA GPU";
    }

    public GpuMetrics Sample()
    {
        // 1) NVML
        var m = TrySampleNvml();
        if (m != null) return m;

        // 2) LibreHardwareMonitor
        m = TrySampleLhb();
        if (m != null) return m;

        // 3) PDH GPU 计数器
        m = TrySamplePdh();
        if (m != null) return m;

        // 4) 全部不可用
        return new GpuMetrics
        {
            Name = "未检测到 GPU",
            Notes = "无 NVIDIA 驱动或硬件不支持；温度等不可用",
        };
    }

    private GpuMetrics? TrySampleNvml()
    {
        if (_nvmlDevice == IntPtr.Zero) return null;
        try
        {
            var util = Nvml.GetUtilization(_nvmlDevice);
            var memInfo = Nvml.GetMemory(_nvmlDevice);
            var m = new GpuMetrics
            {
                Name = _nvmlName,
                Vendor = GpuVendor.Nvidia,
                Usage = util?.Gpu ?? 0,
                VramTotalBytes = memInfo?.Total ?? 0,
                VramUsedBytes = memInfo?.Used ?? 0,
                TemperatureC = Nvml.GetTemperature(_nvmlDevice),
                PowerWatts = Nvml.GetPowerWatts(_nvmlDevice),
                CoreFrequencyMHz = Nvml.GetClock(_nvmlDevice, Nvml.ClockGraphics),
                MemoryFrequencyMHz = Nvml.GetClock(_nvmlDevice, Nvml.ClockMem),
                FanPercent = Nvml.GetFanPercent(_nvmlDevice),
                UsageSource = MetricSource.Nvml,
                VramSource = memInfo != null ? MetricSource.Nvml : MetricSource.None,
                TempSource = MetricSource.Nvml,
            };
            return m;
        }
        catch { return null; }
    }

    // ---------------- LibreHardwareMonitor ----------------

    private void EnsureLhb()
    {
        if (_lhbTried) return;
        _lhbTried = true;
        try
        {
            var c = new LibreHardwareMonitor.Hardware.Computer
            {
                IsCpuEnabled = false,
                IsGpuEnabled = true,
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

    private GpuMetrics? TrySampleLhb()
    {
        try
        {
            EnsureLhb();
            if (_lhb == null) return null;

            // 找第一个有负载传感器的 GPU（Nvidia/GpuNvidia 优先）
            LibreHardwareMonitor.Hardware.IHardware? gpu = null;
            foreach (var hw in _lhb.Hardware)
            {
                var t = hw.HardwareType;
                if (t == LibreHardwareMonitor.Hardware.HardwareType.GpuNvidia ||
                    t == LibreHardwareMonitor.Hardware.HardwareType.GpuAmd ||
                    t == LibreHardwareMonitor.Hardware.HardwareType.GpuIntel)
                { gpu = hw; break; }
            }
            if (gpu == null) return null;
            if ((DateTime.UtcNow - _lastLhbUpdate).TotalSeconds >= 3)
            {
                gpu.Update();
                _lastLhbUpdate = DateTime.UtcNow;
            }
            if (string.IsNullOrEmpty(_lhbName)) _lhbName = gpu.Name;

            double? load = null, temp = null, power = null, coreClock = null, memClock = null, fan = null;
            ulong? vramUsed = null, vramTotal = null;
            foreach (var s in gpu.Sensors)
            {
                switch (s.SensorType)
                {
                    case LibreHardwareMonitor.Hardware.SensorType.Load:
                        if (s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase)) load ??= s.Value;
                        break;
                    case LibreHardwareMonitor.Hardware.SensorType.Temperature:
                        temp ??= s.Value; break;
                    case LibreHardwareMonitor.Hardware.SensorType.Power:
                        if (s.Name.Contains("Power", StringComparison.OrdinalIgnoreCase)) power ??= s.Value;
                        break;
                    case LibreHardwareMonitor.Hardware.SensorType.Clock:
                        if (s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase)) coreClock ??= s.Value;
                        else if (s.Name.Contains("Memory", StringComparison.OrdinalIgnoreCase)) memClock ??= s.Value;
                        break;
                    case LibreHardwareMonitor.Hardware.SensorType.Control:
                        fan ??= s.Value; break;
                    case LibreHardwareMonitor.Hardware.SensorType.SmallData:
                        if (s.Name.Contains("Memory Used", StringComparison.OrdinalIgnoreCase)) vramUsed ??= (ulong?)MapToUlong(s.Value);
                        else if (s.Name.Contains("Memory Total", StringComparison.OrdinalIgnoreCase)) vramTotal ??= (ulong?)MapToUlong(s.Value);
                        break;
                    case LibreHardwareMonitor.Hardware.SensorType.Data:
                        if (s.Name.Contains("Memory Used", StringComparison.OrdinalIgnoreCase)) vramUsed ??= MapToUlong(s.Value);
                        else if (s.Name.Contains("Memory Total", StringComparison.OrdinalIgnoreCase)) vramTotal ??= MapToUlong(s.Value);
                        break;
                }
            }

            if (load == null && vramUsed == null) return null; // 一个有效传感器都没有 → 降级

            var vendor = gpu.HardwareType switch
            {
                LibreHardwareMonitor.Hardware.HardwareType.GpuNvidia => GpuVendor.Nvidia,
                LibreHardwareMonitor.Hardware.HardwareType.GpuAmd => GpuVendor.Amd,
                LibreHardwareMonitor.Hardware.HardwareType.GpuIntel => GpuVendor.Intel,
                _ => GpuVendor.Unknown,
            };

            return new GpuMetrics
            {
                Name = _lhbName,
                Vendor = vendor,
                Usage = load ?? 0,
                VramUsedBytes = vramUsed ?? 0,
                VramTotalBytes = vramTotal ?? 0,
                TemperatureC = temp,
                PowerWatts = power,
                CoreFrequencyMHz = coreClock,
                MemoryFrequencyMHz = memClock,
                FanPercent = fan,
                UsageSource = load != null ? MetricSource.LibreHardware : MetricSource.None,
                VramSource = vramUsed != null ? MetricSource.LibreHardware : MetricSource.None,
                TempSource = temp != null ? MetricSource.LibreHardware : MetricSource.None,
                Notes = vendor != GpuVendor.Nvidia ? "非 NVIDIA GPU，使用通用传感器读取" : "",
            };
        }
        catch { return null; }
    }

    private static ulong MapToUlong(float? v) => v is >= 0 ? (ulong)Math.Round(v.Value * 1024 * 1024) : 0;

    // ---------------- PDH GPU 计数器（Win10+，任意厂商）----------------

    private GpuMetrics? TrySamplePdh()
    {
        try
        {
            if (!_pdhTried)
            {
                _pdhTried = true;
                // GPU Adapter Memory（每个适配器一行：Dedicated/Sahred Usage）
                var q = PdhQuery.TryCreate();
                if (q != null)
                {
                    var paths = PdhQuery.ExpandWildCard("\\GPU Adapter Memory(*)\\Dedicated Usage") ?? new List<string>();
                    if (paths.Count > 0)
                    {
                        foreach (var p in paths)
                            if (q.TryAdd(p))
                            {
                                // 记录句柄
                            }
                        _pdhAdapterMem = q;
                        foreach (var (_, h) in q.Counters) _pdhAdapterMemHandles.Add(h);
                    }
                    else q.Dispose();
                }
            }
            if (_pdhAdapterMem == null) return null;

            if (!_pdhAdapterMem.Collect()) return null;
            ulong used = 0;
            foreach (var h in _pdhAdapterMemHandles)
            {
                var v = _pdhAdapterMem.ReadLarge(h);
                if (v is >= 0) used += (ulong)v;
            }
            if (used == 0) return null;

            return new GpuMetrics
            {
                Name = "GPU（性能计数器）",
                VramUsedBytes = used,
                VramTotalBytes = 0, // PDH 无法拿到显存总量
                VramSource = MetricSource.Pdh,
                UsageSource = MetricSource.None,
                Notes = "驱动不支持详细查询，仅显存占用可用",
            };
        }
        catch { return null; }
    }

    public void Dispose()
    {
        try { _lhb?.Close(); } catch { }
        try { _pdhAdapterMem?.Dispose(); } catch { }
        _lhb = null; _pdhAdapterMem = null;
    }
}
