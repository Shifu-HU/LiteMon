using System;
using System.Collections.Generic;

namespace LiteMon.Core.Models;

public enum DiskKind { Fixed, Removable }

/// <summary>单个磁盘（卷）的信息。</summary>
public sealed class DiskMetrics
{
    /// <summary>盘符，如 "C:"。</summary>
    public required string Letter { get; init; }
    /// <summary>卷标。</summary>
    public string Label { get; init; } = "";
    /// <summary>磁盘类型（固定/可移动）。</summary>
    public DiskKind Kind { get; init; }
    /// <summary>总容量（字节）。</summary>
    public ulong TotalBytes { get; init; }
    /// <summary>剩余空间（字节）。</summary>
    public ulong FreeBytes { get; init; }
    public ulong UsedBytes => TotalBytes - FreeBytes;
    public double UsagePercent => TotalBytes == 0 ? 0 : Math.Round(UsedBytes * 100.0 / TotalBytes, 1);
    /// <summary>读速度（字节/秒）。</summary>
    public double ReadBytesPerSec { get; init; }
    /// <summary>写速度（字节/秒）。</summary>
    public double WriteBytesPerSec { get; init; }
    /// <summary>活动时间百分比（0~100，可能不可用）。</summary>
    public double? ActivePercent { get; init; }
    public bool IsAvailable => TotalBytes > 0;
}

/// <summary>整机磁盘信息。</summary>
public sealed class DiskSetMetrics
{
    public IReadOnlyList<DiskMetrics> Disks { get; init; } = Array.Empty<DiskMetrics>();
    /// <summary>本轮检测到的可移动盘盘符集合（与上帧对比用）。</summary>
    public IReadOnlyList<string> RemovableLetters { get; init; } = Array.Empty<string>();
}
