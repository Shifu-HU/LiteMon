using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LiteMon.Core.Interop;
using LiteMon.Core.Models;

namespace LiteMon.Core.Collectors;

/// <summary>
/// 每进程网络速率采集器（任务管理器"网络"列同口径的两种来源）：
/// 1) TCP estats（GetPerTcpConnectionEStats Data 字节差分）——最准，需管理员 + 栈支持；
///    多数新 Win11 消费机栈返回 ERROR_NOT_SUPPORTED(50)。
/// 2) 回退：PDH \Process(*)\IO Data Bytes/sec（socket 收发也计入进程 IO，下载型应用排序准确；
///    含磁盘/管道 IO，属估算口径，UI 注明）。
/// </summary>
public sealed class NetworkProcessCollector : IDisposable
{
    public bool IsApproximate { get; private set; }   // true = 走 IO Data 估算口径

    // ================= estats 路径 =================
    private Dictionary<int, (DateTime t, ulong rx, ulong tx)> _last = new();
    private bool _estatsProbed;
    private bool _estatsAvailable;

    private const int AF_INET = 2;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint State, LocalAddr, LocalPort, RemoteAddr, RemotePort;
        public int OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW
    {
        public uint State, LocalAddr, LocalPort, RemoteAddr, RemotePort;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TCP_ESTATS_DATA_RW_v0
    {
        public byte EnableCollection;
    }

    [DllImport("iphlpapi.dll")]
    private static extern int GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int TableClass, int Reserved);

    [DllImport("iphlpapi.dll")]
    private static extern int GetPerTcpConnectionEStats(ref MIB_TCPROW Row, int EstatsType,
        ref TCP_ESTATS_DATA_RW_v0 Rw, int RwVersion, int RwSize,
        IntPtr Rod, int RodVersion, int RodSize);

    [DllImport("iphlpapi.dll")]
    private static extern int SetPerTcpConnectionEStats(ref MIB_TCPROW Row, int EstatsType,
        ref TCP_ESTATS_DATA_RW_v0 Rw, int RwVersion, int RwSize);

    // ================= PDH IO Data 回退路径 =================
    private PdhQuery? _ioQuery;
    private readonly List<(string Name, IntPtr Data, IntPtr Id)> _ioHandles = new();
    private int _ioAge;
    private const int RebuildEvery = 10;

    public IReadOnlyList<NetworkProcessMetrics> Sample()
    {
        try
        {
            if (!_estatsProbed)
            {
                _estatsProbed = true;
                _estatsAvailable = ProbeEstats();
                IsApproximate = !_estatsAvailable;
            }
            return _estatsAvailable ? SampleEstats() : SampleIoData();
        }
        catch { return Array.Empty<NetworkProcessMetrics>(); }
    }

    // ---------- estats ----------

    private static bool ProbeEstats()
    {
        try
        {
            var rows = EnumerateTcpRows();
            foreach (var row in rows)
            {
                if (row.State != 5) continue;
                var got = ReadEstats(row, out _);
                if (got) return true;   // 任一连接可读即认为支持
            }
        }
        catch { }
        return false;
    }

    private IReadOnlyList<NetworkProcessMetrics> SampleEstats()
    {
        var result = new List<NetworkProcessMetrics>(32);
        var rows = EnumerateTcpRows();
        if (rows.Count == 0) return result;
        var now = DateTime.UtcNow;

        var totals = new Dictionary<int, (ulong rx, ulong tx)>();
        foreach (var row in rows)
        {
            if (row.State != 5) continue;
            if (!ReadEstats(row, out var est)) continue;
            totals.TryGetValue(row.OwningPid, out var acc);
            totals[row.OwningPid] = (acc.rx + est.rx, acc.tx + est.tx);
        }

        foreach (var (pid, (rx, tx)) in totals)
        {
            if (_last.TryGetValue(pid, out var prev))
            {
                var dt = (now - prev.t).TotalSeconds;
                if (dt > 0.15)
                {
                    var drx = rx >= prev.rx ? rx - prev.rx : 0;
                    var dtx = tx >= prev.tx ? tx - prev.tx : 0;
                    if (drx > 0 || dtx > 0)
                        result.Add(new NetworkProcessMetrics { Pid = pid, RxBytesPerSec = drx / dt, TxBytesPerSec = dtx / dt });
                }
            }
            _last[pid] = (now, rx, tx);
        }

        if (_last.Count > 512)
        {
            var alive = new HashSet<int>(totals.Keys);
            foreach (var k in _last.Keys)
                if (!alive.Contains(k)) _last.Remove(k);
        }
        return result;
    }

