using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LiteMon.Wpf;

namespace LiteMon.Wpf.Controls;

/// <summary>iOS 风格拨动开关（参考项目的 ToggleSwitch）。</summary>
public class ToggleSwitch : Control
{
    static ToggleSwitch()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(ToggleSwitch),
            new FrameworkPropertyMetadata(typeof(ToggleSwitch)));
    }

    public static readonly DependencyProperty IsOnProperty =
        DependencyProperty.Register(nameof(IsOn), typeof(bool), typeof(ToggleSwitch),
            new PropertyMetadata(false, (d, _) => ((ToggleSwitch)d).InvalidateVisual()));

    public bool IsOn
    {
        get => (bool)GetValue(IsOnProperty);
        set => SetValue(IsOnProperty, value);
    }

    public static readonly RoutedEvent ToggledEvent =
        EventManager.RegisterRoutedEvent("Toggled", RoutingStrategy.Bubble,
            typeof(RoutedEventHandler), typeof(ToggleSwitch));

    public event RoutedEventHandler Toggled
    {
        add => AddHandler(ToggledEvent, value);
        remove => RemoveHandler(ToggledEvent, value);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        IsOn = !IsOn;
        RaiseEvent(new RoutedEventArgs(ToggledEvent));
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var track = IsOn
            ? new SolidColorBrush(ThemeBrush.GetColor("Brush.Accent"))
            : new SolidColorBrush(Color.FromRgb(0x6E, 0x76, 0x84));
        track.Opacity = IsOn ? 1 : 0.55;
        dc.DrawRoundedRectangle(track, null, new Rect(0, 0, w, h), h / 2, h / 2);
        double r = h - 4;
        double x = IsOn ? w - r - 2 : 2;
        dc.DrawEllipse(Brushes.White, null, new Point(x + r / 2, h / 2), r / 2, r / 2);
    }
}
