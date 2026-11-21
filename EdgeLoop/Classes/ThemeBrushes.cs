using System.Windows;
using System.Windows.Media;

namespace EdgeLoop.Classes
{
    /// <summary>Brushes from the theme (Themes/Colors.xaml) for code that draws or converts colours.</summary>
    internal static class ThemeBrushes
    {
        /// <summary>The theme brush called <paramref name="key"/>, or <paramref name="fallback"/> outside the app (tests, design time).</summary>
        public static Brush Get(string key, Color fallback) =>
            Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
    }
}
