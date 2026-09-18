using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LiteMon.Core;
using LiteMon.Core.Models;
using LiteMon.Wpf.Controls;

namespace LiteMon.Wpf.Views.Pages;

/// <summary>CPU 页：摘要 + 占用曲线 + 每核长条（全展开）+ 按 CPU 排序的应用列表 + 结束进程浮岛。</summary>
public class CpuPage : BasePage
{
    private readonly TextBlock _vUsage, _vFreq, _vTemp, _vPower, _vProc;
    private readonly TextBlock _sUsage, _sFreq, _sTemp, _sPower, _sProc;
    private readonly Grid _coreGrid;
    private Border[]? _coreTracks;
    private Border[]? _coreFills;
    private TextBlock[]? _coreLabels;
    private readonly AppListPanel _appList;
    private readonly MiniChart _chart;
    private readonly TextBlock _chartHint;
    private DateTime _lastListRefresh = DateTime.MinValue;
    private double _lastFreqGHz;

    public CpuPage()
    {
        var root = new DockPanel();

        // ---- 摘要卡片行 ----
        var stats = new WrapPanel { Margin = new Thickness(20, 16, 20, 6) };
        var cards = new[]
        {
            StatCard(Services.Loc.T("Cpu.Title"), out _vUsage, out _sUsage, B("Brush.Cpu")),
            StatCard(Services.Loc.T("Cpu.Speed"), out _vFreq, out _sFreq),
            StatCard(Services.Loc.T("Cpu.Temp"), out _vTemp, out _sTemp, B("Brush.Orange")),
            StatCard(Services.Loc.T("Cpu.Power"), out _vPower, out _sPower),
            StatCard(Services.Loc.T("Cpu.Procs"), out _vProc, out _sProc),
        };
        foreach (var c in cards)
        {
            c.MinWidth = 150;
            c.Margin = new Thickness(0, 0, 14, 12);
            stats.Children.Add(c);
        }
        DockPanel.SetDock(stats, Dock.Top);
        root.Children.Add(stats);

        // ---- 主体（整页滚动）+ 浮岛覆盖层 ----
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        SlowScroll.Attach(scroll);   // 滚轮速度 = 默认的 80%
        var body = new StackPanel { Margin = new Thickness(20, 8, 20, 20) };
        scroll.Content = body;

        // 占用曲线
        var chartCard = new Border
        {
            Style = (Style)TryFindResource("Card"),
            Padding = new Thickness(12, 10, 12, 12),
            Margin = new Thickness(0, 0, 0, 14),
        };
        var cp = new DockPanel();
        var chartTitle = new TextBlock
        {
            Text = Services.Loc.T("Cpu.CurveTitle"),
            FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = B("Brush.Text"), FontFamily = F(),
            Margin = new Thickness(2, 0, 0, 8),
        };
        DockPanel.SetDock(chartTitle, Dock.Top);
        cp.Children.Add(chartTitle);
        _chart = new MiniChart
        {
            Height = 90,
            SeriesBrush = B("Brush.Cpu"),
            FillBrush = ThemeBrush.GetAlpha("Brush.Cpu", 0.18),
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

        // 每核占用
        var coreCard = new Border { Style = (Style)TryFindResource("Card"), Padding = new Thickness(12, 10, 12, 12), Margin = new Thickness(0, 0, 0, 14) };
        var corePanel = new DockPanel();
        var coreTitle = new TextBlock
        {
            Text = Services.Loc.T("Cpu.PerCore"),
            FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = B("Brush.Text"), FontFamily = F(),
            Margin = new Thickness(2, 0, 0, 8),
        };
        DockPanel.SetDock(coreTitle, Dock.Top);
        corePanel.Children.Add(coreTitle);

        _coreGrid = new Grid();
        for (int c = 0; c < 3; c++)
            _coreGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        corePanel.Children.Add(_coreGrid);
        coreCard.Child = corePanel;
        body.Children.Add(coreCard);

        // 应用列表
        _appList = new AppListPanel(new[]
        {
            new AppListPanel.ColDef { Header = Services.Loc.T("Common.App"), Width = 3.4, IsName = true },
            new AppListPanel.ColDef { Header = "CPU", Width = 1.0 },
        }, emptyHint: Services.Loc.T("List.NoProcess"));
        body.Children.Add(_appList);

        root.Children.Add(scroll);
        Content = root;
    }

    /// <summary>CPU 页的"结束进程"目标列表。</summary>
    public override LiteMon.Wpf.Controls.AppListPanel? KillableList => _appList;

    private readonly double[] _curveBuf = new double[Core.Scheduling.CurveHistory.Capacity];
    private DateTime _lastHintUpdate = DateTime.MinValue;

    /// <summary>0.1s 高频曲线刷新：曲线每 0.1s 重绘，百分比数字每 0.5s 更新一次（数字跳动太频看不清）。</summary>
    public override void UpdateChart(Core.Scheduling.MonitorService monitor)
    {
        int n = monitor.Curves.CopyCpu(_curveBuf);
        _chart.SetData(_curveBuf, n);
        if ((DateTime.UtcNow - _lastHintUpdate).TotalSeconds >= 0.5)
        {
            _lastHintUpdate = DateTime.UtcNow;
            _chartHint.Text = monitor.Curves.LatestCpu.ToString("0.0") + "%";
        }
    }

    public override void Apply(Snapshot s)
    {
        _vUsage.Text = s.Cpu.TotalUsage.ToString("0") + "%";
        _sUsage.Text = s.Cpu.Name;

        if (s.Cpu.FrequencyMHz is > 0)
        {
            _lastFreqGHz = s.Cpu.FrequencyMHz.Value / 1000.0;
            _vFreq.Text = _lastFreqGHz.ToString("0.00") + " GHz";
        }
        else
        {
            _vFreq.Text = _lastFreqGHz > 0 ? _lastFreqGHz.ToString("0.00") + " GHz" : "N/A";
        }
        _sFreq.Text = Services.Loc.T("Cpu.LogicalCores", s.Cpu.LogicalCores);

        _vTemp.Text = s.Cpu.TemperatureC is > 0 ? s.Cpu.TemperatureC.Value.ToString("0") + " ℃" : "N/A";
        _sTemp.Text = s.Cpu.TemperatureC is > 0 ? "" : Services.Loc.T("Cpu.NoTempSensor");

        _vPower.Text = s.Cpu.PowerWatts is > 0 ? s.Cpu.PowerWatts.Value.ToString("0.0") + " W" : "N/A";
        _sPower.Text = s.Cpu.PowerWatts is > 0 ? "" : Services.Loc.T("Cpu.NoPowerSensor");

        _vProc.Text = s.Processes.Count > 0 ? s.Processes.Count.ToString() : "…";
        _sProc.Text = Services.Loc.T("Cpu.RunningApps");

        // 曲线由 UpdateChart 高频驱动（0.1s），此处不再推点

        // 每核
        var perCore = s.Cpu.PerCoreUsage;
        if (perCore.Count > 0 && (_coreTracks == null || _coreTracks.Length != perCore.Count))
            RebuildCores(perCore.Count);
        if (_coreTracks != null)
        {
            for (int i = 0; i < _coreTracks.Length && i < perCore.Count; i++)
            {
                var v = perCore[i];
                var track = _coreTracks[i];
                var fill = _coreFills![i];
                var w = track.ActualWidth > 0 ? track.ActualWidth : 160;
                fill.Width = Math.Max(2, v / 100.0 * w);
                fill.Background = v > 85 ? B("Brush.Red") : v > 60 ? B("Brush.Orange") : B("Brush.Cpu");
                _coreLabels![i].Text = "CPU " + (i + 1) + "  ·  " + v.ToString("0") + "%";
            }
        }

        // 应用列表（节流 2s）
        if ((DateTime.Now - _lastListRefresh).TotalSeconds >= 2)
        {
            _lastListRefresh = DateTime.Now;
            var top = s.Processes
                .OrderByDescending(p => p.CpuPercent)
                .Take(30)
                .Select(p => new AppListPanel.RowData
                {
                    Pid = p.Pid,
                    Cells = new string?[] { p.Name + "  (" + p.Pid + ")", p.CpuPercent > 0 ? p.CpuPercent.ToString("0.0") + "%" : "—" },
                })
                .ToList();
            _appList.Update(top);
        }
    }

    private void RebuildCores(int count)
    {
        _coreGrid.Children.Clear();
        _coreGrid.RowDefinitions.Clear();
        _coreTracks = new Border[count];
        _coreFills = new Border[count];
        _coreLabels = new TextBlock[count];

        int cols = 3;
        int rows = (int)Math.Ceiling(count / (double)cols);
        for (int r = 0; r < rows; r++)
            _coreGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });

        for (int i = 0; i < count; i++)
        {
            var cell = new StackPanel { Margin = new Thickness(4, 5, 10, 5) };
            Grid.SetColumn(cell, i % cols);
            Grid.SetRow(cell, i / cols);

            var label = new TextBlock
            {
                Text = "CPU " + (i + 1),
                FontSize = 10.5,
                Foreground = B("Brush.TextSub"),
                Margin = new Thickness(1, 0, 0, 4),
                FontFamily = F(),
            };
            _coreLabels[i] = label;
            cell.Children.Add(label);

            var fill = new Border
            {
                Height = 8,
                CornerRadius = new CornerRadius(4),
                Width = 2,
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = B("Brush.Cpu"),
            };
            _coreFills[i] = fill;

            var track = new Border
            {
                Height = 8,
                CornerRadius = new CornerRadius(4),
                Background = B("Brush.Track"),
                Child = fill,
            };
            _coreTracks[i] = track;
            cell.Children.Add(track);

            _coreGrid.Children.Add(cell);
        }
    }

    private static Brush B(string key) => ThemeBrush.Get(key);
    private static FontFamily F() => (FontFamily)Application.Current.Resources["Font.Main"];
}
