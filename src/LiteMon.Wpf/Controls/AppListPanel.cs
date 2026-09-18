using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LiteMon.Wpf.Controls;

/// <summary>
/// 通用文本表格：表头与所有行共用同一组列宽（Grid），确保内容与标题对齐。
/// 行区不内部滚动：整卡高度随内容自适应，由页面级 ScrollViewer 统一滚动
/// （避免嵌套滚动与外层裁剪）。支持行选中（点击高亮，供"结束进程"等操作）。
/// </summary>
public class AppListPanel : UserControl
{
    /// <summary>一行数据：预格式化的单元格 + 关联进程 PID（0 表示无关联）。</summary>
    public sealed class RowData
    {
        public required string?[] Cells { get; init; }
        public int Pid { get; init; }
    }

    public sealed class ColDef
    {
        public required string Header { get; init; }
        public double Width { get; init; } = 90;      // 星号权重
        public bool IsName { get; init; }
        public bool AlignRight { get; init; }
    }

    /// <summary>当前选中行的 PID（未选中为 0）。</summary>
    public int SelectedPid => _selected?.Pid ?? 0;

    /// <summary>选中行变化（pid=0 表示取消选中）。</summary>
    public event Action<int>? SelectionChanged;

    private readonly ColDef[] _cols;
    private readonly Grid _grid;          // 表头 + 全部行共用列定义
    private readonly TextBlock _emptyHint;
    private readonly TextBlock[] _headerTexts;
    private RowData? _selected;
    private Border? _selectedRowVisual;
    /// <summary>行复用池：pid → 行视觉（Border）。刷新时 PID 相同的行只改文本，避免整表重建闪烁。</summary>
    private readonly Dictionary<int, Border> _rowByPid = new();

    public AppListPanel(ColDef[] cols, string emptyHint = "")
    {
        _cols = cols;
        if (string.IsNullOrEmpty(emptyHint)) emptyHint = Services.Loc.T("List.Empty");

        var root = new DockPanel();

        // 表头行
        var header = new Grid { Margin = new Thickness(10, 2, 10, 6) };
        ApplyCols(header);
        _headerTexts = new TextBlock[cols.Length];
        for (int i = 0; i < cols.Length; i++)
        {
            var h = new TextBlock
            {
                Text = cols[i].Header,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = ThemeBrush.Get("Brush.TextSub"),
                FontFamily = F(),
                HorizontalAlignment = cols[i].AlignRight ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            };
            Grid.SetColumn(h, i);
            header.Children.Add(h);
            _headerTexts[i] = h;
        }
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        // 表头分隔线
        var sep = new Border
        {
            Height = 1,
            Background = ThemeBrush.Get("Brush.Divider"),
            Margin = new Thickness(10, 0, 10, 2),
        };
        DockPanel.SetDock(sep, Dock.Top);
        root.Children.Add(sep);

        // 空数据提示
        _emptyHint = new TextBlock
        {
            Text = emptyHint,
            FontSize = 12,
            Foreground = ThemeBrush.Get("Brush.TextDim"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 24, 0, 24),
            FontFamily = F(),
        };
        DockPanel.SetDock(_emptyHint, Dock.Top);
        _emptyHint.Visibility = Visibility.Collapsed;
        root.Children.Add(_emptyHint);

        // 行区：直接展开，无内部滚动
        _grid = new Grid { Margin = new Thickness(10, 0, 10, 8) };
        ApplyCols(_grid);
        root.Children.Add(_grid);

        var host = new Border
        {
            Style = (Style)Application.Current.Resources["Card"],
            Padding = new Thickness(4),
            Child = root,
        };
        // 点击卡片内任意非行区域（表头/分隔线/行下空白）→ 放弃选中；
        // 点击行本身不在此处理（事件会冒泡，需从源向上找行 Border 排除）
        host.MouseLeftButtonUp += (_, e) =>
        {
            if (_selected == null) return;
            DependencyObject? d = e.OriginalSource as DependencyObject;
            while (d != null && !ReferenceEquals(d, host))
            {
                if (d is Border b && b.Tag is RowData) return;   // 点在行内：交给行自身逻辑
                d = System.Windows.Media.VisualTreeHelper.GetParent(d);
            }
            ClearSelection();
        };
        Content = host;
    }

    private static FontFamily F() => (FontFamily)Application.Current.Resources["Font.Main"];

