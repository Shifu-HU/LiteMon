using LiteMon.Core.Interop;
using LiteMon.Core.Models;

namespace LiteMon.Core.Collectors;

/// <summary>内存采集器：GlobalMemoryStatusEx（一次调用 0.01ms，最廉价可靠的来源）。</summary>
public sealed class MemoryCollector
{
    public MemoryMetrics Sample()
    {
        var s = NativeMethods.GetMemoryStatus();
        return new MemoryMetrics
        {
            TotalBytes = s.ullTotalPhys,
            AvailableBytes = s.ullAvailPhys,
            UsedBytes = s.ullTotalPhys - s.ullAvailPhys,
            CommitTotalBytes = s.ullTotalPageFile,
            CommitUsedBytes = s.ullTotalPageFile - s.ullAvailPageFile,
            Source = MetricSource.Native,
        };
    }
}
