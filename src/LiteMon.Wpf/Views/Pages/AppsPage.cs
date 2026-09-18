using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using LiteMon.Core;
using LiteMon.Core.Models;

namespace LiteMon.Wpf.Views.Pages;

/// <summary>应用页：任务管理器式总列表（可排序、可过滤）。</summary>
public class AppsPage : BasePage
{
    private readonly ListView _list;
    private readonly TextBox _search;
    private readonly TextBlock _count;
    private ListCollectionView? _view;
    private string _filter = "";
    private string _sortBy = "CpuPercent";
    private ListSortDirection _sortDir = ListSortDirection.Descending;
    private readonly System.Collections.Generic.Dictionary<GridViewColumn, string> _colSortKeys = new();

    public AppsPage()
    {
        var root = new DockPanel();

        // 工具行
        var bar = new DockPanel { Margin = new Thickness(16, 12, 16, 8) };
        _search = new TextBox
        {
            Width = 240,
            FontSize = 12,
            Padding = new Thickness(8, 5, 8, 5),
            Background = (Brush)TryFindResource("Brush.InputBg"),
            BorderBrush = (Brush)TryFindResource("Brush.InputBorder"),
            Foreground = (Brush)TryFindResource("Brush.Text"),
            FontFamily = F(),
        };
        _search.TextChanged += (_, _) => { _filter = _search.Text.Trim(); RefreshFilter(); };
        DockPanel.SetDock(_search, Dock.Left);
        bar.Children.Add(_search);

        _count = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            FontSize = 11,
            Foreground = B("Brush.TextSub"),
            FontFamily = F(),
        };
        bar.Children.Add(_count);
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);

        // 列表
        _list = new ListView
        {
            FontFamily = F(),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Margin = new Thickness(16, 0, 16, 16),
        };
        var gv = new GridView { AllowsColumnReorder = false };
        AddCol(gv, Services.Loc.T("Common.App"), 300, "Display", sortBy: "Name");
        AddCol(gv, "PID", 64, "PidText", sortBy: "Pid");
        AddCol(gv, "CPU", 72, "CpuText", sortBy: "CpuPercent", alignRight: true);
        AddCol(gv, Services.Loc.T("Common.Memory"), 90, "MemText", sortBy: "WorkingSetBytes", alignRight: true);
        AddCol(gv, "GPU", 64, "GpuText", sortBy: "GpuPercent", alignRight: true);
        AddCol(gv, Services.Loc.T("Common.Vram"), 90, "VramText", sortBy: "GpuDedicatedBytes", alignRight: true);
        _list.View = gv;
        root.Children.Add(_list);

        // 表头点击排序
        foreach (var col in gv.Columns)
        {
            if (!_colSortKeys.TryGetValue(col, out var src)) continue;
            var headerBd = GetHeaderButton(col.Header as string, src);
            col.Header = headerBd;
        }

        Content = root;
    }

    private Button GetHeaderButton(string? text, string sortKey)
    {
        var b = new Button
        {
            Content = text,
            Style = (Style)TryFindResource("BtnText"),
            FontSize = 11,
            Padding = new Thickness(6, 4, 6, 4),
            Tag = sortKey,
        };
        b.Click += (_, _) => ToggleSort(sortKey);
        return b;
    }

    private void ToggleSort(string key)
    {
        if (_sortBy == key) _sortDir = _sortDir == ListSortDirection.Descending ? ListSortDirection.Ascending : ListSortDirection.Descending;
        else { _sortBy = key; _sortDir = key == "Name" || key == "Pid" ? ListSortDirection.Ascending : ListSortDirection.Descending; }
        RefreshFilter();
    }

    private void AddCol(GridView gv, string header, double width, string binding, string? sortBy = null, bool alignRight = false)
    {
        var fc = new FrameworkElementFactory(typeof(TextBlock));
        fc.SetBinding(TextBlock.TextProperty, new Binding(binding));
        fc.SetValue(TextBlock.FontSizeProperty, 12.0);
        fc.SetValue(FrameworkElement.MarginProperty, new Thickness(6, 2, 6, 2));
        fc.SetValue(TextBlock.FontFamilyProperty, F());
        if (alignRight) fc.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Right);
        var dt = new DataTemplate { VisualTree = fc };
        var col = new GridViewColumn { Header = header, Width = width, CellTemplate = dt };
        if (sortBy != null) _colSortKeys[col] = sortBy;
        gv.Columns.Add(col);
    }

    private sealed class Row
    {
        public Row(ProcessMetrics p) => P = p;
        public ProcessMetrics P { get; }
        public string Display => P.Name;
        public int Pid => P.Pid;
        public string PidText => P.Pid.ToString();
        public double CpuPercent => P.CpuPercent;
        public string CpuText => P.CpuPercent.ToString("0.0") + "%";
        public ulong WorkingSetBytes => P.WorkingSetBytes;
        public string MemText => Formatting.FormatBytes(P.WorkingSetBytes);
        public double GpuPercent => P.GpuPercent;
        public string GpuText => P.GpuPercent > 0.05 ? P.GpuPercent.ToString("0.0") + "%" : "—";
        public ulong GpuDedicatedBytes => P.GpuDedicatedBytes;
        public string VramText => P.GpuDedicatedBytes > 10 * 1024 * 1024 ? Formatting.FormatBytes(P.GpuDedicatedBytes) : "—";
    }

    private DateTime _lastListRefresh = DateTime.MinValue;
    private const double ListRefreshSeconds = 2.0; // 列表重建每 2s 一次，降低 GC/渲染开销

    public override void Apply(Snapshot s)
    {
        _count.Text = Services.Loc.T("Apps.Count", s.Processes.Count);
        if ((DateTime.Now - _lastListRefresh).TotalSeconds < ListRefreshSeconds && _view != null) return;
        _lastListRefresh = DateTime.Now;

        var rows = s.Processes.Select(p => new Row(p)).ToList();
        _view = new ListCollectionView(rows);
        _view.Filter = o => _filter.Length == 0 || ((Row)o).Display.Contains(_filter, StringComparison.OrdinalIgnoreCase);
        ApplySort();
        _list.ItemsSource = _view;
    }

    private void ApplySort()
    {
        if (_view == null) return;
        var prop = _sortBy;
        _view.SortDescriptions.Clear();
        _view.SortDescriptions.Add(new SortDescription(prop, _sortDir));
        // 稳定次序
        _view.SortDescriptions.Add(new SortDescription("Pid", ListSortDirection.Ascending));
    }

    private void RefreshFilter()
    {
        if (_view == null) return;
        ApplySort();
        _view.Refresh();
    }

    private static Brush B(string key) => (Brush)Application.Current.Resources.FindCompatibleColor2(key);
    private static FontFamily F() => (FontFamily)Application.Current.Resources["Font.Main"];
}
