using System;

namespace LiteMon.Core.Scheduling;

/// <summary>
/// 曲线专用高频历史：8 条 600 点环形缓冲（共用头指针，天然对齐）。
/// cpu / gpu / ram / ramCommit / netRx / netTx / diskR / diskW
/// - 采集线程 0.1s 一推（EMA 拟合平滑后入队），UI 线程拷出渲染
/// - 600 点 × 0.1s = 60 秒窗口，波形移动缓慢、细节平滑
/// </summary>
public sealed class CurveHistory
{
    public const int Capacity = 600;

    private readonly double[] _cpu = new double[Capacity];
    private readonly double[] _gpu = new double[Capacity];
    private readonly double[] _ram = new double[Capacity];
    private readonly double[] _ramCommit = new double[Capacity];
    private readonly double[] _netRx = new double[Capacity];
    private readonly double[] _netTx = new double[Capacity];
    private readonly double[] _diskR = new double[Capacity];
    private readonly double[] _diskW = new double[Capacity];
    private int _count, _head;
    private readonly object _lock = new();

    // EMA 拟合状态（对 0.1s 原始采样做指数平滑，消除 PDH 高频抖动）
    private double _sCpu, _sGpu, _sRam, _sRamCommit, _sNetRx, _sNetTx, _sDiskR, _sDiskW;
    private bool _primed;

    private const double Alpha = 0.30;   // 平滑系数：0.3 ≈ 3~4 个采样的时间常数

    private static double C0(double v) => double.IsNaN(v) || v < 0 ? 0 : v;

    /// <summary>
    /// 推入一帧原始值（内部 EMA 平滑后存储）。副序列（ramCommit/net/disk）为可选——
    /// 未传时保持上一平滑值（曲线连续，不出现归零断崖）。
    /// </summary>
    public void Push(double cpu, double gpu, double ram,
        double? ramCommit = null, double? netRx = null, double? netTx = null,
        double? diskR = null, double? diskW = null)
    {
        cpu = C0(cpu); gpu = C0(gpu); ram = C0(ram);
        var rc = ramCommit ?? _sRamCommit;
        var rx = C0(netRx ?? _sNetRx);
        var tx = C0(netTx ?? _sNetTx);
        var dr = C0(diskR ?? _sDiskR);
        var dw = C0(diskW ?? _sDiskW);

        lock (_lock)
        {
            if (!_primed)
            {
                // 首帧直接锚定，避免从 0 爬升的假斜坡
                _sCpu = cpu; _sGpu = gpu; _sRam = ram;
                _sRamCommit = rc; _sNetRx = rx; _sNetTx = tx; _sDiskR = dr; _sDiskW = dw;
                _primed = true;
            }
            else
            {
                _sCpu += Alpha * (cpu - _sCpu);
                _sGpu += Alpha * (gpu - _sGpu);
                _sRam += Alpha * (ram - _sRam);
                _sRamCommit += Alpha * (rc - _sRamCommit);
                _sNetRx += Alpha * (rx - _sNetRx);
                _sNetTx += Alpha * (tx - _sNetTx);
                _sDiskR += Alpha * (dr - _sDiskR);
                _sDiskW += Alpha * (dw - _sDiskW);
            }

            _cpu[_head] = Math.Round(_sCpu, 2);
            _gpu[_head] = Math.Round(_sGpu, 2);
            _ram[_head] = Math.Round(_sRam, 2);
            _ramCommit[_head] = Math.Round(_sRamCommit, 2);
            _netRx[_head] = Math.Round(_sNetRx, 1);
            _netTx[_head] = Math.Round(_sNetTx, 1);
            _diskR[_head] = Math.Round(_sDiskR, 1);
            _diskW[_head] = Math.Round(_sDiskW, 1);
            _head = (_head + 1) % Capacity;
            if (_count < Capacity) _count++;
        }
    }

    /// <summary>拷出 CPU 序列（最旧在前）。返回实际点数；dest 不足时只拷尾部。</summary>
    public int CopyCpu(double[] dest) => Copy(_cpu, dest);
    public int CopyGpu(double[] dest) => Copy(_gpu, dest);
    public int CopyRam(double[] dest) => Copy(_ram, dest);
    public int CopyRamCommit(double[] dest) => Copy(_ramCommit, dest);
    public int CopyNetRx(double[] dest) => Copy(_netRx, dest);
    public int CopyNetTx(double[] dest) => Copy(_netTx, dest);
    public int CopyDiskR(double[] dest) => Copy(_diskR, dest);
    public int CopyDiskW(double[] dest) => Copy(_diskW, dest);

    /// <summary>当前已有点数（0..Capacity）。</summary>
    public int Count
    {
        get { lock (_lock) { return _count; } }
    }

    public double LatestCpu => Latest(_cpu);
    public double LatestGpu => Latest(_gpu);
    public double LatestRam => Latest(_ram);
    public double LatestRamCommit => Latest(_ramCommit);
    public double LatestNetRx => Latest(_netRx);
    public double LatestNetTx => Latest(_netTx);
    public double LatestDiskR => Latest(_diskR);
    public double LatestDiskW => Latest(_diskW);

    private double Latest(double[] src)
    {
        lock (_lock) { return _count == 0 ? 0 : src[(_head - 1 + Capacity) % Capacity]; }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _count = 0; _head = 0; _primed = false;
            _sCpu = _sGpu = _sRam = _sRamCommit = _sNetRx = _sNetTx = _sDiskR = _sDiskW = 0;
        }
    }

    private int Copy(double[] src, double[] dest)
    {
        if (dest == null || dest.Length == 0) return 0;
        lock (_lock)
        {
            int n = Math.Min(_count, dest.Length);
            int start = (_head - n + Capacity * 2) % Capacity;   // 最旧
            for (int i = 0; i < n; i++)
                dest[i] = src[(start + i) % Capacity];
            return n;
        }
    }
}
