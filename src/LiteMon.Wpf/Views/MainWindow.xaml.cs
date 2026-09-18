using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LiteMon.Core;
using LiteMon.Core.Models;
using LiteMon.Wpf.Services;

namespace LiteMon.Wpf.Views;

/// <summary>主窗口：顶栏状态 Chip（含速度/显存）+ 导航 + 内容页。</summary>
public partial class MainWindow : Window
{
    private readonly AppRuntime _rt;
    private readonly System.Windows.Threading.DispatcherTimer _uiTimer;
    private readonly System.Windows.Threading.DispatcherTimer _chartTimer;   // 0.1s 曲线专用
    private Snapshot? _last;
    private bool _showing;

    private readonly System.Collections.Generic.Dictionary<string, Pages.BasePage> _pages = new();

    public MainWindow()
    {
        InitializeComponent();
        _rt = App.Current.Runtime;

        _uiTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(Math.Max(0.5, _rt.Settings.RefreshSeconds)),
        };
        _uiTimer.Tick += (_, _) => PullLatest();
        _rt.IntervalChanged += s => Dispatcher.Invoke(() =>
        {
            _uiTimer.Interval = TimeSpan.FromSeconds(Math.Max(0.5, s));
            _uiTimer.Stop();
            _uiTimer.Start();
            SubTitle.Text = Services.Loc.T("App.Sub", s.ToString("0.#"));
        });
        SubTitle.Text = Services.Loc.T("App.Sub", _rt.Settings.RefreshSeconds.ToString("0.#"));
        Title = Services.Loc.T("App.Title");
        BtnPause.Content = _rt.Settings.Paused ? Services.Loc.T("App.Resume") : Services.Loc.T("App.Pause");
        BtnPause.ToolTip = Services.Loc.T("App.PauseTip");
        BtnClose.ToolTip = Services.Loc.T("App.CloseTip");
        NavSettings.ToolTip = Services.Loc.T("App.SettingsTip");
        ApplyFlowDirection();
        _uiTimer.Start();

        // 曲线专用快速定时器：固定 0.1s，只刷新当前页图表
        _chartTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _chartTimer.Tick += (_, _) =>
        {
            if (!_showing) return;
            if (Content.Content is Pages.BasePage p)
                p.UpdateChart(_rt.Monitor);
        };
        _chartTimer.Start();

        Loaded += (_, _) =>
        {
            _showing = true;
            // 恢复上次窗口尺寸（首次用 XAML 默认 1240×860；clamp 到当前屏幕工作区）
            var sw = SystemParameters.WorkArea;
            if (_rt.Settings.MainW > 0 && _rt.Settings.MainH > 0)
            {
                Width = Math.Min(_rt.Settings.MainW, sw.Width);
                Height = Math.Min(_rt.Settings.MainH, sw.Height);
            }
            Navigate("Cpu");
        };
        Closed += (_, _) => { _uiTimer.Stop(); _chartTimer.Stop(); _showing = false; };

