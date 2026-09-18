using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LiteMon.Core;
using LiteMon.Core.Models;
using LiteMon.Wpf.Controls;

namespace LiteMon.Wpf.Views.Pages;

/// <summary>WIFI 页：摘要（SSID/下行/上行/信号/信道/协议）+ 信号条 + 各网络接口实时速率表。</summary>
public class WifiPage : BasePage
{
    private readonly TextBlock _vSsid, _vSignal, _vChannel, _vPhy, _vDown, _vUp;
    private readonly TextBlock _sSsid, _sSignal, _sChannel, _sPhy, _sDown, _sUp;
    private readonly Border _signalTrack, _signalFill;
    private readonly TextBlock _signalDetail;
    private readonly AppListPanel _ifaceList;
    private readonly AppListPanel _netAppList;
    private readonly MiniChart _chart;
    private readonly TextBlock _chartHint;
    private DateTime _lastListRefresh = DateTime.MinValue;
    private DateTime _lastNetAppRefresh = DateTime.MinValue;
    private DateTime _lastHintUpdate = DateTime.MinValue;
    private readonly double[] _curveBuf = new double[Core.Scheduling.CurveHistory.Capacity];
    private readonly double[] _curveBuf2 = new double[Core.Scheduling.CurveHistory.Capacity];

    public WifiPage()
    {
        var root = new DockPanel();

        // 六卡等宽一行（UniformGrid）：协议卡永不被挤到第二行
        var stats = new System.Windows.Controls.Primitives.UniformGrid
        {
            Columns = 6,
            Margin = new Thickness(20, 16, 20, 6),
        };
        var cardDefs = new[]
        {
            StatCard("SSID", out _vSsid, out _sSsid, B("Brush.Net"), 14),
            StatCard(Services.Loc.T("Wifi.Title"), out _vDown, out _sDown, B("Brush.Net")),
            StatCard(Services.Loc.T("Wifi.Title2"), out _vUp, out _sUp, B("Brush.Net")),
            StatCard(Services.Loc.T("Wifi.Signal"), out _vSignal, out _sSignal),
            StatCard(Services.Loc.T("Wifi.Channel"), out _vChannel, out _sChannel),
            // 协议名较长（"802.11ax (Wi-Fi 6)"），用小字号保证在 1/6 宽卡片里能完整显示
            StatCard(Services.Loc.T("Wifi.Protocol"), out _vPhy, out _sPhy, null, 13.5),
        };
        foreach (var c in cardDefs)
        {
            c.Margin = new Thickness(0, 0, 10, 12);
            stats.Children.Add(c);
        }
        DockPanel.SetDock(stats, Dock.Top);
        root.Children.Add(stats);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        SlowScroll.Attach(scroll);   // 滚轮速度 = 默认的 80%
        var body = new StackPanel { Margin = new Thickness(20, 8, 20, 20) };
        scroll.Content = body;

        // 流量曲线（近 60 秒 · 0.1s 采样 · 实线下行 / 虚线上行）
        var chartCard = new Border
        {
            Style = (Style)TryFindResource("Card"),
            Padding = new Thickness(12, 10, 12, 12),
            Margin = new Thickness(0, 0, 0, 14),
        };
        var cp = new DockPanel();
        var chartTitle = new TextBlock
        {
            Text = Services.Loc.T("Wifi.CurveTitle"),
            FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = B("Brush.Text"), FontFamily = F(),
            Margin = new Thickness(2, 0, 0, 8),
        };
        DockPanel.SetDock(chartTitle, Dock.Top);
        cp.Children.Add(chartTitle);
        _chart = new MiniChart
        {
            Height = 90,
            SeriesBrush = B("Brush.Net"),
            FillBrush = ThemeBrush.GetAlpha("Brush.Net", 0.18),
            DashBrush = ThemeBrush.GetAlpha("Brush.Accent", 0.95),   // 上行（虚线）
            DualScale = true,   // 下行/上行量级差百倍：副序列独立 y 轴自适应
        };
        _chartHint = new TextBlock
        {
            Text = "…", FontSize = 10.5, Foreground = B("Brush.TextDim"),
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 4, 0), FontFamily = F(),
        };
        var chartHost = new DockPanel();
        DockPanel.SetDock(_chartHint, Dock.Top);
        chartHost.Children.Add(_chartHint);
        chartHost.Children.Add(_chart);
        cp.Children.Add(chartHost);
        chartCard.Child = cp;
        body.Children.Add(chartCard);

