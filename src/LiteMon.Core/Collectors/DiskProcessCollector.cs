using System;
using System.Collections.Generic;
using LiteMon.Core.Interop;
using LiteMon.Core.Models;

namespace LiteMon.Core.Collectors;

/// <summary>
/// 磁盘进程占用采集器：PDH Process(*)\IO Read/Write Bytes/sec（与任务管理器"磁盘"列同口径：
/// 进程整体文件/管道 IO 速率，无需管理员权限）。实例随进程增删变化，定期重建；
/// pid 通过同实例的 "ID Process" 计数器读取。
/// </summary>
public sealed class DiskProcessCollector : IDisposable
{
    private PdhQuery? _query;
    private readonly List<(string Name, IntPtr Read, IntPtr Write, IntPtr Id)> _handles = new();
    private int _age;
    private const int RebuildEvery = 10;

    public IReadOnlyList<DiskProcessMetrics> Sample()
    {
        var result = new List<DiskProcessMetrics>(64);
        try
        {
            _age++;
            if (_query == null || _age >= RebuildEvery)
            {
                _age = 0;
                Rebuild();
            }
            if (_query == null || !_query.Collect()) return result;

            var map = new Dictionary<int, DiskProcessMetrics>();
            foreach (var (name, readH, writeH, idH) in _handles)
            {
                int pid = 0;
                if (idH != IntPtr.Zero)
                {
                    var pidv = _query.ReadLarge(idH);
                    if (pidv is > 0) pid = (int)pidv.Value;
                }
                var read = _query.ReadLarge(readH) ?? 0;
                var write = _query.ReadLarge(writeH) ?? 0;
                if (read <= 0 && write <= 0) continue;
                if (map.TryGetValue(pid, out var m))
                {
                    map[pid] = new DiskProcessMetrics
                    {
                        Name = m.Name, Pid = pid,
                        ReadBytesPerSec = m.ReadBytesPerSec + read,
                        WriteBytesPerSec = m.WriteBytesPerSec + write,
                    };
                }
                else
                {
                    map[pid] = new DiskProcessMetrics { Name = name, Pid = pid, ReadBytesPerSec = read, WriteBytesPerSec = write };
                }
            }
            result.AddRange(map.Values);
        }
        catch { }
        return result;
    }

    private void Rebuild()
    {
        try
        {
            _query?.Dispose();
            _handles.Clear();

            var q = PdhQuery.TryCreate();
            if (q == null) return;

            var readPaths = PdhQuery.ExpandWildCard(@"\Process(*)\IO Read Bytes/sec");
            var writePaths = PdhQuery.ExpandWildCard(@"\Process(*)\IO Write Bytes/sec");
            var idPaths = PdhQuery.ExpandWildCard(@"\Process(*)\ID Process");
            if (readPaths == null || writePaths == null) { q.Dispose(); return; }

            var read = new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
            var write = new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
            var ids = new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);

            foreach (var p in readPaths!) if (q.TryAdd(p) && TryInstance(p, out var i)) read[i] = q.Counters[q.Counters.Count - 1].Handle;
            foreach (var p in writePaths!) if (q.TryAdd(p) && TryInstance(p, out var i)) write[i] = q.Counters[q.Counters.Count - 1].Handle;
            if (idPaths != null)
                foreach (var p in idPaths) if (q.TryAdd(p) && TryInstance(p, out var i)) ids[i] = q.Counters[q.Counters.Count - 1].Handle;

            foreach (var (inst, readH) in read)
            {
                write.TryGetValue(inst, out var writeH);
                ids.TryGetValue(inst, out var idH);
                // "chrome#1" -> "chrome"；"_Total" 留着无害（通常速率 0）
                var name = inst.Contains('#') ? inst[..inst.IndexOf('#')] : inst;
                _handles.Add((name, readH, writeH, idH));
            }

            if (_handles.Count == 0) { q.Dispose(); return; }
            _query = q;
        }
        catch { _query = null; }
    }

    private static bool TryInstance(string path, out string instance)
    {
        var open = path.IndexOf('(');
        var close = path.IndexOf(')', open + 1);
        if (open < 0 || close < 0) { instance = ""; return false; }
        instance = path[(open + 1)..close];
        return true;
    }

    public void Dispose()
    {
        try { _query?.Dispose(); } catch { }
        _query = null;
    }
}
