using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiteMon.Core.Interop;
using LiteMon.Core.Models;

namespace LiteMon.Core.Collectors;

/// <summary>
/// 磁盘采集器：
/// - 容量/剩余：DriveInfo（.NET 内置，GetDiskFreeSpaceEx 封装）
/// - 读写速度/活动时间：PDH \LogicalDisk(X:)\Disk Read|Write Bytes/sec + % Idle Time（英文计数器）
/// 盘符集合每帧枚举（极廉价），插入/拔出可移动盘自动感知。
/// </summary>
public sealed class DiskCollector : IDisposable
{
    private PdhQuery? _ioQuery;
    /// <summary>键 = 盘符（含冒号，如 "C:"），与 DiskMetrics.Letter 一致。</summary>
    private readonly Dictionary<string, (IntPtr Read, IntPtr Write, IntPtr Idle)> _ioHandles = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastQueryBuild = DateTime.MinValue;
    private IReadOnlyList<DiskMetrics> _lastFixed = Array.Empty<DiskMetrics>();

    // 曲线专用独立 query：与快照的 _ioQuery 分离，避免 0.1s tick 与 1s 快照对同一 PDH query
    // 交错 Collect 导致速率计数器帧间隔错乱（读出 0）。_Total 实例天然是全盘合计。
    private PdhQuery? _curveQuery;
    private IntPtr _curveRead, _curveWrite;

    /// <summary>
    /// 作废曲线专用 query（睡眠唤醒后 PDH 实例可能失效），下次 SampleIoTotal 惰性重建。
    /// </summary>
    public void InvalidateCurve()
    {
        try { _curveQuery?.Dispose(); } catch { }
        _curveQuery = null;
        _curveRead = IntPtr.Zero;
        _curveWrite = IntPtr.Zero;
    }

    /// <summary>
    /// 轻量采样：全盘（_Total）读/写速率合计，供 0.1s 曲线 tick 用。
    /// 首次调用惰性构建专用 query。
    /// </summary>
    public (double read, double write) SampleIoTotal()
    {
        try
        {
            if (_curveQuery == null)
            {
                var q = PdhQuery.TryCreate();
                if (q == null) return (0, 0);
                if (!q.TryAdd(@"\LogicalDisk(_Total)\Disk Read Bytes/sec") ||
                    !q.TryAdd(@"\LogicalDisk(_Total)\Disk Write Bytes/sec"))
                {
                    q.Dispose();
                    return (0, 0);
                }
                _curveRead = q.Counters[0].Handle;
                _curveWrite = q.Counters[1].Handle;
                q.Collect();   // 预热首帧（速率计数器首帧无效）
                _curveQuery = q;
            }
            if (!_curveQuery.Collect()) return (0, 0);
            return (_curveQuery.ReadDouble(_curveRead) ?? 0, _curveQuery.ReadDouble(_curveWrite) ?? 0);
        }
        catch { return (0, 0); }
    }

