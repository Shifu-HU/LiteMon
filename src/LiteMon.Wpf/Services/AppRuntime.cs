using System;
using System.Windows.Threading;
using LiteMon.Core.Scheduling;
using LiteMon.Core.Settings;

namespace LiteMon.Wpf.Services;

/// <summary>全局运行时：设置、监控服务、主题。窗口共享。</summary>
public sealed class AppRuntime : IDisposable
{
    public AppSettings Settings { get; private set; } = new();
    public MonitorService Monitor { get; } = new();

    public void Start()
    {
        Settings = AppSettings.Load();
        Loc.Language = Settings.Language;   // 页面构造前定语言
        Monitor.IntervalSeconds = Settings.RefreshSeconds;
        Monitor.CollectProcesses = Settings.CollectProcesses;
        Monitor.Paused = Settings.Paused;
        Monitor.Start();
        ThemeManager.Apply(Settings);
    }

    public void Save() => Settings.Save();

    /// <summary>刷新间隔变化（秒）。UI 层据此同步自己的定时器。</summary>
    public event Action<double>? IntervalChanged;

    public void SetInterval(double s)
    {
        Settings.RefreshSeconds = s;
        Monitor.IntervalSeconds = s;
        Save();
        IntervalChanged?.Invoke(s);
    }

    public void SetPaused(bool p)
    {
        Settings.Paused = p;
        Monitor.Paused = p;
        Save();
    }

    /// <summary>应用正在真正退出（区别于窗口隐藏驻留）。</summary>
    public bool IsExiting { get; private set; }

    public void Shutdown()
    {
        IsExiting = true;
    }

    public void Dispose()
    {
        try { Monitor.Dispose(); } catch { }
    }
}