        // 主题/配色切换后，构造时缓存了画刷的页面与浮岛按新主题重建
        Services.ThemeManager.Applied += OnThemeApplied;
        Closed += (_, _) => Services.ThemeManager.Applied -= OnThemeApplied;
    }

    private void OnThemeApplied()
    {
        Dispatcher.Invoke(() =>
        {
            _pages.Clear();                        // 页面卡片/图表缓存色全部作废
            Content.Content = null;
            Fab.Bind(null);
            if (_showing)
            {
                // 回到当前页（新实例 → 新主题/新语言文案），切换语言后停在设置页而非跳回 CPU
                var tag = string.IsNullOrEmpty(_currentTag) ? "Cpu" : _currentTag;
                // 页签选中态错误会让"再点设置"不触发 Checked，这里强制同步
                var rb = FindRadio(this, tag);
                if (rb != null && rb.IsChecked != true) rb.IsChecked = true;
                Navigate(tag, animate: false);
                FadeIn((UIElement)Content.Content);   // 重建也淡入，避免整页闪白
            }
            Fab.UpdateColors();
            ApplyFlowDirection();   // 阿拉伯语切到 RTL
            // 语言/主题共用重建机制：窗口级文案随之刷新
            Title = Services.Loc.T("App.Title");
            SubTitle.Text = Services.Loc.T("App.Sub", _rt.Settings.RefreshSeconds.ToString("0.#"));
            BtnPause.Content = _rt.Settings.Paused ? Services.Loc.T("App.Resume") : Services.Loc.T("App.Pause");
            BtnPause.ToolTip = Services.Loc.T("App.PauseTip");
            BtnClose.ToolTip = Services.Loc.T("App.CloseTip");
            NavSettings.ToolTip = Services.Loc.T("App.SettingsTip");
        });
    }

    public bool IsExiting => _rt.IsExiting;

    private void PullLatest()
    {
        if (!_showing) return;
        var s = _rt.Monitor.Latest;
        if (s == null || ReferenceEquals(s, _last)) return;
        _last = s;

        // 顶栏 Chip：CPU % / GPU % / VRAM % / RAM %（均只显示占用率）
        ChipCpuText.Text = "CPU " + s.Cpu.TotalUsage.ToString("0") + "%";
        ChipGpuText.Text = s.Gpu.IsAvailable ? "GPU " + s.Gpu.Usage.ToString("0") + "%" : "GPU —";
        ChipGpuVramText.Text = s.Gpu.IsAvailable
            ? "VRAM " + (s.Gpu.VramTotalBytes > 0 ? s.Gpu.VramPercent.ToString("0") + "%" : "—")
            : "VRAM —";
        ChipRamText.Text = "RAM " + s.Memory.UsagePercent.ToString("0") + "%";

        // 可移动盘导航页签增减（DISK2…）
        UpdateRemovableNav(s);

        if (Content.Content is Pages.BasePage p)
            p.Apply(s);
    }

    // ---------------- 可移动盘动态页签 ----------------

    private readonly System.Collections.Generic.HashSet<string> _removableTabs = new(StringComparer.OrdinalIgnoreCase);

    private void UpdateRemovableNav(Snapshot s)
    {
        var removable = s.Disks
            .Where(d => d.Kind == DiskKind.Removable)
            .Select(d => d.Letter)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var current = _removableTabs.ToList();
        if (current.Count == removable.Count && !current.Except(removable, StringComparer.OrdinalIgnoreCase).Any())
            return;

        foreach (var gone in current.Where(c => !removable.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList())
        {
            var rb = FindRadio(this, "Disk:" + gone);
            if (rb != null && rb.Parent is System.Windows.Controls.Panel panel)
                panel.Children.Remove(rb);
            _removableTabs.Remove(gone);
            _pages.Remove("Disk:" + gone);
            if (Content.Content is Pages.DiskPage dp && (dp.Tag as string) == "Disk:" + gone)
                SelectNav("Disk");
        }

        for (int i = 0; i < removable.Count; i++)
        {
            var letter = removable[i];
            if (_removableTabs.Contains(letter)) continue;
            var number = 2 + removable.Take(i).Count(l => _removableTabs.Contains(l));
            var rb = new RadioButton
            {
                Style = (Style)FindResource("NavItem"),
                Content = "DISK" + number + " " + letter,
                Tag = "Disk:" + letter,
                GroupName = "NavTabs",
                Margin = new Thickness(0, 0, 10, 0),
            };
            rb.Checked += Nav_Checked;
            NavRemovableHost.Children.Add(rb);
            _removableTabs.Add(letter);
        }
    }

    // ---------------- 导航 ----------------

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string tag && IsLoaded)
            Navigate(tag);
    }

    /// <summary>阿拉伯语等 RTL 语言：整窗镜像（WPF 自动翻转布局）。</summary>
    private void ApplyFlowDirection()
    {
        FlowDirection = Services.Loc.IsRtl
            ? System.Windows.FlowDirection.RightToLeft
            : System.Windows.FlowDirection.LeftToRight;
    }

    private string _currentTag = "Cpu";

    private void Navigate(string tag, bool animate = true)
    {
        _currentTag = tag;
        var page = GetPage(tag);
        if (_last != null) page.Apply(_last);
        Content.Content = page;
        // 浮岛跟随页面：有可结束列表则绑定，否则隐藏（WIFI/设置 等页）
        Fab.Bind(page.KillableList);
        if (animate && _showing) FadeIn(page);
    }

    /// <summary>切页淡入（180ms），避免生硬闪现。</summary>
    private void FadeIn(UIElement page)
    {
        page.Opacity = 0;
        var anim = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut,
            },
        };
        page.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    private Pages.BasePage GetPage(string key)
    {
        if (_pages.TryGetValue(key, out var cached)) return cached;
        Pages.BasePage p = key switch
        {
            "Gpu" => new Pages.GpuPage(),
            "Memory" => new Pages.MemoryPage(),
            "Wifi" => new Pages.WifiPage(),
            "Disk" => new Pages.DiskPage(null) { Tag = "Disk" },
            "Settings" => new Pages.SettingsPage { Tag = "Settings" },
            _ when key.StartsWith("Disk:", StringComparison.OrdinalIgnoreCase)
                => new Pages.DiskPage(key.Substring(5)) { Tag = key },
            _ => new Pages.CpuPage(),
        };
        _pages[key] = p;
        return p;
    }

    public void NavigateToSettings() => SelectNav("Settings");

    private void SelectNav(string tag)
    {
        var rb = FindRadio(this, tag);
        if (rb != null) { rb.IsChecked = true; }
        else if (IsLoaded) Navigate(tag);
    }

    private static RadioButton? FindRadio(DependencyObject root, string tag)
    {
        int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (c is RadioButton rb && (string?)rb.Tag == tag) return rb;
            var r = FindRadio(c, tag);
            if (r != null) return r;
        }
        return null;
    }

    // ---------------- 窗口事件 ----------------

    private void BtnPause_Click(object sender, RoutedEventArgs e)
    {
        var p = !_rt.Settings.Paused;
        _rt.SetPaused(p);
        BtnPause.Content = p ? Services.Loc.T("App.Resume") : Services.Loc.T("App.Pause");
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_rt.Settings.MinimizeToTrayOnClose && !_rt.IsExiting)
        {
            e.Cancel = true;
            Hide();
        }
        else
        {
            _rt.Settings.MainW = Width;
            _rt.Settings.MainH = Height;
            _rt.Save();
        }
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        // 最小化只进任务栏；不隐藏（隐藏会像"窗口消失"）。
        // 关闭是否驻留托盘由 MinimizeToTrayOnClose 在 Closing 里处理。
    }
}
