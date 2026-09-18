using System.Windows.Controls;
using LiteMon.Core.Models;
using LiteMon.Core.Scheduling;
using LiteMon.Wpf.Controls;

namespace LiteMon.Wpf.Views.Pages;

/// <summary>详情页基类：每帧由 MainWindow 推送 Snapshot。</summary>
public abstract class BasePage : UserControl
{
    public abstract void Apply(Snapshot s);

    /// <summary>本页可用于"结束进程"的应用列表（null = 该页无此功能，浮岛隐藏）。</summary>
    public virtual AppListPanel? KillableList => null;

    /// <summary>高频曲线刷新（0.1s 一次，由 MainWindow 快速定时器驱动；只更新图表，不碰其它数据）。</summary>
    public virtual void UpdateChart(MonitorService monitor) { }

    /// <summary>
    /// 摘要卡片（上方一栏）构建帮助。
    /// <para>
    /// <paramref name="valueFontSize"/> 用于文字较长的卡片（如协议名 "802.11ax (Wi-Fi 6)"）：
    /// 卡片宽度是均分的，长文本会溢出被裁掉，所以这类卡片用小字号并允许换行。
    /// </para>
    /// </summary>
    protected System.Windows.Controls.Border StatCard(string title, out System.Windows.Controls.TextBlock valueOut, out System.Windows.Controls.TextBlock subOut, System.Windows.Media.Brush? accent = null, double valueFontSize = 21)
    {
        var value = new System.Windows.Controls.TextBlock
        {
            FontSize = valueFontSize,
            FontWeight = System.Windows.FontWeights.Bold,
            Foreground = accent ?? (System.Windows.Media.Brush?)TryFindResource("Brush.Text"),
            FontFamily = (System.Windows.Media.FontFamily?)TryFindResource("Font.Main"),
            // 长文本（协议名/SSID）不裁切：允许换行，避免右边被卡片边界切掉
            TextWrapping = System.Windows.TextWrapping.Wrap,
            TextTrimming = System.Windows.TextTrimming.None,
        };
        var title_ = new System.Windows.Controls.TextBlock
        {
            Text = title,
            FontSize = 11,
            Foreground = (System.Windows.Media.Brush?)TryFindResource("Brush.TextSub"),
            FontFamily = (System.Windows.Media.FontFamily?)TryFindResource("Font.Main"),
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
            TextWrapping = System.Windows.TextWrapping.NoWrap,
            TextTrimming = System.Windows.TextTrimming.CharacterEllipsis,
        };
        var sub = new System.Windows.Controls.TextBlock
        {
            FontSize = 10.5,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Foreground = (System.Windows.Media.Brush?)TryFindResource("Brush.TextDim"),
            FontFamily = (System.Windows.Media.FontFamily?)TryFindResource("Font.Main"),
            Margin = new System.Windows.Thickness(0, 6, 0, 0),
        };
        var sp = new System.Windows.Controls.StackPanel();
        sp.Children.Add(title_);
        sp.Children.Add(value);
        sp.Children.Add(sub);
        var bd = new System.Windows.Controls.Border
        {
            // 紧凑内边距：六卡均分一行，太胖会把内容挤出去
            Padding = new System.Windows.Thickness(12, 8, 12, 10),
            Child = sp,
        };
        bd.Style = (System.Windows.Style?)TryFindResource("Card");
        valueOut = value;
        subOut = sub;
        return bd;
    }
}
