using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LiteMon.Wpf.Controls;

/// <summary>
/// 右下角悬浮"结束进程"操作岛（挂在主窗口层，随页面切换绑定对应列表）。
/// 点选行后按钮亮起，再次点击武装，第三次点击执行结束——防误触。
/// </summary>
public class KillFab : Border
{
    private AppListPanel? _list;
    private readonly TextBlock _label;
    private bool _armed;
    private bool _suppressOnce;                    // 结束成功后那次 ClearSelection 不复位文字
    private System.Windows.Threading.DispatcherTimer? _resultTimer;

    public KillFab()
    {
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(14, 8, 14, 8);
        Background = ThemeBrush.GetAlpha("Brush.Accent", 0.30);
        BorderBrush = ThemeBrush.GetAlpha("Brush.Accent", 0.85);
        BorderThickness = new Thickness(1.5);
        Cursor = Cursors.Hand;
        Opacity = 0.92;
        VerticalAlignment = VerticalAlignment.Bottom;
        HorizontalAlignment = HorizontalAlignment.Right;
        Margin = new Thickness(0, 0, 22, 18);
        IsHitTestVisible = true;

        _label = new TextBlock
        {
            Text = Services.Loc.T("Fab.EndTask"),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrush.Get("Brush.Text"),
            FontFamily = (FontFamily)Application.Current.Resources["Font.Main"],
        };
        Child = _label;

        MouseEnter += (_, _) =>
        {
            if (!_armed) { Opacity = 1.0; Background = ThemeBrush.GetAlpha("Brush.Accent", 0.40); }
        };
        MouseLeave += (_, _) =>
        {
            if (!_armed) { Opacity = 0.92; Background = ThemeBrush.GetAlpha("Brush.Accent", 0.30); }
        };
        MouseLeftButtonUp += (_, _) => Activate();
    }

    /// <summary>主题/配色切换后按当前选中状态重新着色。</summary>
    public void UpdateColors() => UpdateState(_list?.SelectedPid ?? 0);

    /// <summary>绑定目标列表（换页时调用；null 隐藏浮岛）。</summary>
    public void Bind(AppListPanel? list)
    {
        if (_list != null) _list.SelectionChanged -= OnSelection;
        _list = list;
        if (_list == null)
        {
            Visibility = Visibility.Collapsed;
            return;
        }
        _list.SelectionChanged += OnSelection;
        Visibility = Visibility.Visible;
        UpdateState(_list.SelectedPid);
    }

    private void OnSelection(int pid) => Dispatcher.Invoke(() =>
    {
        if (_suppressOnce) { _suppressOnce = false; return; }   // 结果提示期间忽略复位
        UpdateState(pid);
    });

    /// <summary>结果提示显示 2.5 秒后恢复到当前选中状态对应的外观。</summary>
    private void ShowResult(string text)
    {
        _label.Text = text;
        _resultTimer?.Stop();
        _resultTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _resultTimer.Tick += (_, _) =>
        {
            _resultTimer.Stop();
            UpdateState(_list?.SelectedPid ?? 0);
        };
        _resultTimer.Start();
    }

    private void UpdateState(int pid)
    {
        _armed = false;
        if (pid > 0)
        {
            Opacity = 0.98;
            Background = ThemeBrush.GetAlpha("Brush.Red", 0.30);
            BorderBrush = ThemeBrush.GetAlpha("Brush.Red", 0.85);
            _label.Text = Services.Loc.T("Fab.EndTaskPid", pid);
            _label.Foreground = ThemeBrush.Get("Brush.Red");
        }
        else
        {
            Opacity = 0.92;
            Background = ThemeBrush.GetAlpha("Brush.Accent", 0.30);
            BorderBrush = ThemeBrush.GetAlpha("Brush.Accent", 0.85);
            _label.Text = Services.Loc.T("Fab.EndTask");
            _label.Foreground = ThemeBrush.Get("Brush.Text");
        }
    }

    private void Activate()
    {
        var pid = _list?.SelectedPid ?? 0;
        if (pid <= 0)
        {
            _label.Text = Services.Loc.T("Fab.PickFirst");
            return;
        }
        if (!_armed)
        {
            _armed = true;
            Background = ThemeBrush.GetAlpha("Brush.Red", 0.42);
            _label.Text = Services.Loc.T("Fab.ConfirmKill", pid);
            return;
        }

        _armed = false;
        try
        {
            var proc = Process.GetProcessById(pid);
            var name = proc.ProcessName;
            proc.Kill(true);   // 结束整棵进程树
            _suppressOnce = true;            // 本次清空选中不触发复位，让结果提示可见
            _list?.ClearSelection();
            ShowResult(Services.Loc.T("Fab.Killed", name));    // 2.5s 后自动回到结束态
        }
        catch (Exception ex)
        {
            var msg = ex.Message + " " + (ex.InnerException?.Message ?? "");
            ShowResult(msg.Contains("访问被拒绝") || msg.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
                ? Services.Loc.T("Fab.KillDenied") : Services.Loc.T("Fab.KillFailed"));
        }
        _label.Foreground = ThemeBrush.Get("Brush.Text");
        Opacity = 0.92;
        Background = ThemeBrush.GetAlpha("Brush.Accent", 0.30);
        BorderBrush = ThemeBrush.GetAlpha("Brush.Accent", 0.85);
    }
}
