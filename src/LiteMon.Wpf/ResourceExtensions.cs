using System.Windows;
using System.Windows.Media;

namespace LiteMon.Wpf
{
    internal static class ResourceExtensions
    {
        /// <summary>取资源画刷（找不到时回退 SteelBlue）。</summary>
        public static object FindCompatibleColor2(this ResourceDictionary res, string key)
        {
            try
            {
                if (res[key] is SolidColorBrush b) return b;
                if (res[key] is Color c) return new SolidColorBrush(c);
            }
            catch { }
            return new SolidColorBrush(Colors.SteelBlue);
        }
    }
}
