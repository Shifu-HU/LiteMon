using System.Windows;
using System.Windows.Media;

namespace LiteMon.Wpf;

/// <summary>主题画刷/颜色快速获取。</summary>
public static class ThemeBrush
{
    public static Brush Get(string key)
    {
        try
        {
            if (Application.Current.Resources[key] is Brush b) return b;
            if (Application.Current.Resources[key] is Color c) return new SolidColorBrush(c);
        }
        catch { }
        return Brushes.SteelBlue;
    }

    public static Color GetColor(string key)
    {
        try
        {
            if (Application.Current.Resources[key] is SolidColorBrush b) return b.Color;
            if (Application.Current.Resources[key] is Color c) return c;
        }
        catch { }
        return Colors.SteelBlue;
    }

    public static Brush GetAlpha(string key, double alpha)
    {
        var c = GetColor(key);
        return new SolidColorBrush(Color.FromArgb((byte)(alpha * 255), c.R, c.G, c.B));
    }
}