    private void ApplyCols(Grid g)
    {
        g.ColumnDefinitions.Clear();
        foreach (var c in _cols)
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(c.Width, GridUnitType.Star) });
    }

    /// <summary>
    /// 增量更新行（行复用，避免整表重建闪烁）：
    /// - PID 相同的行只更新单元格文本，视觉树不动 → 无闪烁
    /// - 新 PID 建新行；消失 PID 移除旧行
    /// - 排序变化仅调整 Grid.SetRow（WPF 平滑处理，不重建元素）
    /// - 刷新保留选中：目标进程仍在则保持高亮且不触发事件（浮岛/武装态不重置）
    /// </summary>
    public void Update(IReadOnlyList<RowData> rows)
    {
        // pid=0 的行（接口表等）没有稳定复用键：移除全部 pid=0 旧行，走"重建"路径
        // （行数少、内容稳定，无感；避免行复用池把旧行堆积撑爆表高）
        if (rows.Count > 0 && rows.All(r => r.Pid == 0))
        {
            var stale = _grid.Children.OfType<Border>().Where(b => b.Tag is RowData rd && rd.Pid == 0).ToList();
            foreach (var b in stale) _grid.Children.Remove(b);
        }

        int keepPid = _selected?.Pid ?? 0;

        if (rows.Count == 0)
        {
            foreach (var b in _rowByPid.Values) _grid.Children.Remove(b);
            _rowByPid.Clear();
            _grid.RowDefinitions.Clear();
            _emptyHint.Visibility = Visibility.Visible;
            if (keepPid > 0)
            {
                _selected = null;
                _selectedRowVisual = null;
                SelectionChanged?.Invoke(0);
            }
            return;
        }
        _emptyHint.Visibility = Visibility.Collapsed;

        // 行高对齐（行数可能变）
        while (_grid.RowDefinitions.Count < rows.Count)
            _grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(27) });
        while (_grid.RowDefinitions.Count > rows.Count)
            _grid.RowDefinitions.RemoveAt(_grid.RowDefinitions.Count - 1);

        // 同名 PID 重名冲突时（不同实例同 pid 不可能）逐条处理
        var seen = new HashSet<int>();
        for (int r = 0; r < rows.Count; r++)
        {
            var data = rows[r];
            if (!seen.Add(data.Pid) && data.Pid != 0) continue;   // 重复 pid 只留第一个（理论不发生）
            var row = GetOrCreateRow(data);
            // 文本更新（只改 Text 属性——不重建视觉树）
            if (row.Child is Grid inner)
                for (int i = 0; i < _cols.Length; i++)
                {
                    if (inner.Children[i] is TextBlock tb)
                        tb.Text = i < data.Cells.Length ? (data.Cells[i] ?? "—") : "—";
                }
            // 位置更新（排序变化 → SetRow 平滑调整）
            Grid.SetRow(row, r);
            Grid.SetColumnSpan(row, _grid.ColumnDefinitions.Count);
            row.Tag = data;
        }

        // 移除消失的行
        foreach (var dead in _rowByPid.Where(kv => !seen.Contains(kv.Key)).ToList())
        {
            _grid.Children.Remove(dead.Value);
            _rowByPid.Remove(dead.Key);
        }

        // 选中保持：pid 仍在 → 高亮跟到复用行（不发事件，浮岛不重置）；pid 消失 → 复位
        if (keepPid > 0)
        {
            if (_rowByPid.TryGetValue(keepPid, out var visual))
            {
                var again = rows.FirstOrDefault(x => x.Pid == keepPid);
                if (again != null)
                {
                    _selected = again;
                    _selectedRowVisual = visual;
                    visual.Background = ThemeBrush.GetAlpha("Brush.Accent", 0.22);
                }
            }
            else
            {
                _selected = null;
                _selectedRowVisual = null;
                SelectionChanged?.Invoke(0);
            }
        }
    }

    /// <summary>取复用行或新建（新建走完整视觉树 + 事件绑定）。</summary>
    private Border GetOrCreateRow(RowData data)
    {
        if (_rowByPid.TryGetValue(data.Pid, out var existing)) return existing;

        var row = new Border
        {
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 1, 0, 1),
            Cursor = Cursors.Hand,
            Tag = data,
        };
        var inner = new Grid();
        var widths = _grid.ColumnDefinitions.Select(c => c.Width).ToArray();
        foreach (var w in widths)
            inner.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        for (int i = 0; i < _cols.Length; i++)
        {
            var text = new TextBlock
            {
                Text = i < data.Cells.Length ? (data.Cells[i] ?? "—") : "—",
                FontSize = 12,
                Foreground = ThemeBrush.Get(_cols[i].IsName ? "Brush.Text" : "Brush.TextSub"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontFamily = F(),
                HorizontalAlignment = _cols[i].AlignRight ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            };
            Grid.SetColumn(text, i);
            inner.Children.Add(text);
        }
        row.Child = inner;

        // 点击选中（闭包捕 row；data 由 Tag 随刷新更新）
        row.MouseLeftButtonUp += (_, _) => SelectRow(row, row.Tag as RowData ?? data);

        row.MouseEnter += (_, _) =>
        {
            if (row.Background == null)
                row.Background = ThemeBrush.GetAlpha("Brush.Accent", 0.10);
        };
        row.MouseLeave += (_, _) =>
        {
            // 只清除 hover 色：选中色由选中逻辑管理
            if (_selectedRowVisual != row)
                row.Background = null;
            else
                row.Background = ThemeBrush.GetAlpha("Brush.Accent", 0.22);   // 选中行恢复高亮
        };

        _grid.Children.Add(row);
        if (data.Pid != 0) _rowByPid[data.Pid] = row;   // pid=0 的行（接口表等）不复用
        return row;
    }

    private void SelectRow(Border row, RowData data)
    {
        if (_selectedRowVisual != null && _selectedRowVisual != row)
            _selectedRowVisual.Background = null;

        if (_selected == data)
        {
            _selected = null;
            _selectedRowVisual = null;
            row.Background = null;
            SelectionChanged?.Invoke(0);
            return;
        }

        _selected = data;
        _selectedRowVisual = row;
        row.Background = ThemeBrush.GetAlpha("Brush.Accent", 0.22);
        SelectionChanged?.Invoke(data.Pid);
    }

    /// <summary>改某列表头的显示文本（如"下载"→"IO 速率"）。</summary>
    public void SetHeaderText(int col, string text)
    {
        if (col >= 0 && col < _headerTexts.Length) _headerTexts[col].Text = text;
    }

    /// <summary>清除选中状态。</summary>
    public void ClearSelection()
    {
        if (_selectedRowVisual != null)
            _selectedRowVisual.Background = null;
        _selected = null;
        _selectedRowVisual = null;
        SelectionChanged?.Invoke(0);
    }
}
