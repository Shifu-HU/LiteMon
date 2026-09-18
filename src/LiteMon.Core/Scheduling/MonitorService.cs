using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using LiteMon.Core.Collectors;
using LiteMon.Core.Models;

namespace LiteMon.Core.Scheduling;

/// <summary>
/// 监控调度服务：单一后台线程循环采集（PDH 线程亲和要求 + 避免多线程开销）。
/// - 快照不可变，采集线程 → UI 线程单向传递
/// - 任何采集器异常都被吞掉并降级，绝不向上抛
/// - 刷新率可运行时调整（事件唤醒）
/// </summary>
public sealed class MonitorService : IDisposable
{
    private readonly CpuCollector _cpu = new();
    private readonly MemoryCollector _mem = new();
    private readonly GpuCollector _gpu = new();
    private readonly NetworkCollector _net = new();
    private readonly DiskCollector _disk = new();
    private readonly DiskProcessCollector _diskProc = new();
    private readonly ProcessCollector _proc = new();
    private readonly NetworkProcessCollector _netProc = new();

    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private readonly AutoResetEvent _wake = new(false);
    private int _intervalMs = 1000; // volatile 只支持整型；用毫秒存（完整快照节奏）
    private volatile bool _paused;
    private volatile bool _collectProcesses = true;
    private static readonly bool _hasProcessesCategory = CheckGpuCounterCategories();

    /// <summary>曲线专用高频历史（0.1s 一帧，独立于快照节奏）。</summary>
    public CurveHistory Curves { get; } = new();

    private const int CurveTickMs = 100;   // 曲线采样固定 0.1s：600 点 = 60 秒窗口

    private DateTime _lastProcSample = DateTime.MinValue;
    private IReadOnlyList<ProcessMetrics> _lastProcesses = Array.Empty<ProcessMetrics>();
    private DateTime _lastDiskProcSample = DateTime.MinValue;
    private IReadOnlyList<DiskProcessMetrics> _lastDiskProcs = Array.Empty<DiskProcessMetrics>();
    private DateTime _lastNetProcSample = DateTime.MinValue;
    private IReadOnlyList<NetworkProcessMetrics> _lastNetProcs = Array.Empty<NetworkProcessMetrics>();

    /// <summary>最新快照（永远有值；启动后先由首帧填充）。UI 线程读、采集线程写引用——引用赋值原子。</summary>
    public Snapshot? Latest { get; private set; }

    /// <summary>每帧回调（在采集线程触发；UI 层自行调度到 UI 线程）。</summary>
    public event Action<Snapshot>? SnapshotProduced;

    public double IntervalSeconds
    {
        get => Thread.VolatileRead(ref _intervalMs) / 1000.0;
        set
        {
            // 完整快照节奏（曲线已解耦为固定 0.1s 高频采样，见 Curves）
            Thread.VolatileWrite(ref _intervalMs, (int)(Math.Clamp(value, 0.5, 5) * 1000));
            _wake.Set();
        }
    }

    public bool Paused
    {
        get => _paused;
        set { _paused = value; _wake.Set(); }
    }

    public bool CollectProcesses
    {
        get => _collectProcesses;
        set { _collectProcesses = value; _proc.EnableGpuPerProcess = value; }
    }

    private static bool CheckGpuCounterCategories()
    {
        // GPU Engine / GPU Process Memory 类别（Win10+；Server 无则进程 GPU 列全为 0）
        try
        {
            var q = Interop.PdhQuery.TryCreate();
            if (q == null) return false;
            var ok = q.TryAdd("\\GPU Engine(*)\\Utilization Percentage");
            q.Dispose();
            return ok;
        }
        catch { return false; }
    }

    /// <summary>
    /// 睡眠唤醒后调用：作废网络/磁盘的曲线专用 PDH query（实例可能已失效），随后惰性重建。
    /// </summary>
    public void InvalidateCurves()
    {
        _net.InvalidateCurve();
        _disk.InvalidateCurve();
        CurveHistoryReset();
    }