    /// <summary>采样一次：返回所有盘（固定+可移动）。</summary>
    public IReadOnlyList<DiskMetrics> Sample()
    {
        var disks = new List<DiskMetrics>();
        try
        {
            foreach (var letter in Directory.GetLogicalDrives())
            {
                try
                {
                    var di = new DriveInfo(letter);
                    // 空读卡器槽位 / 无介质盘：DriveType=Removable 且未就绪、无卷标、容量为 0
                    // 这类"盘符存在但没有盘"的槽位不该出现在界面（用户要求：没有盘的盘符就别显示）
                    if (!di.IsReady)
                    {
                        if (di.DriveType == DriveType.Removable || di.DriveType == DriveType.CDRom) continue;
                        disks.Add(MakeNotReady(letter, di));
                        continue;
                    }

                    // 容量为 0 的已就绪盘通常是空槽位，同样跳过
                    if (di.TotalSize <= 0) continue;

                    var kind = di.DriveType switch
                    {
                        DriveType.Fixed => IsExternalDisk(letter) ? DiskKind.Removable : DiskKind.Fixed,
                        DriveType.Removable => DiskKind.Removable,
                        _ => DiskKind.Fixed,   // 网络/CD 盘不单列，归 Fixed 视图（也可扩展）
                    };
                    // 网络盘不纳入（避免掉线卡顿）
                    if (di.DriveType == DriveType.Network) continue;

                    disks.Add(new DiskMetrics
                    {
                        Letter = di.Name.TrimEnd('\\'),
                        Label = di.VolumeLabel,
                        Kind = kind,
                        TotalBytes = di.TotalSize <= 0 ? 0 : (ulong)di.TotalSize,
                        FreeBytes = di.AvailableFreeSpace <= 0 ? 0 : (ulong)di.AvailableFreeSpace,
                    });
                }
                catch { }
            }
        }
        catch { }

        // ---- IO 计数器（每 20 秒重建一次查询以纳入新盘）----
        try
        {
            AttachIo(disks);
            // PDH 速率计数器（Disk Read/Write Bytes/sec）需要**两次采集的差值**才能算出速率：
            // 第一次 Collect 只是建立基线（此时读出来是 0 或无效值），
            // 所以重建查询后必须立刻 Collect 一次，之后每帧再 Collect 才拿得到真实速率。
            // 缺了这一步，读数会永远是 0。
            _ioQuery?.Collect();
        }
        catch { }

        // 合并 IO 数据。先统一采集一帧（PDH 速率计数器取的是两次采集的差值，
        // 每个查询每帧只需 Collect 一次，循环内重复调用是浪费）。
        // Collect 失败（PDH_NO_DATA 等）说明查询里有实例失效，立即作废以便下一帧重建。
        bool ioOk = false;
        try
        {
            ioOk = _ioQuery != null && _ioQuery.Collect();
            if (!ioOk && _ioQuery != null) { try { _ioQuery.Dispose(); } catch { } _ioQuery = null; _ioHandles.Clear(); _lastQueryBuild = DateTime.MinValue; }
        }
        catch { }

        for (int i = 0; i < disks.Count; i++)
        {
            var d = disks[i];
            double read = 0, write = 0;
            double? active = null;
            if (ioOk && _ioQuery != null && _ioHandles.TryGetValue(d.Letter, out var h))
            {
                read = _ioQuery.ReadDouble(h.Read) ?? 0;
                write = _ioQuery.ReadDouble(h.Write) ?? 0;
                var idle = _ioQuery.ReadDouble(h.Idle);
                if (idle.HasValue) active = Math.Max(0, Math.Min(100, 100 - idle.Value));
            }
            disks[i] = new DiskMetrics
            {
                Letter = d.Letter,
                Label = d.Label,
                Kind = d.Kind,
                TotalBytes = d.TotalBytes,
                FreeBytes = d.FreeBytes,
                ReadBytesPerSec = read,
                WriteBytesPerSec = write,
                ActivePercent = active,
            };
        }

        _lastFixed = disks.Where(d => d.Kind == DiskKind.Fixed).ToList();
        return disks;
    }

    /// <summary>
    /// <summary>
    /// 判断盘符背后是不是**外接/可移动**存储。
    /// <para>
    /// 背景：USB 移动硬盘（含 USB 桥接的 SATA/NVMe 硬盘盒）在 Windows 里常被报成
    /// <c>DriveType.Fixed</c>，所以不能只看 DriveInfo。权威信号来自 Win32_DiskDrive：
    /// </para>
    /// <list type="number">
    /// <item>PNPDeviceID 以 <c>USBSTOR</c>/<c>USB</c> 开头，或 InterfaceType 含 USB（最常见的优盘/移动硬盘）</item>
    /// <item><b>MediaType 含 "External"</b>——USB 桥接芯片（JMicron／RTL9210／ASMedia 等）会把自己的盘
    /// 通过 SCSI 通道呈现给系统，此时 PNPDeviceID 是 <c>SCSI\...</c> 且 InterfaceType 是 SCSI，
    /// 但驱动会如实填写 MediaType="External hard disk media"。这是识别这类盘的关键。</item>
    /// <item>MediaType = "Removable Media"（读卡器、优盘介质）</item>
    /// </list>
    /// 结果按盘符缓存 60 秒——采样是 1 秒一次，不能每次都查 WMI。
    /// </summary>
    private static readonly Dictionary<string, bool> _externalCache = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _externalCacheAt = DateTime.MinValue;

    private static bool IsExternalDisk(string letter)
    {
        var key = letter.TrimEnd('\\');
        if ((DateTime.UtcNow - _externalCacheAt).TotalSeconds > 60)
        {
            _externalCache.Clear();
            _externalCacheAt = DateTime.UtcNow;
        }
        if (_externalCache.TryGetValue(key, out var cached)) return cached;

        bool external = false;
        try
        {
            // 该盘符可能横跨多个分区/磁盘（动态磁盘、存储池），任一命中即算外接
            var q = $"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{key}'}} WHERE AssocClass=Win32_LogicalDiskToPartition";
            using var part = new System.Management.ManagementObjectSearcher(q);
            foreach (System.Management.ManagementObject p in part.Get())
            {
                var q2 = $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{p["DeviceID"]}'}} WHERE AssocClass=Win32_DiskDriveToDiskPartition";
                using var drv = new System.Management.ManagementObjectSearcher(q2);
                foreach (System.Management.ManagementObject d in drv.Get())
                {
                    if (IsExternalBus(
                            (d["PNPDeviceID"] as string) ?? "",
                            (d["InterfaceType"] as string) ?? "",
                            (d["MediaType"] as string) ?? ""))
                    {
                        external = true;
                        break;
                    }
                }
                if (external) break;
            }
        }
        catch
        {
            // WMI 不可用时不要误判成外接（宁可按固定盘处理，避免误删页签）
        }
        _externalCache[key] = external;
        return external;
    }

