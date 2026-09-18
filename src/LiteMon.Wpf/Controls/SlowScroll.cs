using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LiteMon.Wpf.Controls;

/// <summary>
/// 页面级 ScrollViewer 的滚轮调速：WPF 默认每格滚 3 行（约 50px）偏快，
/// 这里缩放为默认的 80%，手感更稳。只拦截滚轮，不动拖动/键盘滚动。
/// </summary>
public static class SlowScroll
{
    /// <summary>相对系统默认滚轮步长的比例。</summary>
    public const double Factor = 0.8;

    private const double LineHeight = 17.0;   // 与页面 12~13px 字号的实际行高一致

    public static void Attach(ScrollViewer sv) => sv.PreviewMouseWheel += OnPreviewWheel;

    private static void OnPreviewWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv || e.Handled) return;
        if (sv.ScrollableHeight <= 0) return;
        int lines = SystemParameters.WheelScrollLines;   // 通常 3
        if (lines <= 0) return;                          // -1 = 整页滚动，保留系统行为
        double step = lines * LineHeight * Factor;
        double target = sv.VerticalOffset - Math.Sign(e.Delta) * step;
        sv.ScrollToVerticalOffset(Math.Max(0, Math.Min(sv.ScrollableHeight, target)));
        e.Handled = true;
    }
}
