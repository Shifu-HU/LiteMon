using System.Windows;
using LiteMon.Wpf.Services;
using LiteMon.Wpf.Views;

namespace LiteMon.Wpf;

public partial class App : Application
{
    public static new App Current => (App)Application.Current;

    public AppRuntime Runtime { get; } = new();
    internal TrayIcon? Tray { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Runtime.Start();
        ThemeManager.Apply(Runtime.Settings);

        // 全局兜底：任何 UI 线程异常不崩溃
        DispatcherUnhandledException += (_, ex) =>
        {
            ex.Handled = true;
            try { Log.Error("UI", ex.Exception.ToString()); } catch { }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            try { Log.Error("APP", ex.ExceptionObject?.ToString() ?? "?"); } catch { }
        };

        // 合盖睡眠/唤醒：重建曲线 PDH query 与历史（唤醒后实例易失效，否则曲线恒 0）
        Microsoft.Win32.SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode != Microsoft.Win32.PowerModes.Resume) return;
            try { Runtime.Monitor.InvalidateCurves(); } catch { }
        };

        // 托盘（左键 = 打开主窗口）
        Tray = new TrayIcon(() => OpenMain(), Dispatcher);
        var menu = new System.Windows.Controls.ContextMenu();
        var miOpen = new System.Windows.Controls.MenuItem { Header = Services.Loc.T("Tray.Open") };
        miOpen.Click += (_, _) => OpenMain();
        var miPause = new System.Windows.Controls.MenuItem { Header = Services.Loc.T("Tray.Pause") };
        miPause.Click += (_, _) => Runtime.SetPaused(!Runtime.Settings.Paused);
        var miExit = new System.Windows.Controls.MenuItem { Header = Services.Loc.T("Tray.Exit") };
        miExit.Click += (_, _) => ExitApp();
        menu.Items.Add(miOpen);
        menu.Items.Add(miPause);
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(miExit);
        Tray.SetMenu(menu);

        // 启动是否最小化到托盘（后台静默运行）
        if (Runtime.Settings.StartMinimizedToTray) { /* 只留托盘，不开窗口 */ }
        else OpenMain();
    }

    internal MainWindow? MainWindow2 { get; private set; }

    internal void OpenMain()
    {
        if (MainWindow2 == null || !MainWindow2.IsLoaded)
        {
            MainWindow2 = new MainWindow();
            MainWindow2.Closed += (_, _) => MainWindow2 = null;
            MainWindow2.Show();
        }
        else
        {
            if (MainWindow2.WindowState == WindowState.Minimized) MainWindow2.WindowState = WindowState.Normal;
            MainWindow2.Activate();
        }
    }

    internal void ExitApp()
    {
        Runtime.Shutdown();
        Tray?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Runtime.Dispose();
        base.OnExit(e);
    }
}
