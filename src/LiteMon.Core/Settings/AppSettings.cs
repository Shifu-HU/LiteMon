using System.IO;
using System.Text.Json;

namespace LiteMon.Core.Settings;

/// <summary>外观模式：深色/浅色/跟随系统。</summary>
public enum ThemeMode { System, Dark, Light }

/// <summary>配色方案（与参考项目一致的 8 套 + 默认深海军蓝）。</summary>
public static class ColorSchemes
{
    /// <summary>
    /// 配色方案。<paramref name="Id"/> 是**稳定的持久化键**（与语言无关，改名/翻译不影响已存设置）；
    /// 显示名由 Ui 层通过 Loc.T("Scheme." + Id) 取，实现多语言。
    /// </summary>
    public sealed record Scheme(string Id, string C1, string C2, string C3);

    public static readonly Scheme[] All =
    {
        new("navy",   "#3B82F6", "#38BDF8", "#818CF8"),
        new("mist",   "#B5C5D7", "#D4B5D7", "#E8E7E9"),
        new("sand",   "#EBD7C6", "#D2D7B8", "#EEC8C4"),
        new("sky",    "#8DC5F2", "#EAB2E6", "#F0F5CA"),
        new("matcha", "#DFE691", "#F3F1DA", "#FFFFFF"),
        new("steel",  "#B4C1D4", "#CFD3DD", "#CFD3DD"),
        new("mint",   "#ABEBC7", "#CAEBC7", "#FFFFFF"),
        new("cream",  "#F5EAD7", "#F5D7D8", "#F5D7D8"),
        new("lilac",  "#D7CDF5", "#F5CDF0", "#F5CDF0"),
    };

    /// <summary>按 Id 取配色；兼容旧的（中文名）存档值。</summary>
    public static Scheme ByName(string name)
    {
        var hit = Array.Find(All, s => s.Id == name);
        if (hit != null) return hit;
        // 旧版本存档存的是中文显示名 → 按顺序回退到对应方案
        int legacy = Array.FindIndex(LegacyNames, n => n == name);
        return legacy >= 0 && legacy < All.Length ? All[legacy] : All[0];
    }

    /// <summary>历史版本用过的中文名（仅用于读取旧存档，顺序与 All 一致）。</summary>
    private static readonly string[] LegacyNames =
    {
        "默认·深海军蓝", "浅蓝灰·紫粉", "暖米沙·豆沙绿", "天蓝·柔粉", "抹茶绿·米白",
        "冷蓝灰·浅灰", "薄荷绿·黄绿", "奶油米·肉粉", "淡紫丁香·浅粉",
    };
}

/// <summary>悬浮窗显示项开关。</summary>
public class FloatingItems
{
    public bool Cpu { get; set; } = true;
    public bool Gpu { get; set; } = true;
    public bool Memory { get; set; } = true;
    public bool Network { get; set; } = false;
    public bool Frequencies { get; set; } = false;
    public bool PerCore { get; set; } = false;
    public bool Temperatures { get; set; } = false;
}

/// <summary>详情页显示项。</summary>
public class DetailItems
{
    public bool HistoryChart { get; set; } = true;
    public bool PerCore { get; set; } = true;
    public bool Apps { get; set; } = true;
    public bool Temps { get; set; } = true;
    public bool Power { get; set; } = true;
    public bool Frequencies { get; set; } = true;
}

/// <summary>应用设置（JSON 持久化到 %LOCALAPPDATA%\LiteMon\settings.json）。</summary>
public class AppSettings
{
    // 采集
    public double RefreshSeconds { get; set; } = 1.0;
    public bool Paused { get; set; }

    // 悬浮窗
    public bool FloatingVisible { get; set; } = true;
    public double FloatingOpacity { get; set; } = 0.92;
    public bool FloatingClickThrough { get; set; }
    public bool FloatingTopmost { get; set; } = true;
    public double FloatingX { get; set; } = -1;   // <0 = 屏幕右上角默认
    public double FloatingY { get; set; } = -1;
    public FloatingItems FloatingItems { get; set; } = new();

    // 外观
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public string Scheme { get; set; } = "";
    public bool CompactFloating { get; set; }
    /// <summary>界面语言：zh（默认）/ en / ja。</summary>
    public string Language { get; set; } = "zh";

    // 详情窗口
    public double MainX { get; set; } = -1;
    public double MainY { get; set; } = -1;
    public double MainW { get; set; } = 1060;
    public double MainH { get; set; } = 720;
    public DetailItems DetailItems { get; set; } = new();

    // 其他
    public bool AutoStart { get; set; }
    public bool StartMinimizedToTray { get; set; }
    public bool CollectProcesses { get; set; } = true;
    public bool MinimizeToTrayOnClose { get; set; } = true;

    public static AppSettings Load(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
                if (s != null)
                {
                    s.FloatingItems ??= new FloatingItems();
                    s.DetailItems ??= new DetailItems();
                    return s;
                }
            }
        }
        catch { }
        return new AppSettings();
    }

    public void Save(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiteMon", "settings.json");
}
