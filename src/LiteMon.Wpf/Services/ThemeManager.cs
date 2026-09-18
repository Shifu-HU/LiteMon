using System;
using System.Windows;
using System.Windows.Media;
using LiteMon.Core.Settings;

namespace LiteMon.Wpf.Services;

/// <summary>
/// 主题管理：深/浅/跟随系统（读取注册表 AppsUseLightTheme），9 套配色方案。
/// 全部通过替换 Application.Resources 中的 DynamicResource 画刷实现，窗口用 DynamicResource 绑定即自动换色。
/// </summary>
public static class ThemeManager
{
    /// <summary>主题/配色已重新应用（构造时缓存颜色的控件应据此刷新）。</summary>
    public static event Action? Applied;

    public static bool SystemDark()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", false);
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return true; }
    }

    public static bool ResolveDark(ThemeMode mode) =>
        mode switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => SystemDark(),
        };

    /// <summary>主入口：应用主题。</summary>
    public static void Apply(AppSettings s)
    {
        var scheme = ColorSchemes.ByName(s.Scheme);
        bool dark = ResolveDark(s.Theme);
        Apply(dark, scheme.C1, scheme.C2, scheme.C3);
    }

    public static void Apply(bool dark, string c1, string c2, string c3)
    {
        var res = Application.Current.Resources;
        var accent = Hex(c1);
        var accent2 = Hex(c2);
        var accent3 = Hex(c3);

        res["IsDark"] = dark;

        if (dark)
        {
            // 深色：深海军蓝底 + 方案色染色（参考 FClash/Surfboard 风格）
            Set(res, "Brush.WindowBg", Blend(Hex("#141622"), accent, 0.06, 0xF2));
            Set(res, "Brush.WindowBgSolid", Blend(Hex("#141622"), accent, 0.06));
            Set(res, "Brush.Card", Blend(Hex("#1A2332"), accent, 0.10));
            Set(res, "Brush.CardBorder", Blend(Hex("#33465F"), accent, 0.18));
            Set(res, "Brush.CardHover", Blend(Hex("#223146"), accent, 0.12));
            Set(res, "Brush.TopBar", Blend(Hex("#101A2E"), accent, 0.08, 0xF2));
            Set(res, "Brush.NavBar", Blend(Hex("#0D1526"), accent, 0.08, 0xF2));
            Set(res, "Brush.MenuBg", Blend(Hex("#162030"), accent, 0.10, 0xF5));

            Set(res, "Brush.Text", Hex("#E9F1FF"));
            Set(res, "Brush.TextSub", Hex("#8A97B5"));
            Set(res, "Brush.TextDim", Hex("#5D6B8A"));

            Set(res, "Brush.Accent", accent);
            Set(res, "Brush.Accent2", accent2);
            Set(res, "Brush.Accent3", accent3);
            Set(res, "Brush.AccentSoft", Darker(accent, 0.25));

            Set(res, "Brush.Hover", ARGB(0x14, 0xFF, 0xFF, 0xFF));
            Set(res, "Brush.Press", ARGB(0x1F, 0xFF, 0xFF, 0xFF));
            Set(res, "Brush.InputBg", ARGB(0x0F, 0xFF, 0xFF, 0xFF));
            Set(res, "Brush.InputBorder", ARGB(0x29, 0xFF, 0xFF, 0xFF));
            Set(res, "Brush.Divider", ARGB(0x1A, 0xFF, 0xFF, 0xFF));
            Set(res, "Brush.Scroll", ARGB(0x2E, 0xFF, 0xFF, 0xFF));
            Set(res, "Brush.Track", Blend(Hex("#293042"), accent, 0.12));
            Set(res, "Brush.ChartGrid", ARGB(0x16, 0xFF, 0xFF, 0xFF));
        }
        else
        {
            // 浅色：淡蓝灰底 + 方案色
            Set(res, "Brush.WindowBg", Blend(Hex("#F2F5FA"), accent, 0.10, 0xF2));
            Set(res, "Brush.WindowBgSolid", Blend(Hex("#F2F5FA"), accent, 0.10));
            Set(res, "Brush.Card", Hex("#FFFFFF"));
            Set(res, "Brush.CardBorder", Blend(Hex("#D4DDEA"), accent, 0.12));
            Set(res, "Brush.CardHover", Blend(Hex("#EDF2F8"), accent, 0.08));
            Set(res, "Brush.TopBar", Blend(Hex("#E8EDF5"), accent, 0.08, 0xF2));
            Set(res, "Brush.NavBar", Blend(Hex("#E2E9F3"), accent, 0.08, 0xF2));
            Set(res, "Brush.MenuBg", Blend(Hex("#FFFFFF"), accent, 0.04, 0xF8));

            Set(res, "Brush.Text", Hex("#1A1B20"));
            Set(res, "Brush.TextSub", Hex("#565B66"));
            Set(res, "Brush.TextDim", Hex("#8A90A0"));

            Set(res, "Brush.Accent", Darker(accent, 0.12));
            Set(res, "Brush.Accent2", accent2);
            Set(res, "Brush.Accent3", accent3);
            Set(res, "Brush.AccentSoft", Darker(accent, 0.30));

            Set(res, "Brush.Hover", ARGB(0x0A, 0x1A, 0x1B, 0x20));
            Set(res, "Brush.Press", ARGB(0x12, 0x1A, 0x1B, 0x20));
            Set(res, "Brush.InputBg", ARGB(0xE6, 0xFF, 0xFF, 0xFF));
            Set(res, "Brush.InputBorder", ARGB(0x38, 0x1A, 0x1B, 0x20));
            Set(res, "Brush.Divider", ARGB(0x12, 0x1A, 0x1B, 0x20));
            Set(res, "Brush.Scroll", ARGB(0x40, 0x1A, 0x1B, 0x20));
            Set(res, "Brush.Track", Hex("#E3E9F2"));
            Set(res, "Brush.ChartGrid", ARGB(0x14, 0x1A, 0x1B, 0x20));
        }

        // 器件分类色（深浅色都清晰）
        Set(res, "Brush.Cpu", dark ? Hex("#38BDF8") : Hex("#0284C7"));
        Set(res, "Brush.Gpu", dark ? Hex("#A78BFA") : Hex("#7C3AED"));
        Set(res, "Brush.Ram", dark ? Hex("#34D399") : Hex("#059669"));
        Set(res, "Brush.Net", dark ? Hex("#22D3EE") : Hex("#0891B2"));

        Applied?.Invoke();
    }

    private static void Set(System.Windows.ResourceDictionary res, string key, Color c) =>
        res[key] = new SolidColorBrush(c);

    private static Color Hex(string s)
    {
        s = s.TrimStart('#');
        if (s.Length == 6) return Color.FromRgb((byte)Convert.ToInt32(s[0..2], 16), (byte)Convert.ToInt32(s[2..4], 16), (byte)Convert.ToInt32(s[4..6], 16));
        if (s.Length == 8) return Color.FromArgb((byte)Convert.ToInt32(s[0..2], 16), (byte)Convert.ToInt32(s[2..4], 16), (byte)Convert.ToInt32(s[4..6], 16), (byte)Convert.ToInt32(s[6..8], 16));
        return Colors.Gray;
    }

    private static Color ARGB(byte a, byte r, byte g, byte b) => Color.FromArgb(a, r, g, b);

    private static Color Blend(Color baseC, Color tint, double t, byte alpha = 0xFF)
    {
        var r = (byte)(baseC.R + (tint.R - baseC.R) * t);
        var g = (byte)(baseC.G + (tint.G - baseC.G) * t);
        var b = (byte)(baseC.B + (tint.B - baseC.B) * t);
        return Color.FromArgb(alpha, r, g, b);
    }

    private static Color Darker(Color c, double t) =>
        Color.FromRgb((byte)(c.R * (1 - t)), (byte)(c.G * (1 - t)), (byte)(c.B * (1 - t)));
}
