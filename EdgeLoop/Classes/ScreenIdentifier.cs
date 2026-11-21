using System;
using System.Linq;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace EdgeLoop.Classes
{
    /// <summary>
    /// "Identify screens": shows each screen's number large in its middle for a couple of seconds,
    /// matching the "Screen N" names in the launcher and Settings. The numbers can't be clicked and
    /// never take focus.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class ScreenIdentifier
    {
        public static readonly TimeSpan ShowFor = TimeSpan.FromSeconds(2.5);
        private const double Size = 220;

        public static void Show()
        {
            // Window positions are in device-independent units at the system DPI
            var reference = Application.Current?.MainWindow;
            double scale = reference != null ? VisualTreeHelper.GetDpi(reference).DpiScaleX : 1.0;

            foreach (var viewer in WindowServices.GetAllScreenViewers().Where(v => v.Screen != null))
            {
                var bounds = viewer.Screen.Bounds;
                var window = CreateWindow(viewer);
                window.Left = (bounds.X + bounds.Width / 2.0) / scale - Size / 2;
                window.Top = (bounds.Y + bounds.Height / 2.0) / scale - Size / 2;
                window.SourceInitialized += (s, e) => MakeClickThrough(window);
                window.Show();

                var timer = new DispatcherTimer { Interval = ShowFor };
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    window.Close();
                };
                timer.Start();
            }
        }

        private static Window CreateWindow(ScreenViewer viewer)
        {
            var accent = ThemeBrushes.Get("AccentPrimary", Color.FromRgb(0xFF, 0x14, 0x93));
            var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            panel.Children.Add(new TextBlock
            {
                Text = viewer.ScreenIndex.ToString(),
                FontSize = 110,
                FontWeight = FontWeights.Bold,
                Foreground = accent,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, -10, 0, 0)
            });
            panel.Children.Add(new TextBlock
            {
                Text = viewer.Details,
                FontSize = 13,
                Foreground = ThemeBrushes.Get("TextSecondary", Color.FromRgb(0xFF, 0xB6, 0xD9)),
                HorizontalAlignment = HorizontalAlignment.Center
            });

            return new Window
            {
                Title = viewer.DisplayName,
                Width = Size,
                Height = Size,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Topmost = true,
                Content = new Border
                {
                    Background = ThemeBrushes.Get("SurfaceBrush", Color.FromRgb(0x1A, 0x05, 0x10)),
                    BorderBrush = accent,
                    BorderThickness = new Thickness(3),
                    CornerRadius = new CornerRadius(24),
                    Child = panel
                }
            };
        }

        private static void MakeClickThrough(Window window)
        {
            var handle = new WindowInteropHelper(window).Handle;
            int style = WindowServices.GetWindowLong(handle, WindowServices.GWL_EXSTYLE);
            WindowServices.SetWindowLong(handle, WindowServices.GWL_EXSTYLE,
                style | WindowServices.WS_EX_TRANSPARENT | WindowServices.WS_EX_TOOLWINDOW | WindowServices.WS_EX_NOACTIVATE);
        }
    }
}