    private static bool ReadEstats(MIB_TCPROW_OWNER_PID row, out (ulong rx, ulong tx) bytes)
    {
        bytes = (0, 0);
        var mib = new MIB_TCPROW
        {
            State = row.State, LocalAddr = row.LocalAddr, LocalPort = row.LocalPort,
            RemoteAddr = row.RemoteAddr, RemotePort = row.RemotePort,
        };
        var rw = new TCP_ESTATS_DATA_RW_v0 { EnableCollection = 1 };
        try { SetPerTcpConnectionEStats(ref mib, 1, ref rw, 0, 1); } catch { }

        var rod = Marshal.AllocHGlobal(88);
        try
        {
            var rc = GetPerTcpConnectionEStats(ref mib, 1, ref rw, 0, 1, rod, 0, 88);
            if (rc != 0) return false;
            bytes = ((ulong)Marshal.ReadInt64(rod, 16), (ulong)Marshal.ReadInt64(rod, 0));
            return true;
        }
        finally { Marshal.FreeHGlobal(rod); }
    }

    private static List<MIB_TCPROW_OWNER_PID> EnumerateTcpRows()
    {
        var rows = new List<MIB_TCPROW_OWNER_PID>(128);
        int size = 0;
        var rc = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
        if (rc != 0 || size <= 0) return rows;

        var buf = Marshal.AllocHGlobal(size);
        try
        {
            rc = GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
            if (rc != 0) return rows;
            int count = Marshal.ReadInt32(buf);
            var rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
            for (int i = 0; i < count; i++)
                rows.Add(Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(buf + 4 + i * rowSize));
        }
        finally { Marshal.FreeHGlobal(buf); }
        return rows;
    }

    // ---------- PDH IO Data 回退 ----------

    private IReadOnlyList<NetworkProcessMetrics> SampleIoData()
    {
        var result = new List<NetworkProcessMetrics>(64);
        try
        {
            _ioAge++;
            if (_ioQuery == null || _ioAge >= RebuildEvery)
            {
                _ioAge = 0;
                RebuildIoQuery();
            }
            if (_ioQuery == null || !_ioQuery.Collect()) return result;

            foreach (var (name, dataH, idH) in _ioHandles)
            {
                int pid = 0;
                if (idH != IntPtr.Zero)
                {
                    var v = _ioQuery.ReadLarge(idH);
                    if (v is > 0) pid = (int)v.Value;
                }
                var rate = _ioQuery.ReadLarge(dataH) ?? 0;
                if (rate <= 0 || pid <= 0) continue;   // 有 IO 就显示（低门槛保证列表集合稳定不闪）
                result.Add(new NetworkProcessMetrics
                {
                    Pid = pid,
                    RxBytesPerSec = rate,   // IO Data 不分方向：总量记入下行列，UI 注明估算口径
                    TxBytesPerSec = 0,
                });
            }
        }
        catch { }
        return result;
    }

    private void RebuildIoQuery()
    {
        try
        {
            _ioQuery?.Dispose();
            _ioQuery = null;
            _ioHandles.Clear();

            var q = PdhQuery.TryCreate();
            if (q == null) return;

            var dataPaths = PdhQuery.ExpandWildCard(@"\Process(*)\IO Data Bytes/sec");
            var idPaths = PdhQuery.ExpandWildCard(@"\Process(*)\ID Process");
            if (dataPaths == null) { q.Dispose(); return; }

            var data = new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
            var ids = new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in dataPaths) if (q.TryAdd(p) && TryInstance(p, out var i)) data[i] = q.Counters[q.Counters.Count - 1].Handle;
            if (idPaths != null)
                foreach (var p in idPaths) if (q.TryAdd(p) && TryInstance(p, out var i)) ids[i] = q.Counters[q.Counters.Count - 1].Handle;

            foreach (var (inst, dataH) in data)
            {
                ids.TryGetValue(inst, out var idH);
                if (inst.Equals("_Total", StringComparison.OrdinalIgnoreCase)) continue;
                var name = inst.Contains('#') ? inst[..inst.IndexOf('#')] : inst;
                _ioHandles.Add((name, dataH, idH));
            }

            if (_ioHandles.Count == 0) { q.Dispose(); return; }
            _ioQuery = q;
        }
        catch { _ioQuery = null; }
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
        try { _ioQuery?.Dispose(); } catch { }
        _ioQuery = null;
    }
}
