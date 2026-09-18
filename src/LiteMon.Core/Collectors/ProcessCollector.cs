using System;
using System.Collections.Generic;
using System.Diagnostics;
using LiteMon.Core.Interop;
using LiteMon.Core.Models;

namespace LiteMon.Core.Collectors;

/// <summary>
/// 进程占用采集器（任务管理器口径）：
/// - CPU%：两帧 ΔTotalProcessorTime / ΔWallTime / 逻辑核数 → 归一化到全机 0~100
/// - 内存：WorkingSet64（任务管理器“内存”列）+ PrivateMemorySize64
/// - GPU%：GPU Engine 计数器按 pid 求和（cap 100）
/// - 显存：GPU Process Memory Dedicated/Shared 按 pid 求和
/// 不抛异常；进程枚举失败返回空表。
/// </summary>
public sealed class ProcessCollector : IDisposable
{
    private sealed class Acc
    {
        public double Util;
        public ulong Dedicated;
        public ulong Shared;
    }

    private readonly double _cores = Math.Max(1, NativeMethods.LogicalProcessorCount);
    private Dictionary<int, (DateTime t, TimeSpan cpu)> _lastCpu = new();

    // GPU 计数器（实例随进程增删变化，定期重建查询）
    private PdhQuery? _gpuQuery;
    private readonly List<(int Pid, IntPtr Handle, Kind Kind)> _handles = new();
    private int _age;
    private const int RebuildEvery = 15;

    private enum Kind { Util, Dedicated, Shared }

    public bool EnableGpuPerProcess { get; set; } = true;

    public IReadOnlyList<ProcessMetrics> Sample()
    {
        var now = DateTime.UtcNow;
        var gpu = SampleGpuPerPid();

        var newLast = new Dictionary<int, (DateTime, TimeSpan)>(_lastCpu.Count);
        var result = new List<ProcessMetrics>(160);

        Process[] processes;
        try { processes = Process.GetProcesses(); }
        catch { return result; }

        foreach (var p in processes)
        {
            try
            {
                double cpuPct = 0;
                ulong ws = 0, privateBytes = 0;
                string name;
                try { name = p.ProcessName; } catch { name = "?"; }

                try
                {
                    var cpu = p.TotalProcessorTime;   // 可能抛（已退出/权限不足）→ 该进程 CPU 显示 0
                    if (_lastCpu.TryGetValue(p.Id, out var prev))
                    {
                        var wall = (now - prev.t).TotalSeconds;
                        if (wall > 0.15)
                        {
                            var coresUsed = (cpu - prev.cpu).TotalSeconds / wall;
                            cpuPct = Math.Min(100.0, coresUsed * 100.0 / _cores);
                        }
                    }
                    newLast[p.Id] = (now, cpu);
                }
                catch { }

                try { ws = (ulong)p.WorkingSet64; } catch { }
                try { privateBytes = (ulong)p.PrivateMemorySize64; } catch { }

                gpu.TryGetValue(p.Id, out var acc);

                result.Add(new ProcessMetrics
                {
                    Pid = p.Id,
                    Name = name,
                    CpuPercent = Math.Round(cpuPct, 1),
                    WorkingSetBytes = ws,
                    PrivateBytes = privateBytes,
                    GpuPercent = acc != null ? Math.Round(Math.Min(100.0, acc.Util), 1) : 0,
                    GpuDedicatedBytes = acc?.Dedicated ?? 0,
                    GpuSharedBytes = acc?.Shared ?? 0,
                });
            }
            catch { }
            finally { p.Dispose(); }
        }

        _lastCpu = newLast;
        return result;
    }

    // ---------------- GPU per-process ----------------

    private Dictionary<int, Acc> SampleGpuPerPid()
    {
        var map = new Dictionary<int, Acc>();
        if (!EnableGpuPerProcess) return map;

        try
        {
            _age++;
            if (_gpuQuery == null || _age >= RebuildEvery)
            {
                _age = 0;
                RebuildGpuQuery();
            }
            if (_gpuQuery == null || !_gpuQuery.Collect()) return map;

            foreach (var (pid, h, kind) in _handles)
            {
                double? v = kind == Kind.Util ? _gpuQuery.ReadDouble(h) : _gpuQuery.ReadLarge(h);
                if (v is not > 0) continue;
                if (!map.TryGetValue(pid, out var acc))
                {
                    acc = new Acc();
                    map[pid] = acc;
                }
                switch (kind)
                {
                    case Kind.Util: acc.Util += v.Value; break;
                    case Kind.Dedicated: acc.Dedicated += (ulong)v.Value; break;
                    case Kind.Shared: acc.Shared += (ulong)v.Value; break;
                }
            }
        }
        catch { }

        return map;
    }

    private void RebuildGpuQuery()
    {
        try
        {
            _gpuQuery?.Dispose();
            _gpuQuery = null;
            _handles.Clear();

            var q = PdhQuery.TryCreate();
            if (q == null) return;

            AddCounterSet(q, "\\GPU Engine(*)\\Utilization Percentage", Kind.Util);
            AddCounterSet(q, "\\GPU Process Memory(*)\\Dedicated Usage", Kind.Dedicated);
            AddCounterSet(q, "\\GPU Process Memory(*)\\Shared Usage", Kind.Shared);

            if (_handles.Count > 0) _gpuQuery = q;
            else q.Dispose();
        }
        catch { _gpuQuery = null; }
    }

    private void AddCounterSet(PdhQuery q, string wild, Kind kind)
    {
        var paths = PdhQuery.ExpandWildCard(wild);
        if (paths == null) return;
        foreach (var p in paths)
        {
            var pid = ExtractPid(p);
            if (pid <= 0) continue;
            if (q.TryAdd(p))
                _handles.Add((pid, q.Counters[q.Counters.Count - 1].Handle, kind));
        }
    }

    private static int ExtractPid(string path)
    {
        // 实例名：pid_1234_luid_0x0000..._phys_0_eng_0_engtype_3D
        var open = path.IndexOf('(');
        var close = path.IndexOf(')', open + 1);
        if (open < 0 || close <= open + 1) return -1;
        var inst = path.AsSpan((open + 1)..close);
        if (!inst.StartsWith("pid_", StringComparison.OrdinalIgnoreCase)) return -1;
        var rest = inst[4..];
        var end = rest.IndexOf('_');
        var num = end < 0 ? rest : rest[..end];
        return int.TryParse(num, out var pid) ? pid : -1;
    }

    public void Dispose()
    {
        try { _gpuQuery?.Dispose(); } catch { }
        _gpuQuery = null;
    }
}