    /// <summary>
    /// 纯判定函数（无 WMI 依赖，便于单元测试）：根据 Win32_DiskDrive 的三个字段判断是否外接总线。
    /// 三种命中任一即视为可移动：
    /// <list type="bullet">
    /// <item>PNPDeviceID 以 USBSTOR / USB 开头 —— 优盘、多数 USB 移动硬盘</item>
    /// <item>InterfaceType 含 USB —— 同上，另一种表述</item>
    /// <item>MediaType 含 External —— <b>USB 桥接硬盘盒走 SCSI 通道时的唯一可靠信号</b></item>
    /// <item>MediaType 含 Removable —— 读卡器/可换介质</item>
    /// </list>
    /// </summary>
    internal static bool IsExternalBus(string pnpDeviceId, string interfaceType, string mediaType)
    {
        return pnpDeviceId.StartsWith("USBSTOR", StringComparison.OrdinalIgnoreCase)
            || pnpDeviceId.StartsWith("USB", StringComparison.OrdinalIgnoreCase)
            || interfaceType.Contains("USB", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("External", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("Removable", StringComparison.OrdinalIgnoreCase);
    }

    private static DiskMetrics MakeNotReady(string letter, DriveInfo di)
    {
        return new DiskMetrics
        {
            Letter = letter.TrimEnd('\\'),
            Label = "未就绪",
            Kind = di.DriveType == DriveType.Removable ? DiskKind.Removable : DiskKind.Fixed,
        };
    }

    private void AttachIo(IReadOnlyList<DiskMetrics> disks)
    {
        // 需要重建：20 秒一次或集合变化
        var letters = disks.Select(d => d.Letter).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var needRebuild = (DateTime.UtcNow - _lastQueryBuild).TotalSeconds > 20;
        if (!needRebuild && _ioQuery != null)
        {
            foreach (var l in letters)
                if (!_ioHandles.ContainsKey(l)) { needRebuild = true; break; }
        }
        if (!needRebuild) return;

        _lastQueryBuild = DateTime.UtcNow;
        var old = _ioQuery;
        _ioQuery = null;
        _ioHandles.Clear();
        try { old?.Dispose(); } catch { }

        var q = PdhQuery.TryCreate();
        if (q == null) { _lastQueryBuild = DateTime.MinValue; return; }   // 失败则下次立即重试

        foreach (var l in letters)
        {
            // Letter 已经带冒号（DriveInfo.Name = "C:\" → TrimEnd('\\') = "C:"），
            // 不能再拼一个，否则路径变成 LogicalDisk(C::)... —— 实例不存在。
            // PdhAddEnglishCounterW 对无效实例是惰性接受的（仍返回成功），
            // 直到 Collect 时才整体返回 PDH_NO_DATA(0x800007D5)，导致所有盘读数恒为 0。
            var inst = l.EndsWith(":", StringComparison.Ordinal) ? l : l + ":";
            IntPtr hR = IntPtr.Zero, hW = IntPtr.Zero, hI = IntPtr.Zero;
            if (q.TryAdd($"\\LogicalDisk({inst})\\Disk Read Bytes/sec"))
                hR = q.Counters[q.Counters.Count - 1].Handle;
            if (q.TryAdd($"\\LogicalDisk({inst})\\Disk Write Bytes/sec"))
                hW = q.Counters[q.Counters.Count - 1].Handle;
            if (q.TryAdd($"\\LogicalDisk({inst})\\% Idle Time"))
                hI = q.Counters[q.Counters.Count - 1].Handle;
            if (hR != IntPtr.Zero || hW != IntPtr.Zero || hI != IntPtr.Zero)
                _ioHandles[inst] = (hR, hW, hI);
        }
        _ioQuery = q;
    }

    public void Dispose()
    {
        try { _ioQuery?.Dispose(); } catch { }
        try { _curveQuery?.Dispose(); } catch { }
        _ioQuery = null;
        _curveQuery = null;
    }
}