using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LiteMon.Core;
using LiteMon.Core.Models;
using LiteMon.Wpf.Controls;

namespace LiteMon.Wpf.Views.Pages;

/// <summary>RAM 页：摘要 + 占用曲线 + 物理内存条 + 按内存排序的应用列表 + 结束进程浮岛。</summary>
public class MemoryPage : BasePage
{
    private readonly TextBlock _vUsed, _vTotal, _vCommit;
    private readonly TextBlock _sUsed, _sTotal, _sCommit;
    private readonly Border _memBarTrack;
    private readonly Border _memBarFill;
    private readonly TextBlock _memDetail;
    private readonly AppListPanel _appList;
    private readonly MiniChart _chart;
    private readonly TextBlock _chartHint;
    private DateTime _lastListRefresh = DateTime.MinValue;

    public MemoryPage()
    {
        var root = new DockPanel();

        var stats = new WrapPanel { Margin = new Thickness(20, 16, 20, 6) };
        var cards = new[]
        {
            StatCard(Services.Loc.T("Mem.Title"), out _vUsed, out _sUsed, B("Brush.Ram")),
            StatCard(Services.Loc.T("Mem.UsedTotal"), out _vTotal, out _sTotal),
            StatCard(Services.Loc.T("Mem.Commit"), out _vCommit, out _sCommit),
        };
        foreach (var c in cards)
        {
            c.MinWidth = 150;
            c.Margin = new Thickness(0, 0, 14, 12);
            stats.Children.Add(c);
        }
        DockPanel.SetDock(stats, Dock.Top);
        root.Children.Add(stats);

        // 整页滚动（修复此前列表下半部分被裁剪无法滚动的问题）
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
            Text = Services.Loc.T("Mem.CurveTitle"),
            FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = B("Brush.Text"), FontFamily = F(),
            Margin = new Thickness(2, 0, 0, 8),
        };
        DockPanel.SetDock(chartTitle, Dock.Top);
        cp.Children.Add(chartTitle);
        _chart = new MiniChart
        {
            Height = 90,
            SeriesBrush = B("Brush.Ram"),
            FillBrush = ThemeBrush.GetAlpha("Brush.Ram", 0.18),
            DashBrush = ThemeBrush.GetAlpha("Brush.Accent2", 0.95),   // 已提交（虚线，亮青色醒目）
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

        // 物理内存条
        var barCard = new Border { Style = (Style)TryFindResource("Card"), Padding = new Thickness(14, 10, 14, 12), Margin = new Thickness(0, 0, 0, 14) };
        var bp = new DockPanel();
        var bt = new TextBlock { Text = Services.Loc.T("Mem.Physical"), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = B("Brush.Text"), FontFamily = F(), Margin = new Thickness(2, 0, 0, 8) };
        DockPanel.SetDock(bt, Dock.Top);
        bp.Children.Add(bt);
        var barStack = new StackPanel();
        _memBarTrack = new Border
        {
            Height = 12, CornerRadius = new CornerRadius(6), Background = B("Brush.Track"), ClipToBounds = true,
            Child = _memBarFill = new Border
            {
                Height = 12, CornerRadius = new CornerRadius(6), Width = 2,
                HorizontalAlignment = HorizontalAlignment.Left, Background = B("Brush.Ram"),
            },
        };
        barStack.Children.Add(_memBarTrack);
        _memDetail = new TextBlock { FontSize = 11, Foreground = B("Brush.TextSub"), Margin = new Thickness(0, 8, 0, 0), FontFamily = F() };
        barStack.Children.Add(_memDetail);
        bp.Children.Add(barStack);
        barCard.Child = bp;
        body.Children.Add(barCard);

        // 应用列表
        _appList = new AppListPanel(new[]
        {
            new AppListPanel.ColDef { Header = Services.Loc.T("Common.App"), Width = 3.2, IsName = true },
            new AppListPanel.ColDef { Header = Services.Loc.T("Common.Memory"), Width = 1.2 },
            new AppListPanel.ColDef { Header = Services.Loc.T("Mem.Private"), Width = 1.2 },
        }, emptyHint: Services.Loc.T("List.NoProcess"));
        body.Children.Add(_appList);

        root.Children.Add(scroll);
        Content = root;
    }

    /// <summary>RAM 页的"结束进程"目标列表。</summary>
    public override LiteMon.Wpf.Controls.AppListPanel? KillableList => _appList;

    private readonly double[] _curveBuf = new double[Core.Scheduling.CurveHistory.Capacity];
    private readonly double[] _curveBuf2 = new double[Core.Scheduling.CurveHistory.Capacity];
    private DateTime _lastHintUpdate = DateTime.MinValue;

    /// <summary>曲线 0.1s 重绘（实线物理 / 虚线已提交）；数字 0.5s 更新。</summary>
    public override void UpdateChart(Core.Scheduling.MonitorService monitor)
    {
        int n = monitor.Curves.CopyRam(_curveBuf);
        _chart.SetData(_curveBuf, n);
        int n2 = monitor.Curves.CopyRamCommit(_curveBuf2);
        _chart.SetData2(n2 >= 2 ? _curveBuf2 : null, n2);
        if ((DateTime.UtcNow - _lastHintUpdate).TotalSeconds >= 0.5)
        {
            _lastHintUpdate = DateTime.UtcNow;
            _chartHint.Text = Services.Loc.T("Mem.Hint", monitor.Curves.LatestRam.ToString("0.0"), monitor.Curves.LatestRamCommit.ToString("0.0"));
        }
    }

    public override void Apply(Snapshot s)
    {
        var m = s.Memory;
        _vUsed.Text = m.UsagePercent.ToString("0") + "%";
        _sUsed.Text = Formatting.FormatBytes(m.UsedBytes) + " / " + Formatting.FormatBytes(m.TotalBytes);

        _vTotal.Text = Formatting.FormatBytes(m.UsedBytes);
        _sTotal.Text = Services.Loc.T("Mem.TotalSub", Formatting.FormatBytes(m.TotalBytes));
        _vCommit.Text = Formatting.FormatBytes(m.CommitUsedBytes);
        _sCommit.Text = Services.Loc.T("Mem.CommitLimit", Formatting.FormatBytes(m.CommitTotalBytes));

        // 曲线由 UpdateChart 高频驱动（0.1s），此处不再推点

        _memDetail.Text = Services.Loc.T("Mem.Detail",
            Formatting.FormatBytes(m.UsedBytes), Formatting.FormatBytes(m.TotalBytes),
            Formatting.FormatBytes(m.CommitUsedBytes), Formatting.FormatBytes(m.CommitTotalBytes));
        _memBarFill.Width = Math.Max(2, m.UsagePercent / 100.0 * Math.Max(60, _memBarTrack.ActualWidth));

        if ((DateTime.Now - _lastListRefresh).TotalSeconds >= 2)
        {
            _lastListRefresh = DateTime.Now;
            var top = s.Processes
                .OrderByDescending(p => p.WorkingSetBytes)
                .Take(30)
                .Select(p => new AppListPanel.RowData
                {
                    Pid = p.Pid,
                    Cells = new string?[]
                    {
                        p.Name + "  (" + p.Pid + ")",
                        Formatting.FormatBytes(p.WorkingSetBytes),
                        Formatting.FormatBytes(p.PrivateBytes),
                    },
                })
                .ToList();
            _appList.Update(top);
        }
    }

    private static Brush B(string key) => ThemeBrush.Get(key);
    private static FontFamily F() => (FontFamily)Application.Current.Resources["Font.Main"];
}
