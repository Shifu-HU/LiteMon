using System.IO;

namespace LiteMon.Wpf.Services;

/// <summary>极简文件日志（%LOCALAPPDATA%\LiteMon\log.txt，最多 2MB 轮换）。</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiteMon", "log.txt");

    public static void Info(string tag, string msg) => Write("INFO", tag, msg);
    public static void Error(string tag, string msg) => Write("ERR ", tag, msg);

    private static void Write(string lvl, string tag, string msg)
    {
        try
        {
            lock (Gate)
            {
                var p = Path;
                var dir = System.IO.Path.GetDirectoryName(p);
                if (dir != null) Directory.CreateDirectory(dir);
                if (File.Exists(p) && new FileInfo(p).Length > 2 * 1024 * 1024)
                    File.WriteAllText(p, ""); // 轮换
                File.AppendAllText(p, $"{DateTime.Now:HH:mm:ss} {lvl} [{tag}] {msg}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