    private void CurveHistoryReset()
    {
        Curves.Reset();
    }

    public void Start()
    {
        if (_thread != null) return;
        _cts = new CancellationTokenSource();
        _proc.EnableGpuPerProcess = _collectProcesses;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "LiteMon.Collect",
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.Start();
    }

    private void Loop()
    {
        _cpu.Initialize();
        _gpu.Initialize();

        // 预热两帧 PDH（首帧经常无效）
        try
        {
            _cpu.TrySample(out _, out _, out _);
            _net.Sample();
        }
        catch { }

        bool first = true;
        long accumulatedMs = 0;   // 距上次完整快照的累计毫秒
        while (true)
        {
            var ct = _cts!.Token;
            if (ct.IsCancellationRequested) break;

            var intervalMs = Thread.VolatileRead(ref _intervalMs);
            var sw = Stopwatch.StartNew();

            if (!_paused)
            {
                // ---- 完整快照帧：按刷新间隔（默认 1s）。快照的采集同时充作该 0.1s 槽位的曲线数据，
                //      避免同一循环里对同一 PDH query 二次 collect（间隔≈0 会让速率计数器读到 100% 假值）。
                accumulatedMs += CurveTickMs;
                var dueSnapshot = accumulatedMs >= intervalMs - CurveTickMs / 2;
                if (dueSnapshot)
                {
                    accumulatedMs = 0;
                    var snap = CollectOnce(first);
                    if (snap != null)
                    {
                        Latest = snap;
                        SnapshotProduced?.Invoke(snap);
                        // 快照值即曲线值：8 条序列同帧推进
                        // 磁盘读/写合计 = 各盘相加（口径与页面一致）
                        double dR = 0, dW = 0;
                        foreach (var d in snap.Disks) { dR += d.ReadBytesPerSec; dW += d.WriteBytesPerSec; }
                        Curves.Push(snap.Cpu.TotalUsage,
                                    snap.Gpu.IsAvailable ? snap.Gpu.Usage : 0,
                                    snap.Memory.UsagePercent,
                                    snap.Memory.CommitUsedPercent,
                                    snap.Network.DownloadBytesPerSec,
                                    snap.Network.UploadBytesPerSec,
                                    dR, dW);
                    }
                    first = false;
                }
                else
                {
                    // ---- 曲线帧：轻量采集（0.1s 一次）。net/disk 走专用轻量路径（不查 WLAN、
                    //      不建明细），且各自 collect 独立 query，不与快照采集冲突 ----
                    try
                    {
                        double cpuTotal = 0;
                        if (_cpu.TrySample(out var t, out _, out _)) cpuTotal = t;
                        double gpuUsage = 0;
                        try { gpuUsage = _gpu.Sample().Usage; } catch { }
                        var m = _mem.Sample();
                        double netRx = 0, netTx = 0;
                        try { (netRx, netTx) = _net.SampleTotal(); } catch { }
                        double dR = 0, dW = 0;
                        try { (dR, dW) = _disk.SampleIoTotal(); } catch { }
                        Curves.Push(cpuTotal, gpuUsage, m.UsagePercent, m.CommitUsedPercent,
                                    netRx, netTx, dR, dW);
                    }
                    catch { }
                }
            }

            var remaining = CurveTickMs - (int)sw.ElapsedMilliseconds;
            if (remaining > 0)
            {
                // 等待：被取消 / 被设置变更唤醒则立刻继续
                _wake.WaitOne(remaining);
            }
        }

        // 退出前清理
        try { _cpu.Dispose(); } catch { }
        try { _gpu.Dispose(); } catch { }
        try { _net.Dispose(); } catch { }
        try { _disk.Dispose(); } catch { }
        try { _diskProc.Dispose(); } catch { }
        try { _proc.Dispose(); } catch { }
    }

