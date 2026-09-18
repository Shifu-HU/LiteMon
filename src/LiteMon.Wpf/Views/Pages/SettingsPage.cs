using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LiteMon.Core.Interop;
using LiteMon.Core.Settings;
using LiteMon.Wpf.Controls;
using LiteMon.Wpf.Services;

namespace LiteMon.Wpf.Views.Pages;

/// <summary>设置页：采集 / 外观（主题+三等分配色圆）/ 系统。</summary>
public class SettingsPage : BasePage
{
    private readonly AppRuntime _rt;

    public SettingsPage()
    {
        _rt = App.Current.Runtime;
        var root = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        SlowScroll.Attach(root);   // 滚轮速度 = 默认的 80%
        var stack = new StackPanel { Margin = new Thickness(20) };
        root.Content = stack;

        // ====== 采集 ======
        stack.Children.Add(Section(Loc.T("Settings.Collect")));

        var refreshRow = RowPanel();
        refreshRow.Children.Add(LabelCol(Loc.T("Settings.Refresh"), Loc.T("Settings.RefreshSub")));
        var ratePanel = new WrapPanel { MaxWidth = 700 };
        foreach (var s in new[] { 0.5, 1.0, 2.0, 5.0 })
        {
            var b = new RadioButton
            {
                Content = Loc.T("Settings.Seconds", s.ToString("0.#")),
                Style = (Style)TryFindResource("NavItem"),
                Tag = s,
                GroupName = "RateOptions",
                Margin = new Thickness(0, 0, 8, 6),
                IsChecked = Math.Abs(_rt.Settings.RefreshSeconds - s) < 0.01,
                FontSize = 12,
            };
            b.Checked += (_, _) => _rt.SetInterval(s);
            ratePanel.Children.Add(b);
        }
        DockPanel.SetDock(ratePanel, Dock.Right);
        refreshRow.Children.Add(ratePanel);
        stack.Children.Add(Card(refreshRow));

        var procRow = RowPanel();
        procRow.Children.Add(LabelCol(Loc.T("Settings.CollectProc"), Loc.T("Settings.CollectProcSub")));
        var procSwitch = new ToggleSwitch { IsOn = _rt.Settings.CollectProcesses, Width = 44, Height = 26, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(procSwitch, Dock.Right);
        procSwitch.Toggled += (_, _) =>
        {
            _rt.Settings.CollectProcesses = procSwitch.IsOn;
            _rt.Monitor.CollectProcesses = procSwitch.IsOn;
            _rt.Save();
        };
        procRow.Children.Add(procSwitch);
        stack.Children.Add(Card(procRow));

        var pauseRow = RowPanel();
        pauseRow.Children.Add(LabelCol(Loc.T("Settings.Pause"), Loc.T("Settings.PauseSub")));
        var pauseSwitch = new ToggleSwitch { IsOn = _rt.Settings.Paused, Width = 44, Height = 26, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(pauseSwitch, Dock.Right);
        pauseSwitch.Toggled += (_, _) => _rt.SetPaused(pauseSwitch.IsOn);
        pauseRow.Children.Add(pauseSwitch);
        stack.Children.Add(Card(pauseRow));

        // ====== 外观 ======
        stack.Children.Add(Section(Loc.T("Settings.Appearance")));

        // 主题
        var themeRow = RowPanel();
        themeRow.Children.Add(Label(Loc.T("Settings.Theme")));
        var themePanel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (mode, name) in new[] { (ThemeMode.System, Loc.T("Settings.ThemeSystem")), (ThemeMode.Dark, Loc.T("Settings.ThemeDark")), (ThemeMode.Light, Loc.T("Settings.ThemeLight")) })
        {
            var b = new RadioButton
            {
                Content = name,
                Style = (Style)TryFindResource("NavItem"),
                Tag = mode,
                Margin = new Thickness(0, 0, 8, 0),
                IsChecked = _rt.Settings.Theme == mode,
                FontSize = 12,
            };
            b.Checked += (_, _) =>
            {
                _rt.Settings.Theme = mode;
                _rt.Save();
                ThemeManager.Apply(_rt.Settings);
            };
            themePanel.Children.Add(b);
        }
        themeRow.Children.Add(themePanel);
        stack.Children.Add(Card(themeRow));

        // 配色方案：三等分圆
        var schemeRow = RowPanel();
        schemeRow.Children.Add(Label(Loc.T("Settings.Scheme")));
        var schemePanel = new WrapPanel { MaxWidth = 700 };
        foreach (var sc in ColorSchemes.All)
        {
            var display = Loc.T("Scheme." + sc.Id);
            var sel = ColorSchemes.ByName(_rt.Settings.Scheme).Id == sc.Id;
            var cell = new StackPanel
            {
                Margin = new Thickness(0, 0, 18, 12),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            cell.ToolTip = display;

            // 三等分圆（120° 扇形 × 3）
            var circle = new Grid { Width = 44, Height = 44, HorizontalAlignment = HorizontalAlignment.Center };
            var colors = new[] { sc.C1, sc.C2, sc.C3 };
            for (int seg = 0; seg < 3; seg++)
            {
                var path = new System.Windows.Shapes.Path
                {
                    Data = PieSegment(22, 22, 20, seg * 120, (seg + 1) * 120),
                    Fill = new SolidColorBrush(HexColor(colors[seg])),
                    Stroke = sel ? ThemeBrush.Get("Brush.Text") : null,
                    StrokeThickness = sel ? 2 : 0,
                };
                circle.Children.Add(path);
            }
            // 选中环
            var ring = new Border
            {
                Width = 48, Height = 48, CornerRadius = new CornerRadius(24),
                BorderThickness = new Thickness(sel ? 2 : 1),
                BorderBrush = sel ? B("Brush.Accent") : B("Brush.CardBorder"),
                IsHitTestVisible = false,
            };
            var ringHost = new Grid { Width = 48, Height = 48, HorizontalAlignment = HorizontalAlignment.Center };
            ringHost.Children.Add(ring);
            ringHost.Children.Add(circle);

            cell.Children.Add(ringHost);
            cell.Children.Add(new TextBlock
            {
                Text = display,
                FontSize = 10.5,
                Foreground = sel ? B("Brush.Accent") : B("Brush.TextSub"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 0),
                FontFamily = F(),
            });
            cell.MouseLeftButtonUp += (_, _) =>
            {
                _rt.Settings.Scheme = sc.Id;
                _rt.Save();
                ThemeManager.Apply(_rt.Settings);
            };
            schemePanel.Children.Add(cell);
        }
        schemeRow.Children.Add(schemePanel);
        stack.Children.Add(Card(schemeRow));

        // 语言（下拉菜单：联合国六种官方语言 + 日语；含 RTL 阿拉伯语）
        var langRow = RowPanel();
        langRow.Children.Add(LabelCol(Loc.T("Settings.Language"), Loc.T("Settings.LanguageSub")));
        var langBox = new ComboBox
        {
            Width = 240,
            Height = 34,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12.5,
            Style = TryFindResource("SettingCombo") as Style,
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(langBox, "LanguageSelect");
        System.Windows.Automation.AutomationProperties.SetName(langBox, "Language");
        foreach (var (code, name) in Loc.Available)
        {
            langBox.Items.Add(new ComboBoxItem
            {
                Content = name,
                Tag = code,
                Style = TryFindResource("SettingComboItem") as Style,
            });
        }
        langBox.SelectedIndex = Math.Max(0, Loc.Available.FindIndex(x => x.Code == _rt.Settings.Language));
        langBox.SelectionChanged += (_, _) =>
        {
            if (langBox.SelectedItem is not ComboBoxItem item || item.Tag is not string code) return;
            if (_rt.Settings.Language == code) return;
            _rt.Settings.Language = code;
            _rt.Save();
            Loc.Language = code;
            ThemeManager.Apply(_rt.Settings);   // 触发页面重建（与主题切换同一机制）
        };
        langRow.Children.Add(langBox);
        stack.Children.Add(Card(langRow));

        // ====== 系统 ======
        stack.Children.Add(Section(Loc.T("Settings.System")));

        var autoRow = RowPanel();
        autoRow.Children.Add(LabelCol(Loc.T("Settings.AutoStart"), Loc.T("Settings.AutoStartSub")));
        var autoSwitch = new ToggleSwitch { IsOn = NativeMethods.GetAutoStart("LiteMon"), Width = 44, Height = 26, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(autoSwitch, Dock.Right);
        autoSwitch.Toggled += (_, _) =>
        {
            _rt.Settings.AutoStart = autoSwitch.IsOn;
            NativeMethods.SetAutoStart("LiteMon", Environment.ProcessPath ?? "", autoSwitch.IsOn);
            _rt.Save();
        };
        autoRow.Children.Add(autoSwitch);
        stack.Children.Add(Card(autoRow));

        var trayRow = RowPanel();
        trayRow.Children.Add(LabelCol(Loc.T("Settings.Tray"), Loc.T("Settings.TraySub")));
        var traySwitch = new ToggleSwitch { IsOn = _rt.Settings.MinimizeToTrayOnClose, Width = 44, Height = 26, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(traySwitch, Dock.Right);
        traySwitch.Toggled += (_, _) =>
        {
            _rt.Settings.MinimizeToTrayOnClose = traySwitch.IsOn;
            _rt.Save();
        };
        trayRow.Children.Add(traySwitch);
        stack.Children.Add(Card(trayRow));

        var minRow = RowPanel();
        minRow.Children.Add(LabelCol(Loc.T("Settings.StartMin"), Loc.T("Settings.StartMinSub")));
        var minSwitch = new ToggleSwitch { IsOn = _rt.Settings.StartMinimizedToTray, Width = 44, Height = 26, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(minSwitch, Dock.Right);
        minSwitch.Toggled += (_, _) =>
        {
            _rt.Settings.StartMinimizedToTray = minSwitch.IsOn;
            _rt.Save();
        };
        minRow.Children.Add(minSwitch);
        stack.Children.Add(Card(minRow));

        // 当前权限
        var privRow = RowPanel();
        privRow.Children.Add(Label(Loc.T("Settings.Privilege")));
        bool elevated = NativeMethods.IsElevated;
        privRow.Children.Add(new TextBlock
        {
            Text = elevated ? Loc.T("Settings.Elevated") : Loc.T("Settings.NotElevated"),
            FontSize = 12,
            Foreground = elevated ? B("Brush.Accent") : B("Brush.TextSub"),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = F(),
        });
        stack.Children.Add(Card(privRow));

        root.Content = stack;
        Content = root;
    }

    /// <summary>设置页是静态表单，重进页面时按新主题/新语言重建，无需消费快照。</summary>
    public override void Apply(LiteMon.Core.Models.Snapshot s) { }

    // ---------------- 辅助 ----------------

    private static Brush B(string key) => ThemeBrush.Get(key);
    private static FontFamily F() => (FontFamily)Application.Current.Resources["Font.Main"];

    /// <summary>卡片内一行：左标签 + 右控件（右控件靠 DockPanel.Dock=Right 停靠）。</summary>
    private static DockPanel RowPanel() => new() { LastChildFill = true, Margin = new Thickness(0, 4, 0, 4) };

    private static StackPanel LabelCol(string title, string sub)
    {
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(new TextBlock
        {
            Text = title, FontSize = 13, Foreground = B("Brush.Text"), FontFamily = F(),
        });
        sp.Children.Add(new TextBlock
        {
            Text = sub, FontSize = 11, Foreground = B("Brush.TextSub"),
            Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap, FontFamily = F(),
        });
        return sp;
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text, FontSize = 13, Foreground = B("Brush.Text"),
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 16, 0), FontFamily = F(),
    };

    private static TextBlock Section(string text) => new()
    {
        Text = text, FontSize = 11.5, FontWeight = FontWeights.SemiBold,
        Foreground = B("Brush.TextSub"), Margin = new Thickness(2, 14, 0, 6), FontFamily = F(),
    };

    private static Border Card(UIElement content) => new()
    {
        Background = B("Brush.Card"),
        BorderBrush = B("Brush.CardBorder"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(14, 10, 14, 10),
        Margin = new Thickness(0, 0, 0, 8),
        Child = content,
    };

    private static Color HexColor(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    /// <summary>三等分圆的 120° 扇形路径。</summary>
    private static Geometry PieSegment(double cx, double cy, double r, double startDeg, double endDeg)
    {
        var fig = new PathFigure { StartPoint = new Point(cx, cy), IsClosed = true };
        fig.Segments.Add(new LineSegment(Polar(cx, cy, r, startDeg), true));
        fig.Segments.Add(new ArcSegment
        {
            Point = Polar(cx, cy, r, endDeg),
            Size = new Size(r, r),
            IsLargeArc = (endDeg - startDeg) > 180,
            SweepDirection = SweepDirection.Clockwise,
        });
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        return geo;
    }

    private static Point Polar(double cx, double cy, double r, double deg)
    {
        double rad = (deg - 90) * Math.PI / 180.0;
        return new Point(cx + r * Math.Cos(rad), cy + r * Math.Sin(rad));
    }
}
