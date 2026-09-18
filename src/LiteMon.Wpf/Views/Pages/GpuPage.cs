using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LiteMon.Core;
using LiteMon.Core.Models;
using LiteMon.Wpf.Controls;

namespace LiteMon.Wpf.Views.Pages;

/// <summary>GPU 页：摘要 + 占用曲线 + 显存条 + GPU 应用列表 + 结束进程浮岛。</summary>
public class GpuPage : BasePage
{
    private readonly TextBlock _vUsage, _vVram, _vTemp, _vPower, _vFreq;
    private readonly TextBlock _sUsage, _sVram, _sTemp, _sPower, _sFreq;
    private readonly Border _vramBarTrack;
    private readonly Border _vramBarFill;
    private readonly AppListPanel _appList;
    private readonly MiniChart _chart;
    private readonly TextBlock _chartHint;
    private readonly Border _naCard;
    private readonly Grid _contentGrid;
    private DateTime _lastListRefresh = DateTime.MinValue;

    private TextBlock _vramDetailText = null!;

    public GpuPage()
    {
        var root = new DockPanel();

        var stats = new WrapPanel { Margin = new Thickness(20, 16, 20, 6) };
        var cards = new[]
        {
            StatCard(Services.Loc.T("Gpu.Title"), out _vUsage, out _sUsage, B("Brush.Gpu")),
            StatCard(Services.Loc.T("Gpu.Vram"), out _vVram, out _sVram, B("Brush.Gpu")),
            StatCard(Services.Loc.T("Gpu.Temp"), out _vTemp, out _sTemp, B("Brush.Orange")),
            StatCard(Services.Loc.T("Gpu.Power"), out _vPower, out _sPower),
            StatCard(Services.Loc.T("Gpu.CoreFreq"), out _vFreq, out _sFreq),
        };
        foreach (var c in cards)
        {
            c.MinWidth = 150;
            c.Margin = new Thickness(0, 0, 14, 12);
            stats.Children.Add(c);
        }
        DockPanel.SetDock(stats, Dock.Top);
        root.Children.Add(stats);

        // 整页滚动
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
            Text = Services.Loc.T("Gpu.CurveTitle"),
            FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = B("Brush.Text"), FontFamily = F(),
            Margin = new Thickness(2, 0, 0, 8),
        };
        DockPanel.SetDock(chartTitle, Dock.Top);
        cp.Children.Add(chartTitle);
        _chart = new MiniChart
        {
            Height = 90,
            SeriesBrush = B("Brush.Gpu"),
            FillBrush = ThemeBrush.GetAlpha("Brush.Gpu", 0.18),
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

        // 显存条
        var vramCard = new Border { Style = (Style)TryFindResource("Card"), Padding = new Thickness(14, 10, 14, 12), Margin = new Thickness(0, 0, 0, 14) };
        var vp = new DockPanel();
        var vt = new TextBlock { Text = Services.Loc.T("Gpu.VramUsage"), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = B("Brush.Text"), FontFamily = F(), Margin = new Thickness(2, 0, 0, 8) };
        DockPanel.SetDock(vt, Dock.Top);
        vp.Children.Add(vt);
        var vramStack = new StackPanel();
        _vramBarTrack = new Border
        {
            Height = 12, CornerRadius = new CornerRadius(6), Background = B("Brush.Track"), ClipToBounds = true,
            Child = _vramBarFill = new Border
            {
                Height = 12, CornerRadius = new CornerRadius(6), Width = 2,
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = B("Brush.Gpu"),
            },
        };
        vramStack.Children.Add(_vramBarTrack);
        var vramDetail = new TextBlock
        {
            FontSize = 11, Foreground = B("Brush.TextSub"), Margin = new Thickness(0, 8, 0, 0), FontFamily = F(),
        };
        _vramDetailText = vramDetail;
        vramStack.Children.Add(vramDetail);
        vp.Children.Add(vramStack);
        vramCard.Child = vp;
        body.Children.Add(vramCard);

        // 应用列表（仅 GPU 维度）
        _appList = new AppListPanel(new[]
        {
            new AppListPanel.ColDef { Header = Services.Loc.T("Common.App"), Width = 3.0, IsName = true },
            new AppListPanel.ColDef { Header = "GPU", Width = 0.9 },
            new AppListPanel.ColDef { Header = Services.Loc.T("Gpu.Dedicated"), Width = 1.1 },
            new AppListPanel.ColDef { Header = Services.Loc.T("Gpu.Shared"), Width = 1.1 },
        }, emptyHint: Services.Loc.T("List.NoGpuApp"));
        body.Children.Add(_appList);

        root.Children.Add(scroll);

        // GPU 不可用遮罩（Grid 叠加覆盖层；IsHitTestVisible=false 避免挡住下层滚动）
        _naCard = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x16, 0x1B, 0x22)),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        var naStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
        };
        _naCard.Child = naStack;
        naStack.Children.Add(new TextBlock
        {
            Text = "GPU", FontSize = 34, Foreground = B("Brush.TextDim"),
            HorizontalAlignment = HorizontalAlignment.Center, FontFamily = F(),
        });
        naStack.Children.Add(new TextBlock
        {
            Text = Services.Loc.T("Gpu.NoData"), FontSize = 13, Foreground = B("Brush.TextSub"),
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0), FontFamily = F(),
        });
        naStack.Children.Add(new TextBlock
        {
            Text = Services.Loc.T("Gpu.NoDataHint"), FontSize = 11, Foreground = B("Brush.TextDim"),
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0), FontFamily = F(),
        });

        _contentGrid = new Grid();
        _contentGrid.Children.Add(root);
        _contentGrid.Children.Add(_naCard);
        Content = _contentGrid;
    }

    /// <summary>GPU 页的"结束进程"目标列表。</summary>
    public override LiteMon.Wpf.Controls.AppListPanel? KillableList => _appList;

    private readonly double[] _curveBuf = new double[Core.Scheduling.CurveHistory.Capacity];
    private DateTime _lastHintUpdate = DateTime.MinValue;

    /// <summary>曲线 0.1s 重绘；百分比数字 0.5s 更新。</summary>
    public override void UpdateChart(Core.Scheduling.MonitorService monitor)
    {
        int n = monitor.Curves.CopyGpu(_curveBuf);
        _chart.SetData(_curveBuf, n);
        if ((DateTime.UtcNow - _lastHintUpdate).TotalSeconds >= 0.5)
        {
            _lastHintUpdate = DateTime.UtcNow;
            _chartHint.Text = monitor.Curves.LatestGpu.ToString("0.0") + "%";
        }
    }

    public override void Apply(Snapshot s)
    {
        var g = s.Gpu;
        bool ok = g.IsAvailable;
        _naCard.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
        if (!ok) return;

        _vUsage.Text = g.Usage.ToString("0") + "%";
        _sUsage.Text = g.Name;

        if (g.VramTotalBytes > 0)
        {
            _vVram.Text = Formatting.FormatBytes(g.VramUsedBytes);
            _sVram.Text = Services.Loc.T("Gpu.VramTotal", Formatting.FormatBytes(g.VramTotalBytes), g.VramPercent.ToString("0.0"));
            _vramBarFill.Width = Math.Max(2, g.VramPercent / 100.0 * Math.Max(60, _vramBarTrack.ActualWidth));
            _vramDetailText.Text = $"{Formatting.FormatBytes(g.VramUsedBytes)} / {Formatting.FormatBytes(g.VramTotalBytes)}";
        }
        else
        {
            _vVram.Text = Formatting.FormatBytes(g.VramUsedBytes);
            _sVram.Text = Services.Loc.T("Gpu.VramUnknown");
        }

        _vTemp.Text = g.TemperatureC is > 0 ? g.TemperatureC.Value.ToString("0") + " ℃" : "N/A";
        _sTemp.Text = g.TemperatureC is > 0 ? "" : Services.Loc.T("Gpu.SensorNA");
        _vPower.Text = g.PowerWatts is > 0 ? g.PowerWatts.Value.ToString("0.0") + " W" : "N/A";
        _vFreq.Text = g.CoreFrequencyMHz is > 0 ? (g.CoreFrequencyMHz.Value / 1000.0).ToString("0.00") + " GHz" : "N/A";
        _sFreq.Text = g.MemoryFrequencyMHz is > 0 ? Services.Loc.T("Gpu.MemFreq", (g.MemoryFrequencyMHz.Value / 1000.0).ToString("0.0")) : "";

        // 曲线由 UpdateChart 高频驱动（0.1s），此处不再推点

        if ((DateTime.Now - _lastListRefresh).TotalSeconds >= 2)
        {
            _lastListRefresh = DateTime.Now;
            var top = s.Processes
                .Where(p => p.GpuPercent > 0.05 || p.GpuDedicatedBytes > 10 * 1024 * 1024)
                .OrderByDescending(p => p.GpuDedicatedBytes + (ulong)(p.GpuPercent * 50 * 1024 * 1024))
                .Take(30)
                .Select(p => new AppListPanel.RowData
                {
                    Pid = p.Pid,
                    Cells = new string?[]
                    {
                        p.Name + "  (" + p.Pid + ")",
                        p.GpuPercent > 0 ? p.GpuPercent.ToString("0.0") + "%" : "—",
                        p.GpuDedicatedBytes > 0 ? Formatting.FormatBytes(p.GpuDedicatedBytes) : "—",
                        p.GpuSharedBytes > 0 ? Formatting.FormatBytes(p.GpuSharedBytes) : "—",
                    },
                })
                .ToList();
            _appList.Update(top);
        }
    }

    private static Brush B(string key) => ThemeBrush.Get(key);
    private static FontFamily F() => (FontFamily)Application.Current.Resources["Font.Main"];
}