    private Snapshot? CollectOnce(bool first)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // ---- CPU ----
            double cpuTotal = 0;
            double[] perCore = Array.Empty<double>();
            double? freq = null;
            double? cpuTemp = null, cpuPower = null;
            try
            {
                if (_cpu.TrySample(out var t, out var pc, out var f))
                {
                    cpuTotal = t; perCore = pc; freq = f;
                }
                cpuTemp = _cpu.TrySampleLhbTemperature();
                cpuPower = _cpu.TrySampleLhbPower();
            }
            catch { }

            var cpu = new CpuMetrics
            {
                Name = _cpu.CpuName,
                TotalUsage = Math.Round(cpuTotal, 1),
                PerCoreUsage = perCore,
                FrequencyMHz = freq,
                TemperatureC = cpuTemp,
                PowerWatts = cpuPower,
                LogicalCores = _cpu.LogicalCores,
                UsageSource = MetricSource.Pdh,
            };

            // ---- 内存 ----
            MemoryMetrics mem;
            try { mem = _mem.Sample(); } catch { mem = new MemoryMetrics(); }

            // ---- GPU ----
            GpuMetrics gpu;
            try { gpu = _gpu.Sample(); } catch { gpu = new GpuMetrics { Notes = "GPU 采集失败" }; }

            // ---- 网络 ----
            NetworkMetrics net;
            try { net = _net.Sample(); } catch { net = new NetworkMetrics(); }

            // ---- 磁盘 ----
            IReadOnlyList<DiskMetrics> disks = Array.Empty<DiskMetrics>();
            if (!first)
            {
                try { disks = _disk.Sample(); } catch { }
            }

            // ---- 磁盘进程占用（哪些应用在读写，每 2s 一次）----
            IReadOnlyList<DiskProcessMetrics> diskProcs = _lastDiskProcs;
            if (_collectProcesses && !first && (DateTime.UtcNow - _lastDiskProcSample).TotalSeconds >= 2.0)
            {
                try
                {
                    diskProcs = _diskProc.Sample();
                    _lastDiskProcs = diskProcs;
                    _lastDiskProcSample = DateTime.UtcNow;
                }
                catch { }
            }

            // ---- 网络进程占用（TCP estats 差分，每 2s 一次，需管理员）----
            IReadOnlyList<NetworkProcessMetrics> netProcs = _lastNetProcs;
            if (!first && (DateTime.UtcNow - _lastNetProcSample).TotalSeconds >= 2.0)
            {
                try
                {
                    var raw = _netProc.Sample();
                    netProcs = raw;
                    _lastNetProcs = netProcs;
                    _lastNetProcSample = DateTime.UtcNow;
                }
                catch { }
            }

            // ---- 进程（可选；首次不采 + 每 2 秒最多一次，降低 Process.GetProcesses 开销）----
            IReadOnlyList<ProcessMetrics> processes = _lastProcesses;
            if (_collectProcesses && !first && (DateTime.UtcNow - _lastProcSample).TotalSeconds >= 2.0)
            {
                try
                {
                    processes = _proc.Sample();
                    _lastProcesses = processes;
                    _lastProcSample = DateTime.UtcNow;
                }
                catch { }
            }

            return new Snapshot
            {
                Cpu = cpu,
                Memory = mem,
                Gpu = gpu,
                Network = net,
                Disks = disks,
                DiskProcesses = diskProcs,
                NetworkProcesses = netProcs,
                NetworkProcessApproximate = _netProc.IsApproximate,
                Processes = processes,
                CollectDurationMs = sw.Elapsed.TotalMilliseconds,
            };
        }
        catch
        {
            return null; // 采集总异常 → 返回 null，跳过本帧（不崩）
        }
        finally
        {
            sw.Stop();
        }
    }

    public void Dispose()
    {
        try
        {
            _cts?.Cancel();
            _wake.Set();
            _thread?.Join(2000);
            _cts?.Dispose();
            _wake.Dispose();
            _netProc.Dispose();
        }
        catch { }
    }
}
