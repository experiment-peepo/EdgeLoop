/*
	Copyright (C) 2026 Llamasoft

	This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version.

	This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.

	You should have received a copy of the GNU General Public License along with this program. If not, see <https://www.gnu.org/licenses/>.
*/

using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace EdgeLoop.Controls
{
    /// <summary>
    /// Base class for EdgeLoop's windows: a themed title bar (icon, title, optional subtitle and extra
    /// buttons, then minimize/maximize/close as allowed by <see cref="Window.ResizeMode"/>) drawn by
    /// <c>EdgeLoopWindowStyle</c> in Themes/Windows.xaml.
    /// It is a normal (not layered) window: Windows draws the shadow, snaps it, and on Windows 11
    /// rounds its corners and draws its outline.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public class EdgeLoopWindow : Window
    {
        public static readonly DependencyProperty SubtitleProperty =
            DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(EdgeLoopWindow), new PropertyMetadata(null));

        /// <summary>Shown after the title, dimmed. Optional.</summary>
        public string Subtitle
        {
            get => (string)GetValue(SubtitleProperty);
            set => SetValue(SubtitleProperty, value);
        }

        public static readonly DependencyProperty TitleBarContentProperty =
            DependencyProperty.Register(nameof(TitleBarContent), typeof(object), typeof(EdgeLoopWindow), new PropertyMetadata(null));

        /// <summary>Extra title bar content (typically buttons), placed left of the minimize button.</summary>
        public object TitleBarContent
        {
            get => GetValue(TitleBarContentProperty);
            set => SetValue(TitleBarContentProperty, value);
        }

        public static readonly DependencyProperty CloseCommandProperty =
            DependencyProperty.Register(nameof(CloseCommand), typeof(ICommand), typeof(EdgeLoopWindow), new PropertyMetadata(null));

        /// <summary>Runs instead of closing when the title bar's close button is clicked, e.g. to confirm exiting.</summary>
        public ICommand CloseCommand
        {
            get => (ICommand)GetValue(CloseCommandProperty);
            set => SetValue(CloseCommandProperty, value);
        }

        private static readonly DependencyPropertyKey ChromePaddingPropertyKey =
            DependencyProperty.RegisterReadOnly(nameof(ChromePadding), typeof(Thickness), typeof(EdgeLoopWindow), new PropertyMetadata(default(Thickness)));
        public static readonly DependencyProperty ChromePaddingProperty = ChromePaddingPropertyKey.DependencyProperty;

        /// <summary>
        /// A maximized window extends past the screen edges by its (invisible) resize frame; the template
        /// pads by this much so nothing is cut off.
        /// </summary>
        public Thickness ChromePadding => (Thickness)GetValue(ChromePaddingProperty);

        public EdgeLoopWindow()
        {
            SetResourceReference(StyleProperty, "EdgeLoopWindowStyle");

            CommandBindings.Add(new CommandBinding(SystemCommands.CloseWindowCommand, (s, e) => RequestClose()));
            CommandBindings.Add(new CommandBinding(SystemCommands.MinimizeWindowCommand,
                (s, e) => SystemCommands.MinimizeWindow(this),
                (s, e) => e.CanExecute = ResizeMode != ResizeMode.NoResize));
            CommandBindings.Add(new CommandBinding(SystemCommands.MaximizeWindowCommand,
                (s, e) => SystemCommands.MaximizeWindow(this),
                (s, e) => e.CanExecute = CanResize));
            CommandBindings.Add(new CommandBinding(SystemCommands.RestoreWindowCommand,
                (s, e) => SystemCommands.RestoreWindow(this),
                (s, e) => e.CanExecute = CanResize));
        }

        private bool CanResize => ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip;

        /// <summary>What the title bar's close button does: <see cref="CloseCommand"/> if set, otherwise <see cref="Window.Close"/>.</summary>
        public void RequestClose()
        {
            var command = CloseCommand;
            if (command == null)
                Close();
            else if (command.CanExecute(this))
                command.Execute(this);
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            ApplySystemFrame();
            UpdateChromePadding();
        }

        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            // Size-to-content windows are first measured with the system frame WindowChrome removes,
            // which can leave a dark strip along the bottom/right edge; measure again without it.
            if (SizeToContent != SizeToContent.Manual)
                InvalidateMeasure();
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            UpdateChromePadding();
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            UpdateChromePadding();
        }

        // --- Windows 11 frame ---

        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWCP_ROUND = 2;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>Rounded corners and a pink outline drawn by Windows 11. Elsewhere the template draws its own 1px border.</summary>
        private void ApplySystemFrame()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            try
            {
                int corners = DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corners, sizeof(int));

                var color = TryFindResource("WindowBorderColor") is Color c ? c : Color.FromRgb(0xA3, 0x0E, 0x5E);
                int colorRef = color.R | (color.G << 8) | (color.B << 16); // COLORREF is 0x00BBGGRR
                if (DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref colorRef, sizeof(int)) == 0)
                    BorderThickness = new Thickness(0); // Windows draws the outline
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        // --- Maximized padding ---

        private const int SM_CXFRAME = 32;
        private const int SM_CYFRAME = 33;
        private const int SM_CXPADDEDBORDER = 92;

        [DllImport("user32.dll")]
        private static extern int GetSystemMetricsForDpi(int index, uint dpi);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        private void UpdateChromePadding()
        {
            if (WindowState != WindowState.Maximized)
            {
                SetValue(ChromePaddingPropertyKey, default(Thickness));
                return;
            }

            var dpi = VisualTreeHelper.GetDpi(this);
            int frameX, frameY;
            try
            {
                uint dpiValue = (uint)Math.Round(dpi.PixelsPerInchX);
                int padded = GetSystemMetricsForDpi(SM_CXPADDEDBORDER, dpiValue);
                frameX = GetSystemMetricsForDpi(SM_CXFRAME, dpiValue) + padded;
                frameY = GetSystemMetricsForDpi(SM_CYFRAME, dpiValue) + padded;
            }
            catch (EntryPointNotFoundException)
            {
                // Before Windows 10 1607
                int padded = GetSystemMetrics(SM_CXPADDEDBORDER);
                frameX = GetSystemMetrics(SM_CXFRAME) + padded;
                frameY = GetSystemMetrics(SM_CYFRAME) + padded;
            }

            double x = frameX / dpi.DpiScaleX;
            double y = frameY / dpi.DpiScaleY;
            SetValue(ChromePaddingPropertyKey, new Thickness(x, y, x, y));
        }
    }
}
