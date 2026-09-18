using System;
using System.Collections.Generic;

namespace LiteMon.Core.Models;

/// <summary>一次采集得到的完整硬件快照（不可变对象）。</summary>
public sealed class Snapshot
{
    public required CpuMetrics Cpu { get; init; }
    public required MemoryMetrics Memory { get; init; }
    public required GpuMetrics Gpu { get; init; }
    public required NetworkMetrics Network { get; init; }
    public required IReadOnlyList<DiskMetrics> Disks { get; init; }
    public required IReadOnlyList<DiskProcessMetrics> DiskProcesses { get; init; }
    /// <summary>每进程网络速率（TCP estats 差分，需管理员；按 ↓+↑ 排序由 UI 做）。</summary>
    public required IReadOnlyList<NetworkProcessMetrics> NetworkProcesses { get; init; }
    /// <summary>网络应用列表是否为估算口径（estats 不可用 → IO Data）。</summary>
    public bool NetworkProcessApproximate { get; init; }
    public required IReadOnlyList<ProcessMetrics> Processes { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    /// <summary>采集这一帧耗时（毫秒），调试用。</summary>
    public double CollectDurationMs { get; init; }
}

/// <summary>来源标记：数值是从哪条链路取到的。</summary>
public enum MetricSource
{
    None,       // 不可用
    Nvml,       // NVIDIA 官方库
    LibreHardware,
    Pdh,        // Windows 性能计数器
    Native,     // GlobalMemoryStatusEx 等原生 API
}

public sealed class CpuMetrics
{
    /// <summary>CPU 名称（如 "Intel Core i7-12700K"）。</summary>
    public string Name { get; init; } = "";
    /// <summary>总使用率 0~100。</summary>
    public double TotalUsage { get; init; }
    /// <summary>每逻辑核使用率 0~100（与 TotalUsage 同一套刻度）。</summary>
    public IReadOnlyList<double> PerCoreUsage { get; init; } = Array.Empty<double>();
    /// <summary>当前平均有效频率（MHz）。null = 不支持。</summary>
    public double? FrequencyMHz { get; init; }
    /// <summary>CPU 温度（℃）。null = 不支持（未提权 / 无传感器）。</summary>
    public double? TemperatureC { get; init; }
    /// <summary>CPU 封装功耗（W）。null = 不支持。</summary>
    public double? PowerWatts { get; init; }
    /// <summary>逻辑处理器数量。</summary>
    public int LogicalCores { get; init; }
    public MetricSource UsageSource { get; init; } = MetricSource.Pdh;
    public MetricSource FrequencySource { get; init; } = MetricSource.None;
    public MetricSource TempSource { get; init; } = MetricSource.None;
    public string Notes { get; init; } = "";
}

public sealed class MemoryMetrics
{
    /// <summary>物理内存总量（字节）。</summary>
    public ulong TotalBytes { get; init; }
    /// <summary>已用物理内存（字节）。</summary>
    public ulong UsedBytes { get; init; }
    public double UsagePercent => TotalBytes == 0 ? 0 : Math.Round(UsedBytes * 100.0 / TotalBytes, 1);
    /// <summary>可用物理内存。</summary>
    public ulong AvailableBytes { get; init; }
    /// <summary>已提交内存 Used/Total。</summary>
    public ulong CommitTotalBytes { get; init; }
    public ulong CommitUsedBytes { get; init; }
    /// <summary>已提交占用百分比（0..100，曲线用）。</summary>
    public double CommitUsedPercent => CommitTotalBytes == 0 ? 0 : CommitUsedBytes * 100.0 / CommitTotalBytes;
    public MetricSource Source { get; init; } = MetricSource.Native;
}

public sealed class GpuMetrics
{
    /// <summary>选中的主 GPU 名称。</summary>
    public string Name { get; init; } = "";
    /// <summary>GPU 渲染使用率 0~100。</summary>
    public double Usage { get; init; }
    /// <summary>显存已用（字节）。</summary>
    public ulong VramUsedBytes { get; init; }
    /// <summary>显存总量（字节）。</summary>
    public ulong VramTotalBytes { get; init; }
    public double VramPercent => VramTotalBytes == 0 ? 0 : Math.Round(VramUsedBytes * 100.0 / VramTotalBytes, 1);
    public double? TemperatureC { get; init; }
    public double? PowerWatts { get; init; }
    /// <summary>GPU 核心频率（MHz）。</summary>
    public double? CoreFrequencyMHz { get; init; }
    /// <summary>显存频率（MHz）。</summary>
    public double? MemoryFrequencyMHz { get; init; }
    /// <summary>风扇转速百分比 0~100。</summary>
    public double? FanPercent { get; init; }
    /// <summary>GPU 厂商。</summary>
    public GpuVendor Vendor { get; init; } = GpuVendor.Unknown;
    public MetricSource UsageSource { get; init; } = MetricSource.None;
    public MetricSource VramSource { get; init; } = MetricSource.None;
    public MetricSource TempSource { get; init; } = MetricSource.None;
    public string Notes { get; init; } = "";
    public bool IsAvailable => UsageSource != MetricSource.None || VramSource != MetricSource.None;
}

public enum GpuVendor { Unknown, Nvidia, Amd, Intel }

public sealed class NetworkMetrics
{
    /// <summary>当前下行速率（字节/秒）。</summary>
    public double DownloadBytesPerSec { get; init; }
    /// <summary>当前上行速率（字节/秒）。</summary>
    public double UploadBytesPerSec { get; init; }
    public MetricSource Source { get; init; } = MetricSource.None;

