using System;
using System.Collections.Generic;
using LiteMon.Core.Interop;
using LiteMon.Core.Models;

namespace LiteMon.Core.Collectors;

/// <summary>
/// 网络速率采集器：PDH Network Interface(*)Bytes Sent|Received/sec。
/// - 全机汇总（排除回环/虚拟接口）
/// - 每个接口的实时速率明细（Interfaces，供 WIFI 页展示）
/// Wi-Fi 的 SSID/信号/信道来自 NativeWifi（WlanInterop），在接口枚举时合并。
/// </summary>
public sealed class NetworkCollector : IDisposable
{
    private PdhQuery? _query;
    private readonly List<(string Instance, IntPtr Handle)> _rx = new();
    private readonly List<(string Instance, IntPtr Handle)> _tx = new();
    private bool _initTried;
    private double _lastRx, _lastTx;   // 上次值（字节/秒）
    private DateTime _lastTime = DateTime.UtcNow;

    // Wi-Fi 接口帧计数差分（上帧值）: instance -> (rx, tx, time)
    private readonly Dictionary<string, (ulong Rx, ulong Tx, DateTime T)> _lastWifiFrames = new(StringComparer.OrdinalIgnoreCase);

    private void EnsureInit()
    {
        if (_initTried) return;
        _initTried = true;
        var q = PdhQuery.TryCreate();
        if (q == null) return;
        try
        {
            var rxPaths = PdhQuery.ExpandWildCard(@"\Network Interface(*)\Bytes Received/sec") ?? new List<string>();
            var txPaths = PdhQuery.ExpandWildCard(@"\Network Interface(*)\Bytes Sent/sec") ?? new List<string>();
            foreach (var p in rxPaths)
                if (q.TryAdd(p) && TryInstance(p, out var inst)) _rx.Add((inst, q.Counters[q.Counters.Count - 1].Handle));
            foreach (var p in txPaths)
                if (q.TryAdd(p) && TryInstance(p, out var inst)) _tx.Add((inst, q.Counters[q.Counters.Count - 1].Handle));
            if (_rx.Count == 0 && _tx.Count == 0) { q.Dispose(); return; }
            _query = q;
        }
        catch { q.Dispose(); }
    }

    private static bool TryInstance(string path, out string instance)
    {
        var open = path.IndexOf('(');
        var close = path.IndexOf(')', open + 1);
        if (open < 0 || close < 0) { instance = ""; return false; }
        instance = path[(open + 1)..close];
        return true;
    }

