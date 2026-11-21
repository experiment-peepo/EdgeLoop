using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace EdgeLoop.Classes
{
    /// <summary>
    /// A small notice in the bottom-left corner of one screen, e.g. "Couldn't play “Title”". It's its own
    /// little window so it stays readable however see-through the video is, can't be clicked, never
    /// takes focus, and fades away after a few seconds. A new notice on a screen replaces the old one.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class OverlayNotice
    {
        public static readonly TimeSpan ShowFor = TimeSpan.FromSeconds(4);
        private static readonly TimeSpan FadeFor = TimeSpan.FromMilliseconds(400);
        private const double Width = 380;
        private const double Margin = 24;

        private static readonly Dictionary<string, Window> _open = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Shows a notice on the screen with this device name (\\.\DISPLAY2). Call on the UI thread.</summary>
        /// <param name="isError">Red accent for errors; pink for information.</param>
        public static void Show(string screenDevice, string headline, string detail, bool isError = true)
        {
            var screen = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s => string.Equals(s.DeviceName, screenDevice, StringComparison.OrdinalIgnoreCase))
                         ?? System.Windows.Forms.Screen.PrimaryScreen;
            if (screen == null) return;

            if (_open.TryGetValue(screen.DeviceName, out var previous))
                previous.Close();

            var window = CreateWindow(headline, detail, isError);
            _open[screen.DeviceName] = window;
            window.Closed += (s, e) =>
            {
                if (_open.TryGetValue(screen.DeviceName, out var current) && current == window)
                    _open.Remove(screen.DeviceName);
            };

            // Placed in real pixels once its size is known, so it sits in the corner at any scaling;
            // invisible until then so it doesn't flash elsewhere
            window.Opacity = 0;
            window.SourceInitialized += (s, e) => MakeClickThrough(window);
            window.ContentRendered += (s, e) =>
            {
                WindowServices.PlaceOnScreen(window, screen, WindowServices.ScreenAnchor.BottomLeft, Margin);
                window.Opacity = 1;
            };
            window.Show();

            var timer = new DispatcherTimer { Interval = ShowFor };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                var fade = new DoubleAnimation(0, FadeFor);
                fade.Completed += (_, _) => window.Close();
                window.BeginAnimation(UIElement.OpacityProperty, fade);
            };
            timer.Start();
        }

        private static Window CreateWindow(string headline, string detail, bool isError)
        {
            var accent = isError
                ? ThemeBrushes.Get("DangerBrush", Color.FromRgb(0xFF, 0x44, 0x44))
                : ThemeBrushes.Get("AccentPrimary", Color.FromRgb(0xFF, 0x14, 0x93));

            var text = new StackPanel { Margin = new Thickness(14, 10, 14, 12) };
            text.Children.Add(new TextBlock
            {
                Text = headline,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = ThemeBrushes.Get("TextPrimary", Colors.White),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            if (!string.IsNullOrWhiteSpace(detail))
            {
                text.Children.Add(new TextBlock
                {
                    Text = detail,
                    FontSize = 12,
                    Margin = new Thickness(0, 3, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = ThemeBrushes.Get("TextSecondary", Color.FromRgb(0xFF, 0xB6, 0xD9))
                });
            }

            var row = new DockPanel();
            var bar = new Border { Width = 4, Background = accent, CornerRadius = new CornerRadius(8, 0, 0, 8) };
            DockPanel.SetDock(bar, Dock.Left);
            row.Children.Add(bar);
            row.Children.Add(text);

            return new Window
            {
                Title = headline,
                Width = Width,
                SizeToContent = SizeToContent.Height,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Topmost = true,
                FontFamily = Application.Current?.TryFindResource("BodyFontFamily") as FontFamily ?? new FontFamily("Segoe UI"),
                Content = new Border
                {
                    Background = ThemeBrushes.Get("SurfaceRaisedBrush", Color.FromRgb(0x3D, 0x0A, 0x1F)),
                    BorderBrush = ThemeBrushes.Get("SubtleBorderBrush", Color.FromArgb(0x4D, 0xFF, 0x14, 0x93)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Child = row
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
