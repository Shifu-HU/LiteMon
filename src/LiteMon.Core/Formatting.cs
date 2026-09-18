using System;
using System.Globalization;

namespace LiteMon.Core;

/// <summary>数字格式化帮助（避免每个绑定都新建格式化器）。</summary>
public static class Formatting
{
    private static readonly CultureInfo Cn = CultureInfo.InvariantCulture;

    public static string FormatBytes(double bytes)
    {
        if (bytes < 0) bytes = 0;
        if (bytes >= 1024 * 1024 * 1024) return (bytes / (1024 * 1024 * 1024)).ToString("0.00", Cn) + " GB";
        if (bytes >= 1024 * 1024) return (bytes / (1024 * 1024)).ToString("0.00", Cn) + " MB";
        if (bytes >= 1024) return (bytes / 1024).ToString("0.0", Cn) + " KB";
        return ((long)bytes).ToString(Cn) + " B";
    }

    public static string FormatBytesShort(double bytes)
    {
        if (bytes < 0) bytes = 0;
        if (bytes >= 1024 * 1024 * 1024) return (bytes / (1024 * 1024 * 1024)).ToString("0.0", Cn) + "G";
        if (bytes >= 1024 * 1024) return (bytes / (1024 * 1024)).ToString("0", Cn) + "M";
        if (bytes >= 1024) return (bytes / 1024).ToString("0", Cn) + "K";
        return ((long)bytes).ToString(Cn) + "B";
    }

    public static string FormatSpeed(double bytesPerSec) => FormatBytes(bytesPerSec) + "/s";

    public static string FormatPercent(double p) => p.ToString("0", Cn) + "%";
    public static string FormatPercent1(double p) => p.ToString("0.0", Cn) + "%";
    public static string FormatMHz(double mhz) => Math.Round(mhz).ToString("0", Cn) + " MHz";
    public static string FormatWatts(double w) => w.ToString("0.0", Cn) + " W";
    public static string FormatCelsius(double c) => c.ToString("0", Cn) + " ℃";
}