    /// <summary>每个网络接口的实时速率明细（含 Wi-Fi/以太网，供 WIFI 页展示）。</summary>
    public IReadOnlyList<NetworkInterfaceMetrics> Interfaces { get; init; } = Array.Empty<NetworkInterfaceMetrics>();
}

/// <summary>网络接口类型。</summary>
public enum InterfaceKind { Other, Wifi, Ethernet }

/// <summary>单个网络接口的实时信息（WIFI 页 / 接口速率表用）。</summary>
public sealed class NetworkInterfaceMetrics
{
    public required string Name { get; init; }
    public InterfaceKind Kind { get; init; } = InterfaceKind.Other;
    /// <summary>Wi-Fi：是否已连接。</summary>
    public bool IsConnected { get; init; }
    /// <summary>Wi-Fi：SSID。</summary>
    public string Ssid { get; init; } = "";
    /// <summary>Wi-Fi：信号质量 0~100（WLAN_SIGNAL_QUALITY）。</summary>
    public uint SignalPercent { get; init; }
    /// <summary>Wi-Fi：当前信道。</summary>
    public uint Channel { get; init; }
    /// <summary>Wi-Fi：802.11 协议（如 802.11be (Wi-Fi 7)）。</summary>
    public string PhyType { get; init; } = "";
    /// <summary>Wi-Fi：链路协商速率（Kbps）。</summary>
    public uint RxLinkKbps { get; init; }
    public uint TxLinkKbps { get; init; }
    /// <summary>驱动累计收/发帧数（帧计数，1 帧 ≈ 1 个 MAC 包，作速率差分用）。</summary>
    public ulong RxBytes { get; init; }
    public ulong TxBytes { get; init; }
    /// <summary>接口状态文本（已连接/未连接/…）。</summary>
    public string StateText { get; init; } = "";
    /// <summary>本帧实时下行速率（字节/秒，接口级）。</summary>
    public double DownloadBytesPerSec { get; init; }
    /// <summary>本帧实时上行速率（字节/秒，接口级）。</summary>
    public double UploadBytesPerSec { get; init; }
}

/// <summary>磁盘进程占用（哪些应用在读写磁盘）。</summary>
public sealed class DiskProcessMetrics
{
    public required string Name { get; init; }
    public int Pid { get; init; }
    /// <summary>读速率（字节/秒）。</summary>
    public double ReadBytesPerSec { get; init; }
    /// <summary>写速率（字节/秒）。</summary>
    public double WriteBytesPerSec { get; init; }
}

/// <summary>单个进程的网络速率（TCP estats 差分；任务管理器"网络"列同口径）。</summary>
public sealed class NetworkProcessMetrics
{
    public int Pid { get; init; }
    /// <summary>下行速率（字节/秒）。</summary>
    public double RxBytesPerSec { get; init; }
    /// <summary>上行速率（字节/秒）。</summary>
    public double TxBytesPerSec { get; init; }
    /// <summary>估算口径（IO Data Bytes，含磁盘/管道 IO；estats 不可用时）。</summary>
    public bool IsApproximate { get; init; }
}

/// <summary>单个进程的占用信息（任务管理器风格）。</summary>
public sealed class ProcessMetrics
{
    public int Pid { get; init; }
    public string Name { get; init; } = "";
    /// <summary>进程 CPU 使用率（0~100，按全部逻辑核归一化，与任务管理器一致）。</summary>
    public double CpuPercent { get; init; }
    /// <summary>工作集内存（字节）——任务管理器“内存”列。</summary>
    public ulong WorkingSetBytes { get; init; }
    /// <summary>专用内存（字节）。</summary>
    public ulong PrivateBytes { get; init; }
    /// <summary>进程 GPU 使用率（0~100，GPU Engine 计数器求和，粗略值）。</summary>
    public double GpuPercent { get; init; }
    /// <summary>进程专用显存占用（字节）。</summary>
    public ulong GpuDedicatedBytes { get; init; }
    /// <summary>进程共享显存占用（字节）。</summary>
    public ulong GpuSharedBytes { get; init; }
    public bool IsSuspended { get; init; }
    public double? CpuFrequencyMHz { get; init; }
}