        // 信号质量条
        var sigCard = new Border { Style = (Style)TryFindResource("Card"), Padding = new Thickness(14, 10, 14, 12), Margin = new Thickness(0, 0, 0, 14) };
        var sp = new DockPanel();
        var st = new TextBlock { Text = Services.Loc.T("Wifi.SignalQuality"), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = B("Brush.Text"), FontFamily = F(), Margin = new Thickness(2, 0, 0, 8) };
        DockPanel.SetDock(st, Dock.Top);
        sp.Children.Add(st);
        var sigStack = new StackPanel();
        _signalTrack = new Border
        {
            Height = 12, CornerRadius = new CornerRadius(6), Background = B("Brush.Track"), ClipToBounds = true,
            Child = _signalFill = new Border
            {
                Height = 12, CornerRadius = new CornerRadius(6), Width = 2,
                HorizontalAlignment = HorizontalAlignment.Left, Background = B("Brush.Net"),
            },
        };
        sigStack.Children.Add(_signalTrack);
        _signalDetail = new TextBlock { FontSize = 11, Foreground = B("Brush.TextSub"), Margin = new Thickness(0, 8, 0, 0), FontFamily = F() };
        sigStack.Children.Add(_signalDetail);
        sp.Children.Add(sigStack);
        sigCard.Child = sp;
        body.Children.Add(sigCard);

        // 接口速率表（与其他页面共用同一套表格控件，保证表头与数据列严格对齐）
        _ifaceList = new AppListPanel(new[]
        {
            new AppListPanel.ColDef { Header = Services.Loc.T("Wifi.IfaceCol"), Width = 3.0, IsName = true },
            new AppListPanel.ColDef { Header = Services.Loc.T("Wifi.Title"), Width = 1.2 },
            new AppListPanel.ColDef { Header = Services.Loc.T("Wifi.Title2"), Width = 1.2 },
        }, emptyHint: Services.Loc.T("Wifi.NoIfaceData"));
        body.Children.Add(_ifaceList);

        // 网络应用表（按带宽占用排序；estats 可用=真实 TCP 字节，否则 IO Data 估算）
        _netAppList = new AppListPanel(new[]
        {
            new AppListPanel.ColDef { Header = Services.Loc.T("Wifi.NetApps"), Width = 3.2, IsName = true },
            new AppListPanel.ColDef { Header = Services.Loc.T("Common.Download"), Width = 1.0, AlignRight = true },
            new AppListPanel.ColDef { Header = Services.Loc.T("Common.Upload"), Width = 1.0, AlignRight = true },
        }, emptyHint: Services.Loc.T("Wifi.NoNetAppData"));
        _netAppList.Margin = new Thickness(0, 14, 0, 0);
        body.Children.Add(_netAppList);

