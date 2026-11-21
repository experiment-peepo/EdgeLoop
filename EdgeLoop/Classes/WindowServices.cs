/*
	Copyright (C) 2026 Llamasoft

	This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version.

	This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.

	You should have received a copy of the GNU General Public License along with this program. If not, see <https://www.gnu.org/licenses/>. 
*/

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Runtime.Versioning;
using System.Windows.Interop;

namespace EdgeLoop.Classes
{
    [SupportedOSPlatform("windows")]
    public static class WindowServices
    {
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int WS_EX_TOOLWINDOW = 0x00000080;   // Not in the taskbar or Alt-Tab
        public const int WS_EX_NOACTIVATE = 0x08000000;   // Never takes focus
        public const int GWL_EXSTYLE = (-20);

        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        public static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hWnd);

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;

        public enum ScreenAnchor
        {
            Center,
            BottomLeft
        }

        /// <summary>
        /// Puts a shown window on a screen, in real pixels: first onto the screen (so it takes that
        /// screen's scaling and size), then at the anchor. <paramref name="marginDip"/> is in that
        /// screen's scaled units. Works whatever scaling each monitor uses.
        /// </summary>
        public static void PlaceOnScreen(Window window, Screen screen, ScreenAnchor anchor, double marginDip = 0)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero || screen == null) return;

            var area = anchor == ScreenAnchor.Center ? screen.Bounds : screen.WorkingArea;
            const uint flags = SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE;

            // Moving onto a screen with different scaling makes WPF resize the window for it
            SetWindowPos(hwnd, IntPtr.Zero, area.Left, area.Top, 0, 0, flags);

            if (!GetWindowRect(hwnd, out var rect)) return;
            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            uint dpi = 96;
            try { dpi = GetDpiForWindow(hwnd); } catch (EntryPointNotFoundException) { }
            int margin = (int)Math.Round(marginDip * (dpi == 0 ? 96 : dpi) / 96.0);

            int x = anchor == ScreenAnchor.Center ? area.Left + (area.Width - width) / 2 : area.Left + margin;
            int y = anchor == ScreenAnchor.Center ? area.Top + (area.Height - height) / 2 : area.Bottom - height - margin;
            SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, flags);
        }

        [SupportedOSPlatform("windows")]
        public static Screen[] GetAllScreens()
        {
            return Screen.AllScreens;
        }

        [SupportedOSPlatform("windows")]
        public static int GetNumberOfScreens()
        {
            return Screen.AllScreens.Length;
        }

        [SupportedOSPlatform("windows")]
        public static List<ScreenViewer> GetAllScreenViewers()
        {
            List<ScreenViewer> list = new List<ScreenViewer>();
            foreach (Screen screen in GetAllScreens())
            {
                list.Add(new ScreenViewer(screen));
            }
            return list;
        }

        [SupportedOSPlatform("windows")]
        public static void MoveWindowToScreen(Window window, Screen screen)
        {
            if (window == null || screen == null) return;

            window.WindowState = WindowState.Normal;
            window.WindowStartupLocation = WindowStartupLocation.Manual;

            var workingArea = screen.WorkingArea;
            window.Left = workingArea.Left;
            window.Top = workingArea.Top;
            window.Width = workingArea.Width;
            window.Height = workingArea.Height;

            window.WindowState = WindowState.Maximized;
        }
    }
}

