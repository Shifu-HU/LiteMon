using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LiteMon.Core;
using LiteMon.Core.Models;
using LiteMon.Wpf.Controls;

namespace LiteMon.Wpf.Views.Pages;

/// <summary>磁盘页：磁盘卡（用量/速率/活动时间）+ 正在读写磁盘的应用表。letter=null 表示所有固定盘。</summary>
public class DiskPage : BasePage
{
    private readonly string? _letter;
    private readonly List<(Border Track, Border Fill, TextBlock Pct, TextBlock Sub, TextBlock Read, TextBlock Write)> _bars = new();
    private WrapPanel _cards;
    private TextBlock _titleText;
    private readonly AppListPanel _procList;
    private readonly MiniChart _chart;
    private readonly TextBlock _chartHint;
    private DateTime _lastListRefresh = DateTime.MinValue;
    private DateTime _lastHintUpdate = DateTime.MinValue;
    private readonly double[] _curveBuf = new double[Core.Scheduling.CurveHistory.Capacity];
    private readonly double[] _curveBuf2 = new double[Core.Scheduling.CurveHistory.Capacity];

    public DiskPage(string? letter)
    {
        _letter = letter;
        var root = new DockPanel();

        _titleText = new TextBlock
        {
            FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = B("Brush.Text"), FontFamily = F(),
            Margin = new Thickness(22, 16, 0, 12),
        };
        DockPanel.SetDock(_titleText, Dock.Top);
        root.Children.Add(_titleText);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        SlowScroll.Attach(scroll);   // 滚轮速度 = 默认的 80%
        var body = new StackPanel { Margin = new Thickness(20, 0, 20, 20) };
        scroll.Content = body;

        _cards = new WrapPanel();
        body.Children.Add(_cards);

        // 读写曲线（近 60 秒 · 0.1s 采样 · 实线读取 / 虚线写入）——全盘合计
        var chartCard = new Border
        {
            Style = (Style)TryFindResource("Card"),
            Padding = new Thickness(12, 10, 12, 12),
            Margin = new Thickness(0, 14, 0, 0),
        };
        var cp = new DockPanel();
        var chartTitle = new TextBlock
        {
            Text = Services.Loc.T("Disk.CurveTitle"),
            FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = B("Brush.Text"), FontFamily = F(),
            Margin = new Thickness(2, 0, 0, 8),
        };
        DockPanel.SetDock(chartTitle, Dock.Top);
        cp.Children.Add(chartTitle);
        _chart = new MiniChart
        {
            Height = 90,
            SeriesBrush = B("Brush.Accent"),
            FillBrush = ThemeBrush.GetAlpha("Brush.Accent", 0.15),
            DashBrush = ThemeBrush.GetAlpha("Brush.Orange", 0.95),   // 写入（虚线，橙色与读的蓝色区分）
            DualScale = true,   // 读/写速率量级可能差很大：副序列独立 y 轴自适应
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

        // 正在读写磁盘的应用（IO 速率）
        _procList = new AppListPanel(new[]
        {
            new AppListPanel.ColDef { Header = Services.Loc.T("Common.App"), Width = 3.0, IsName = true },
            new AppListPanel.ColDef { Header = Services.Loc.T("Disk.Read"), Width = 1.2 },
            new AppListPanel.ColDef { Header = Services.Loc.T("Disk.Write"), Width = 1.2 },
        }, emptyHint: Services.Loc.T("Disk.NoProcData"));
        _procList.Margin = new Thickness(0, 14, 0, 0);
        body.Children.Add(_procList);

        root.Children.Add(scroll);
        Content = root;
    }

    /// <summary>磁盘页的"结束进程"目标列表。</summary>
    public override LiteMon.Wpf.Controls.AppListPanel? KillableList => _procList;

    /// <summary>读写曲线 0.1s 重绘（实线读 / 虚线写 · 全盘合计）；数字 0.5s 更新。</summary>
    public override void UpdateChart(Core.Scheduling.MonitorService monitor)
    {
        int n = monitor.Curves.CopyDiskR(_curveBuf);
        _chart.SetData(_curveBuf, n);
        int n2 = monitor.Curves.CopyDiskW(_curveBuf2);
        _chart.SetData2(n2 >= 2 ? _curveBuf2 : null, n2);
        if ((DateTime.UtcNow - _lastHintUpdate).TotalSeconds >= 0.5)
        {
            _lastHintUpdate = DateTime.UtcNow;
            _chartHint.Text = Services.Loc.T("Disk.Hint", Formatting.FormatBytes(monitor.Curves.LatestDiskR), Formatting.FormatBytes(monitor.Curves.LatestDiskW));
        }
    }

    public override void Apply(Snapshot s)
    {
        var disks = _letter == null
            ? s.Disks.Where(d => d.Kind == DiskKind.Fixed).ToList()
            : s.Disks.Where(d => string.Equals(d.Letter, _letter, StringComparison.OrdinalIgnoreCase)).ToList();

        _titleText.Text = _letter == null
            ? Services.Loc.T("Disk.FixedCount", disks.Count)
            : Services.Loc.T("Disk.RemovableTitle", disks.FirstOrDefault()?.Letter ?? "");

        if (_bars.Count != disks.Count) Rebuild(disks);

        for (int i = 0; i < disks.Count && i < _bars.Count; i++)
        {
            var d = disks[i];
            var b = _bars[i];
            b.Pct.Text = d.UsagePercent.ToString("0") + "%";
            b.Sub.Text = Formatting.FormatBytes(d.UsedBytes) + " / " + Formatting.FormatBytes(d.TotalBytes) + " · " + Services.Loc.T("Disk.Free", Formatting.FormatBytes(d.FreeBytes));
            b.Fill.Width = Math.Max(2, d.UsagePercent / 100.0 * Math.Max(80, b.Track.ActualWidth));
            b.Fill.Background = d.UsagePercent > 90 ? B("Brush.Red") : B("Brush.Accent");
            b.Read.Text = "↓ " + Formatting.FormatBytes(d.ReadBytesPerSec) + "/s";
            b.Write.Text = "↑ " + Formatting.FormatBytes(d.WriteBytesPerSec) + "/s";
            // 活动时间（% Idle Time）已隐藏：该计数器在本机长期恒为 0，
            // 会把活动时间永远显示成 100%，误导性强于参考价值。数据仍在 DiskMetrics 里保留。
        }

        // 正在读写磁盘的应用（节流 2s）
        if ((DateTime.Now - _lastListRefresh).TotalSeconds >= 2)
        {
            _lastListRefresh = DateTime.Now;
            var top = s.DiskProcesses
                .Where(p => p.Pid > 0)
                .OrderByDescending(p => p.ReadBytesPerSec + p.WriteBytesPerSec)
                .Take(20)
                .Select(p => new AppListPanel.RowData
                {
                    Pid = p.Pid,
                    Cells = new string?[]
                    {
                        p.Name + "  (" + p.Pid + ")",
                        p.ReadBytesPerSec > 0 ? Formatting.FormatBytes(p.ReadBytesPerSec) + "/s" : "—",
                        p.WriteBytesPerSec > 0 ? Formatting.FormatBytes(p.WriteBytesPerSec) + "/s" : "—",
                    },
                })
                .ToList();
            _procList.Update(top);
        }
    }

    private void Rebuild(List<DiskMetrics> disks)
    {
        _cards.Children.Clear();
        _bars.Clear();

        foreach (var d in disks)
        {
            var card = new Border { Style = (Style)TryFindResource("Card"), Padding = new Thickness(16, 12, 16, 14), Margin = new Thickness(0, 0, 14, 14), MinWidth = 330, MaxWidth = 500 };
            var sp = new StackPanel();

            var head = new DockPanel();
            var name = new TextBlock
            {
                Text = d.Letter + "  " + (string.IsNullOrEmpty(d.Label) ? Services.Loc.T("Disk.LocalDisk") : d.Label),
                FontSize = 14, FontWeight = FontWeights.SemiBold,
                Foreground = B("Brush.Text"), FontFamily = F(),
            };
            head.Children.Add(name);
            var chip = new Border
            {
                CornerRadius = new CornerRadius(6),
                Background = d.Kind == DiskKind.Removable ? AB("Brush.Orange", 0.2) : AB("Brush.Accent", 0.16),
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var chipTxt = new TextBlock
            {
                Text = d.Kind == DiskKind.Removable ? Services.Loc.T("Disk.Removable") : Services.Loc.T("Disk.Fixed"),
                FontSize = 10, FontFamily = F(),
                Foreground = d.Kind == DiskKind.Removable ? B("Brush.Orange") : B("Brush.Accent"),
            };
            chip.Child = chipTxt;
            head.Children.Add(chip);
            sp.Children.Add(head);

            var fill = new Border
            {
                Height = 10, CornerRadius = new CornerRadius(5), Width = 2,
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = B("Brush.Accent"),
            };
            var track = new Border
            {
                Height = 10, CornerRadius = new CornerRadius(5), Background = B("Brush.Track"),
                ClipToBounds = true, Margin = new Thickness(0, 10, 0, 0),
                Child = fill,
            };
            sp.Children.Add(track);

            var pct = new TextBlock { FontSize = 18, FontWeight = FontWeights.Bold, Foreground = B("Brush.Accent"), FontFamily = F(), Margin = new Thickness(0, 6, 0, 0) };
            sp.Children.Add(pct);
            var sub = new TextBlock { FontSize = 11, Foreground = B("Brush.TextSub"), FontFamily = F(), Margin = new Thickness(0, 2, 0, 0) };
            sp.Children.Add(sub);

            var speeds = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var sRead = new TextBlock { FontSize = 11.5, Foreground = B("Brush.TextSub"), FontFamily = F() };
            var sWrite = new TextBlock { FontSize = 11.5, Foreground = B("Brush.TextSub"), FontFamily = F(), Margin = new Thickness(12, 0, 0, 0) };
            speeds.Children.Add(sRead); speeds.Children.Add(sWrite);
            sp.Children.Add(speeds);

            card.Child = sp;
            _cards.Children.Add(card);
            _bars.Add((track, fill, pct, sub, sRead, sWrite));
        }
    }

    private static Brush B(string key) => ThemeBrush.Get(key);
    private static FontFamily F() => (FontFamily)Application.Current.Resources["Font.Main"];
    private static Brush AB(string key, double alpha) => ThemeBrush.GetAlpha(key, alpha);
}