        root.Children.Add(scroll);
        Content = root;
    }

    /// <summary>网络页的"结束进程"目标列表（网络应用表）。</summary>
    public override AppListPanel? KillableList => _netAppList;

    /// <summary>流量曲线 0.1s 重绘（实线下行 / 虚线上行）；数字 0.5s 更新。</summary>
    public override void UpdateChart(Core.Scheduling.MonitorService monitor)
    {
        int n = monitor.Curves.CopyNetRx(_curveBuf);
        _chart.SetData(_curveBuf, n);
        int n2 = monitor.Curves.CopyNetTx(_curveBuf2);
        _chart.SetData2(n2 >= 2 ? _curveBuf2 : null, n2);
        if ((DateTime.UtcNow - _lastHintUpdate).TotalSeconds >= 0.5)
        {
            _lastHintUpdate = DateTime.UtcNow;
            _chartHint.Text = "↓ " + Formatting.FormatBytes(monitor.Curves.LatestNetRx) + "/s · ↑ "
                + Formatting.FormatBytes(monitor.Curves.LatestNetTx) + "/s";
        }
    }

    public override void Apply(Snapshot s)
    {
        var ifaces = s.Network.Interfaces;
        var wifi = ifaces.FirstOrDefault(i => i.Kind == InterfaceKind.Wifi);

        // 摘要卡
        if (wifi != null)
        {
            var state = TrState(wifi.StateText);
            _vSsid.Text = string.IsNullOrEmpty(wifi.Ssid) ? state : wifi.Ssid;
            _sSsid.Text = wifi.IsConnected ? Services.Loc.T("Wifi.Connected") : state;
            _vSignal.Text = wifi.IsConnected ? (wifi.SignalPercent > 0 ? wifi.SignalPercent.ToString() + "%" : "N/A") : "—";
            _sSignal.Text = wifi.IsConnected ? "" : Services.Loc.T("Wifi.Disconnected");
            _vChannel.Text = wifi.IsConnected && wifi.Channel > 0 ? wifi.Channel.ToString() : "—";
            _sChannel.Text = wifi.IsConnected && wifi.PhyType.Length > 0 ? wifi.PhyType : "";
            _vPhy.Text = wifi.IsConnected && wifi.PhyType.Length > 0 ? wifi.PhyType : "—";
            _sPhy.Text = wifi.IsConnected && wifi.RxLinkKbps > 0
                ? Services.Loc.T("Wifi.LinkRate", FormatKbps(wifi.RxLinkKbps), FormatKbps(wifi.TxLinkKbps))
                : "";

            _signalFill.Width = Math.Max(2, wifi.SignalPercent / 100.0 * Math.Max(60, _signalTrack.ActualWidth));
            _signalDetail.Text = wifi.IsConnected
                ? Services.Loc.T("Wifi.SignalDetail", wifi.SignalPercent.ToString(), Services.Loc.T(
                    wifi.SignalPercent >= 80 ? "Wifi.SignalExcellent" : wifi.SignalPercent >= 60 ? "Wifi.SignalGood" : wifi.SignalPercent >= 40 ? "Wifi.SignalFair" : "Wifi.SignalPoor"))
                : Services.Loc.T("Wifi.NotConnected");
        }
        else
        {
            _vSsid.Text = Services.Loc.T("Wifi.NoWifi");
            _sSsid.Text = Services.Loc.T("Wifi.NoAdapter");
            _vSignal.Text = "—";
            _sSignal.Text = "";
            _vChannel.Text = "—";
            _sChannel.Text = "";
            _vPhy.Text = "—";
            _sPhy.Text = "";
            _signalFill.Width = 2;
            _signalDetail.Text = Services.Loc.T("Wifi.NoInterface");
        }

        _vDown.Text = Formatting.FormatBytes(s.Network.DownloadBytesPerSec) + "/s";
        _vUp.Text = Formatting.FormatBytes(s.Network.UploadBytesPerSec) + "/s";
        _sDown.Text = Services.Loc.T("Wifi.AllIfaces");
        _sUp.Text = Services.Loc.T("Wifi.AllIfaces");

        // 接口速率表（节流 1s，按实时速率排序）
        if ((DateTime.Now - _lastListRefresh).TotalSeconds >= 1)
        {
            _lastListRefresh = DateTime.Now;
            var rows = ifaces
                .Where(i => !IsNoise(i.Name))
                .OrderByDescending(i => i.DownloadBytesPerSec + i.UploadBytesPerSec)
                .Take(12)
                .Select(i => new AppListPanel.RowData
                {
                    Pid = 0,
                    Cells = new string?[]
                    {
                        (i.Kind == InterfaceKind.Wifi ? "📶 " : "🔌 ") + i.Name +
                            (i.Kind == InterfaceKind.Wifi && !string.IsNullOrEmpty(i.Ssid) ? "  (" + i.Ssid + ")" : ""),
                        Formatting.FormatBytes(i.DownloadBytesPerSec) + "/s",
                        Formatting.FormatBytes(i.UploadBytesPerSec) + "/s",
                    },
                })
                .ToList();
            _ifaceList.Update(rows);
        }

        // 网络应用表（节流 2s；按 ↓+↑ 带宽排序，名称从进程快照回填）
        if ((DateTime.Now - _lastNetAppRefresh).TotalSeconds >= 2)
        {
            _lastNetAppRefresh = DateTime.Now;
            var nameByPid = new Dictionary<int, string>(s.Processes.Count);
            foreach (var p in s.Processes)
                nameByPid.TryAdd(p.Pid, p.Name);

            // 口径提示：estats 不可用时为 IO Data 估算（含磁盘 IO），列头切换注明
            _netAppList.SetHeaderText(1, s.NetworkProcessApproximate ? Services.Loc.T("Wifi.IoRate") : Services.Loc.T("Common.Download"));
            _netAppList.SetHeaderText(2, s.NetworkProcessApproximate ? Services.Loc.T("Wifi.Approx") : Services.Loc.T("Common.Upload"));

            var netRows = s.NetworkProcesses
                .Where(n => n.RxBytesPerSec > 1 || n.TxBytesPerSec > 1)
                .OrderByDescending(n => n.RxBytesPerSec + n.TxBytesPerSec)
                .Take(15)
                .Select(n => new AppListPanel.RowData
                {
                    Pid = n.Pid,
                    Cells = new string?[]
                    {
                        (nameByPid.TryGetValue(n.Pid, out var nm) ? nm : "PID " + n.Pid) + "  (" + n.Pid + ")",
                        Formatting.FormatBytes(n.RxBytesPerSec) + "/s",
                        Formatting.FormatBytes(n.TxBytesPerSec) + "/s",
                    },
                })
                .ToList();
            _netAppList.Update(netRows);
        }
    }

    private static bool IsNoise(string n) => LiteMon.Core.Collectors.NetworkCollector.IsNoise(n);

    /// <summary>WlanInterop 状态码（connected/disconnected/…）→ 本地化文案。</summary>
    private static string TrState(string code) => code switch
    {
        "connected" => Services.Loc.T("Wifi.Connected"),
        "disconnected" => Services.Loc.T("Wifi.Disconnected"),
        "disconnecting" => Services.Loc.T("Wifi.State.Disconnecting"),
        "associating" => Services.Loc.T("Wifi.State.Associating"),
        "discovering" => Services.Loc.T("Wifi.State.Discovering"),
        "authenticating" => Services.Loc.T("Wifi.State.Authenticating"),
        "not-ready" => Services.Loc.T("Wifi.State.NotReady"),
        _ => code,
    };

    private static string FormatKbps(uint kbps) => kbps >= 1000
        ? (kbps / 1000.0).ToString("0.#") + " Mbps"
        : kbps + " Kbps";

    private static Brush B(string key) => ThemeBrush.Get(key);
    private static FontFamily F() => (FontFamily)Application.Current.Resources["Font.Main"];
}
