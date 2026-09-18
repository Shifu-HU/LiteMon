using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace LiteMon.Wpf.Controls;

/// <summary>
/// 轻量历史曲线（无第三方依赖）：600 点缓冲，0.1s 一帧 = 60 秒窗口。
/// 数据由外部高频推入（SetData / Push），Geometry 重建 600 点开销可忽略。
/// </summary>
public class MiniChart : Control
{
    private const int Capacity = 600;
    private readonly double[] _buf = new double[Capacity];
    private readonly double[] _buf2 = new double[Capacity];   // 副序列（虚线）
    private bool _has2;
    private int _count, _head;
    private double _max = 100.0;
    private double _max2 = 100.0;      // 副序列窗口最大（双轴用）
    private double? _maxRender2;

    /// <summary>
    /// 副序列独立 y 轴自适应（双轴）：下行 30MB/s vs 上行 200KB/s 这类量级差百倍的场景，
    /// 共轴会把副序列压成贴地直线（看起来"没画"）。true = 副序列按自身最大值缩放。
    /// RAM 页（物理/提交同为百分比且可比）保持 false 共轴。
    /// </summary>
    public bool DualScale { get; set; }

    static MiniChart()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(MiniChart),
            new FrameworkPropertyMetadata(typeof(MiniChart)));
    }

    public static readonly DependencyProperty SeriesBrushProperty =
        DependencyProperty.Register(nameof(SeriesBrush), typeof(Brush), typeof(MiniChart),
            new PropertyMetadata(null));

    public Brush? SeriesBrush
    {
        get => (Brush?)GetValue(SeriesBrushProperty);
        set => SetValue(SeriesBrushProperty, value);
    }

    public static readonly DependencyProperty FillBrushProperty =
        DependencyProperty.Register(nameof(FillBrush), typeof(Brush), typeof(MiniChart),
            new PropertyMetadata(null));

    public Brush? FillBrush
    {
        get => (Brush?)GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    /// <summary>副序列（虚线）画笔：如上行/已提交/磁盘写。</summary>
    public static readonly DependencyProperty DashBrushProperty =
        DependencyProperty.Register(nameof(DashBrush), typeof(Brush), typeof(MiniChart),
            new PropertyMetadata(null));

    public Brush? DashBrush
    {
        get => (Brush?)GetValue(DashBrushProperty);
        set => SetValue(DashBrushProperty, value);
    }

    public static readonly DependencyProperty ValueTextProperty =
        DependencyProperty.Register(nameof(ValueText), typeof(string), typeof(MiniChart),
            new PropertyMetadata(""));

    public string ValueText
    {
        get => (string)GetValue(ValueTextProperty);
        set => SetValue(ValueTextProperty, value);
    }

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(MiniChart),
            new PropertyMetadata(""));

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>追加一个数据点（value 任意非负数，>max 时 max 自适应）。</summary>
    public void Push(double value)
    {
        if (double.IsNaN(value) || value < 0) value = 0;
        _buf[_head] = value;
        _head = (_head + 1) % Capacity;
        if (_count < Capacity) _count++;
        if (value > _max) _max = value;
        InvalidateVisual();
    }

    /// <summary>
    /// 批量替换整条序列（高频曲线渲染路径：values 最旧在前，count 为有效长度）。
    /// 覆盖内部环形缓冲并整帧重绘，避免逐点 Push 的 O(n) 循环。
    /// </summary>
    public void SetData(double[] values, int count) => SetDataImpl(_buf, values, count);

    /// <summary>批量替换副序列（虚线）数据；不传（null）则清除副序列。</summary>
    public void SetData2(double[]? values, int count)
    {
        if (values == null || count < 2) { _has2 = false; InvalidateVisual(); return; }
        _has2 = true;
        SetDataImpl(_buf2, values, count);
        InvalidateVisual();
    }

    private void SetDataImpl(double[] dest, double[] values, int count)
    {
        if (values == null || values.Length == 0 || count < 2) { if (count < 2) { _count = Math.Max(0, count); InvalidateVisual(); } return; }
        int n = Math.Min(count, Math.Min(values.Length, Capacity));
        int srcStart = count - n;
        double windowMax = 0;   // 当前 60 秒窗口内的最大值（波峰滑出后自适应缩回）
        for (int i = 0; i < n; i++)
        {
            double v = values[srcStart + i];
            if (double.IsNaN(v) || v < 0) v = 0;
            dest[i] = v;
            if (v > windowMax) windowMax = v;
        }
        if (ReferenceEquals(dest, _buf))
        {
            _count = n;
            _head = n % Capacity;
            // y 尺度随窗口数据重算：只涨不缩的旧逻辑会让波峰过后曲线永远贴底
            _max = windowMax > 0 ? windowMax : 100;
        }
        else
        {
            _max2 = windowMax > 0 ? windowMax : 100;
        }
        InvalidateVisual();
    }

    public void Reset() { _count = 0; _head = 0; _max = 100; _max2 = 100; _has2 = false; _maxRender2 = null; InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0 || _count < 2) return;

        // 平滑 y 最大值：不对称 EMA——新高快速跟随（0.45，~5 帧），峰过后较快缩回（0.25，~9 帧）
        var target = _max;
        if (target <= 0) target = 1;
        _maxRender ??= target;
        _maxRender = _maxRender.Value < target
            ? _maxRender * 0.55 + target * 0.45
            : _maxRender * 0.75 + target * 0.25;
        var yMax = _maxRender.Value * 1.05;
        if (yMax <= 0) yMax = 1;

        var series = SeriesBrush ?? Brushes.DodgerBlue;

        // 曲线 + 半透明填充
        var geo = new StreamGeometry();
        var fillGeo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            for (int i = 0; i < _count; i++)
            {
                // 环形顺序：最旧在 0
                int idx = (_head - _count + i + Capacity * 2) % Capacity;
                double x = w * i / Math.Max(1, Capacity - 1);
                double y = h - (_buf[idx] / yMax) * (h - 4) - 2;
                if (i == 0) ctx.BeginFigure(new Point(x, y), false, false);
                else ctx.LineTo(new Point(x, y), true, false);
            }
        }
        using (var fctx = fillGeo.Open())
        {
            for (int i = 0; i < _count; i++)
            {
                int idx = (_head - _count + i + Capacity * 2) % Capacity;
                double x = w * i / Math.Max(1, Capacity - 1);
                double y = h - (_buf[idx] / yMax) * (h - 4) - 2;
                if (i == 0) fctx.BeginFigure(new Point(x, y), true, false);
                else fctx.LineTo(new Point(x, y), true, false);
            }
            fctx.LineTo(new Point(w * (_count - 1) / Math.Max(1, Capacity - 1), h), true, false);
            fctx.LineTo(new Point(0, h), true, false);
        }
        geo.Freeze();
        fillGeo.Freeze();

        var fill = FillBrush;
        if (fill != null) dc.DrawGeometry(fill, null, fillGeo);
        var pen = new Pen(series, 1.6);
        pen.Freeze();
        dc.DrawGeometry(null, pen, geo);

        // 副序列（虚线）：DualScale 时按自身窗口最大值缩放（下行/上行量级差百倍场景），
        // 否则与主序列共轴（RAM 物理/提交百分比可比）
        if (_has2 && DashBrush != null)
        {
            var target2 = _max2;
            if (target2 <= 0) target2 = 1;
            _maxRender2 ??= target2;
            _maxRender2 = _maxRender2.Value < target2
                ? _maxRender2 * 0.55 + target2 * 0.45
                : _maxRender2 * 0.75 + target2 * 0.25;
            var yMax2 = DualScale ? _maxRender2.Value * 1.05 : yMax;
            if (yMax2 <= 0) yMax2 = 1;

            var geo2 = new StreamGeometry();
            using (var ctx2 = geo2.Open())
            {
                for (int i = 0; i < _count; i++)
                {
                    int idx = (_head - _count + i + Capacity * 2) % Capacity;
                    double x = w * i / Math.Max(1, Capacity - 1);
                    double y = h - (_buf2[idx] / yMax2) * (h - 4) - 2;
                    if (i == 0) ctx2.BeginFigure(new Point(x, y), false, false);
                    else ctx2.LineTo(new Point(x, y), true, false);
                }
            }
            geo2.Freeze();
            var dash = new Pen(DashBrush, 1.8)
            {
                DashStyle = new DashStyle(new double[] { 5, 3 }, 0),
            };
            dash.Freeze();
            dc.DrawGeometry(null, dash, geo2);

            // 副序列当前值空心点
            int last2 = (_head - 1 + Capacity) % Capacity;
            double lx2 = w * (_count - 1) / Math.Max(1, Capacity - 1);
            double ly2 = h - (_buf2[last2] / yMax2) * (h - 4) - 2;
            dc.DrawEllipse(null, dash, new Point(lx2, ly2), 3, 3);
        }

        // 当前值小点
        int lastIdx = (_head - 1 + Capacity) % Capacity;
        double lx = w * (_count - 1) / Math.Max(1, Capacity - 1);
        double ly = h - (_buf[lastIdx] / yMax) * (h - 4) - 2;
        dc.DrawEllipse(series, null, new Point(lx, ly), 2.5, 2.5);
    }

    private double? _maxRender;
}