    /// <summary>
    /// 过滤：排除本地回环与各种虚拟接口，求和只算真实物理网卡流量
    /// （否则 Wi-Fi Direct/蓝牙 PAN/WAN Miniport 的虚拟实例会把合计叠大）。
    /// </summary>
    public static bool IsNoise(string instance)
    {
        var n = instance;
        return n.Contains("Loopback", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Local Area Connection*", StringComparison.OrdinalIgnoreCase)
            || n.Contains("isatap", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Teredo", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase)
            || n.Contains("WAN Miniport", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Microsoft Wi-Fi Direct", StringComparison.OrdinalIgnoreCase)
            // Hosted Network / 移动热点虚拟接口
            || n.Contains("Hosted Network", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Local Area", StringComparison.OrdinalIgnoreCase) && n.Contains("*", StringComparison.Ordinal);
    }

    // 曲线专用独立 query：与快照的 _query 分离，避免 0.1s tick 与快照交错 Collect 干扰速率计数器
    private PdhQuery? _curveQuery;
    private readonly List<(string Instance, IntPtr Handle)> _curveRx = new();
    private readonly List<(string Instance, IntPtr Handle)> _curveTx = new();

    /// <summary>
    /// 作废曲线专用 query（睡眠唤醒后网卡 PDH 实例可能失效），下次 SampleTotal 惰性重建。
    /// </summary>
    public void InvalidateCurve()
    {
        try { _curveQuery?.Dispose(); } catch { }
        _curveQuery = null;
        _curveRx.Clear();
        _curveTx.Clear();
    }

    /// <summary>
    /// 轻量采样：仅 Collect + 对真实物理接口求 rx/tx 合计（不查 WLAN、不建接口明细）。
    /// 供 0.1s 曲线 tick 使用（WLAN 查询留在快照帧做，避免高频开销）。
    /// 首次调用惰性构建专用 query。
    /// </summary>
    public (double rx, double tx) SampleTotal()
    {
        EnsureInit();
        try
        {
            if (_curveQuery == null)
            {
                var q = PdhQuery.TryCreate();
                if (q == null) return (0, 0);
                var ok = false;
                var rxPaths = PdhQuery.ExpandWildCard(@"\Network Interface(*)\Bytes Received/sec") ?? new List<string>();
                var txPaths = PdhQuery.ExpandWildCard(@"\Network Interface(*)\Bytes Sent/sec") ?? new List<string>();
                foreach (var p in rxPaths)
                {
                    if (!TryInstance(p, out var inst) || IsNoise(inst)) continue;
                    if (q.TryAdd(p)) { _curveRx.Add((inst, q.Counters[q.Counters.Count - 1].Handle)); ok = true; }
                }
                foreach (var p in txPaths)
                {
                    if (!TryInstance(p, out var inst) || IsNoise(inst)) continue;
                    if (q.TryAdd(p)) { _curveTx.Add((inst, q.Counters[q.Counters.Count - 1].Handle)); ok = true; }
                }
                if (!ok) { q.Dispose(); return (0, 0); }
                q.Collect();   // 预热首帧
                _curveQuery = q;
            }
            if (!_curveQuery.Collect()) return (_lastRx, _lastTx);
            double rx = 0, tx = 0;
            foreach (var (_, h) in _curveRx) rx += _curveQuery.ReadDouble(h) ?? 0;
            foreach (var (_, h) in _curveTx) tx += _curveQuery.ReadDouble(h) ?? 0;
            _lastRx = rx; _lastTx = tx;
            return (rx, tx);
        }
        catch { return (_lastRx, _lastTx); }
    }

    public NetworkMetrics Sample()
    {
        EnsureInit();
        if (_query == null)
            return new NetworkMetrics();

        try
        {
            if (!_query.Collect()) return Last(Array.Empty<NetworkInterfaceMetrics>());

            // ---- 接口明细 ----
            var wifi = WlanInterop.GetWifiInterfaces();   // List<NetworkInterfaceMetrics> 或 null
            var list = new List<NetworkInterfaceMetrics>();
            double totalRx = 0, totalTx = 0;

            // PDH 的同名实例会带 _2/_3… 去重后缀（同一块网卡的多个虚拟接口），合并为一条
            var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (inst, h) in _rx)
            {
                var rx = _query.ReadDouble(h) ?? 0;
                var tx = 0.0;
                foreach (var (inst2, h2) in _tx)
                {
                    if (string.Equals(inst2, inst, StringComparison.OrdinalIgnoreCase)) { tx = _query.ReadDouble(h2) ?? 0; break; }
                }

                var key = NormalizeInstance(inst);
                if (byName.TryGetValue(key, out var idx))
                {
                    var ex = list[idx];
                    list[idx] = new NetworkInterfaceMetrics
                    {
                        Name = ex.Name,
                        Kind = ex.Kind,
                        DownloadBytesPerSec = ex.DownloadBytesPerSec + rx,
                        UploadBytesPerSec = ex.UploadBytesPerSec + tx,
                    };
                }
                else
                {
                    byName[key] = list.Count;
                    list.Add(new NetworkInterfaceMetrics
                    {
                        Name = inst,
                        Kind = InterfaceKind.Ethernet,
                        DownloadBytesPerSec = rx,
                        UploadBytesPerSec = tx,
                    });
                }
            }

            // Wi-Fi 接口（NativeWifi 描述名与 PDH 实例名可能不同；匹配失败时不重复添加，
            // 只把 WLAN 信息（SSID/信号/信道）通过“最相近名称”合到既有 PDH 行上）
            if (wifi != null)
            {
                bool wifiMerged = false;
                foreach (var w in wifi)
                {
                    // 只合并第一个 Wi-Fi 接口（Wi-Fi Direct 虚拟接口会重复报同一物理卡，导致接口表出现两行）
                    if (wifiMerged) break;
                    // PDH 里 Wi-Fi 实例名一般是 "MediaTek Wi-Fi 7 MT7925 ..."，与驱动描述一致或近似
                    var match = list.FirstOrDefault(x => ContainsEither(x.Name, w.Name));
                    if (match != null)
                    {
                        // 覆盖为 Wi-Fi 类型并补充 WLAN 信息
                        var idx = list.IndexOf(match);
                        var merged = new NetworkInterfaceMetrics
                        {
                            Name = w.Name,
                            Kind = InterfaceKind.Wifi,
                            IsConnected = w.IsConnected,
                            Ssid = w.Ssid,
                            SignalPercent = w.SignalPercent,
                            Channel = w.Channel,
                            PhyType = w.PhyType,
                            RxLinkKbps = w.RxLinkKbps,
                            TxLinkKbps = w.TxLinkKbps,
                            RxBytes = w.RxBytes,
                            TxBytes = w.TxBytes,
                            StateText = w.StateText,
                            DownloadBytesPerSec = match.DownloadBytesPerSec,
                            UploadBytesPerSec = match.UploadBytesPerSec,
                        };
                        list[idx] = merged;
                    }
                    else if (list.Count == 0)
                    {
                        // PDH 完全没有接口行（极罕见）：帧差分兜底
                        double rxRate = 0, txRate = 0;
                        if (_lastWifiFrames.TryGetValue(w.Name, out var prev) && (DateTime.UtcNow - prev.T).TotalSeconds > 0.2)
                        {
                            var dt = (DateTime.UtcNow - prev.T).TotalSeconds;
                            rxRate = Math.Max(0, (double)(w.RxBytes - prev.Rx) / dt);
                            txRate = Math.Max(0, (double)(w.TxBytes - prev.Tx) / dt);
                        }
                        list.Add(new NetworkInterfaceMetrics
                        {
                            Name = w.Name, Kind = InterfaceKind.Wifi, IsConnected = w.IsConnected, Ssid = w.Ssid,
                            SignalPercent = w.SignalPercent, Channel = w.Channel, PhyType = w.PhyType,
                            RxLinkKbps = w.RxLinkKbps, TxLinkKbps = w.TxLinkKbps, RxBytes = w.RxBytes, TxBytes = w.TxBytes,
                            StateText = w.StateText,
                            DownloadBytesPerSec = rxRate, UploadBytesPerSec = txRate,
                        });
                    }
                    _lastWifiFrames[w.Name] = (w.RxBytes, w.TxBytes, DateTime.UtcNow);
                    wifiMerged = true;
                }
            }

            foreach (var i in list)
                if (!IsNoise(i.Name)) { totalRx += i.DownloadBytesPerSec; totalTx += i.UploadBytesPerSec; }

            _lastRx = totalRx; _lastTx = totalTx; _lastTime = DateTime.UtcNow;
            return new NetworkMetrics
            {
                DownloadBytesPerSec = totalRx,
                UploadBytesPerSec = totalTx,
                Interfaces = list,
                Source = MetricSource.Pdh,
            };
        }
        catch
        {
            return Last(Array.Empty<NetworkInterfaceMetrics>());
        }
    }

    /// <summary>去掉 PDH 实例去重后缀（"Card_2" 或 "Card _2"），用于合并同一网卡的多个虚拟接口。</summary>
    private static string NormalizeInstance(string name)
    {
        var s = name.TrimEnd();
        int i = s.Length;
        while (i > 1 && char.IsDigit(s[i - 1])) i--;
        if (i > 1 && i < s.Length && s[i - 1] == '_')
            return s[..(i - 1)].TrimEnd();   // 去掉 "_2"，顺带去掉前面的空格
        return s;
    }

    private static bool ContainsEither(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        return a.Contains(b, StringComparison.OrdinalIgnoreCase) || b.Contains(a, StringComparison.OrdinalIgnoreCase)
            || Norm(a) == Norm(b);
    }

    private static string Norm(string s) => s.Replace(" ", "").Replace("-", "").Replace("_", "").ToLowerInvariant();

    private NetworkMetrics Last(IReadOnlyList<NetworkInterfaceMetrics> ifaces) =>
        new() { DownloadBytesPerSec = _lastRx, UploadBytesPerSec = _lastTx, Interfaces = ifaces, Source = MetricSource.None };

    public void Dispose()
    {
        try { _query?.Dispose(); } catch { }
        try { _curveQuery?.Dispose(); } catch { }
        _query = null;
        _curveQuery = null;
    }
}
