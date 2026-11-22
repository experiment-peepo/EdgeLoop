using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EdgeLoop.Classes
{
    /// <summary>
    /// With HDR on: the window a screen's video is drawn into, through FlyleafLib's DirectComposition swap chain.
    /// WPF can only show 8-bit SDR, and once one monitor is in HDR it stops showing video on the others, so every
    /// screen gets one (in HDR where the monitor can). It covers the screen just under the playback overlay, which
    /// keeps the labels on top; it's click-through and never takes focus, and the video's see-through opacity is set
    /// on the swap chain.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class HdrVideoSurface : IDisposable
    {
        private const string ClassName = "EdgeLoopHdrVideo";
        private const uint WS_POPUP = 0x80000000;
        private const uint WS_EX_NOREDIRECTIONBITMAP = 0x00200000, WS_EX_LAYERED = 0x00080000, WS_EX_TRANSPARENT = 0x20,
            WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOPMOST = 0x8;
        private const uint WM_NCHITTEST = 0x84, WM_MOUSEACTIVATE = 0x21;
        private const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;

        private static WndProc _wndProc; // Kept alive for as long as the class is registered
        private static bool _registered;

        public nint Handle { get; private set; }

        /// <param name="bounds">The screen, in physical pixels.</param>
        /// <param name="overlay">The playback overlay it goes just under.</param>
        public HdrVideoSurface(System.Drawing.Rectangle bounds, nint overlay)
        {
            Register();
            Handle = CreateWindowExW(
                WS_EX_NOREDIRECTIONBITMAP | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
                ClassName, "EdgeLoop HDR video", WS_POPUP, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                nint.Zero, nint.Zero, GetModuleHandleW(null), nint.Zero);
            if (Handle == nint.Zero)
                throw new InvalidOperationException($"Couldn't create the HDR video window ({Marshal.GetLastWin32Error()})");

            // Layered and transparent: clicks go through to whatever is behind
            SetLayeredWindowAttributes(Handle, 0, 255, 2 /* LWA_ALPHA */);
            SetWindowPos(Handle, overlay, bounds.Left, bounds.Top, bounds.Width, bounds.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        private static void Register()
        {
            if (_registered)
                return;

            _wndProc = (hwnd, message, wParam, lParam) => message switch
            {
                WM_NCHITTEST => -1,     // HTTRANSPARENT
                WM_MOUSEACTIVATE => 3,  // MA_NOACTIVATE
                _ => DefWindowProcW(hwnd, message, wParam, lParam)
            };
            var windowClass = new WindowClass
            {
                Size = (uint)Marshal.SizeOf<WindowClass>(),
                WndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                Instance = GetModuleHandleW(null),
                ClassName = ClassName
            };
            if (RegisterClassExW(ref windowClass) == 0)
                throw new InvalidOperationException($"Couldn't register the HDR video window ({Marshal.GetLastWin32Error()})");
            _registered = true;
        }

        public void Dispose()
        {
            if (Handle != nint.Zero)
            {
                DestroyWindow(Handle);
                Handle = nint.Zero;
            }
        }

        private delegate nint WndProc(nint hwnd, uint message, nint wParam, nint lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WindowClass
        {
            public uint Size, Style;
            public nint WndProc;
            public int ClassExtra, WindowExtra;
            public nint Instance, Icon, Cursor, Background;
            public string MenuName, ClassName;
            public nint SmallIcon;
        }

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern ushort RegisterClassExW(ref WindowClass windowClass);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern nint CreateWindowExW(uint exStyle, string className, string name, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
        [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
        [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string name);
    }
}
