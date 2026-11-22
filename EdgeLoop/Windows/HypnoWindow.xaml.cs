using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Interop;
// using System.Windows.Forms; // Removed to avoid ambiguity
using System.Windows.Media;
using System.Windows.Controls;
using EdgeLoop.Classes;
using EdgeLoop.ViewModels;
using System.Diagnostics;
using Logger = EdgeLoop.Classes.Logger;

namespace EdgeLoop.Windows
{
    [SupportedOSPlatform("windows")]
    public partial class HypnoWindow : Window, IDisposable
    {
        private HypnoViewModel _viewModel;
        private System.Windows.Forms.Screen _targetScreen;
        private bool _disposed = false;
        private DateTime _lastPositionSaveTime = DateTime.MinValue;
        private System.Windows.Threading.DispatcherTimer _syncTimer;


        public HypnoViewModel ViewModel => _viewModel;
        public string ScreenDeviceName => _targetScreen?.DeviceName ?? "Unknown";

        // With HDR on (see HdrSwitcher.UsesOwnVideoWindows): this screen's video goes to a DirectComposition window
        // under this one, in HDR where the monitor can
        private readonly bool _ownVideoWindow;
        private HdrVideoSurface _videoSurface;
        private string _hdrScreen;

        /// <param name="screen">The screen it plays on.</param>
        /// <param name="ownVideoWindow">Draw the video in a window of its own, for HDR, instead of through WPF.</param>
        public HypnoWindow(System.Windows.Forms.Screen screen = null, bool ownVideoWindow = false)
        {
            InitializeComponent();
            _targetScreen = screen;
            _ownVideoWindow = ownVideoWindow;
            _viewModel = new HypnoViewModel(App.UrlExtractor);
            _viewModel.MonitorName = screen?.DeviceName ?? "Default";
            this.DataContext = _viewModel;

            if (screen != null)
            {
                Logger.Debug($"[HypnoWindow] Created for screen {screen.DeviceName} | Bounds: {screen.Bounds}");

                // Position will be set in SourceInitialized using SetWindowPos
            }
            else
            {
                Logger.Debug("[HypnoWindow] Created with default screen");
            }
            // ... (keep event subscriptions)
            _viewModel.MediaErrorOccurred += ViewModel_MediaErrorOccurred;
            _viewModel.MediaOpened += ViewModel_MediaOpened;
            _viewModel.TerminalFailure += ViewModel_TerminalFailure;
            _viewModel.MediaFailed += ViewModel_MediaFailed;
            _viewModel.UpscalingLowered += ViewModel_UpscalingLowered;




            _viewModel.PropertyChanged += ViewModel_PropertyChanged;



            // Initialize position reporting timer
            _syncTimer = new System.Windows.Threading.DispatcherTimer();
            _syncTimer.Interval = TimeSpan.FromMilliseconds(50);
            _syncTimer.Tick += SyncTimer_Tick;
            _syncTimer.Start();
        }

        private void ViewModel_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            // SpeedRatio and Volume are now handled directly by the ViewModel's Player instance

            // The video window's video is see-through on its own; this window's opacity only reaches the labels
            if (_videoSurface != null && e.PropertyName == nameof(HypnoViewModel.EffectiveOpacity))
                _viewModel.Player?.Renderer?.SwapChain.SetOpacity((float)_viewModel.EffectiveOpacity);
        }

        /// <summary>
        /// With HDR on: shows this screen's video in its own window through FlyleafLib's swap chain instead of through
        /// WPF, and on a monitor that supports HDR switches Windows HDR on for it. Every screen of the playback does
        /// this, HDR or not: once one monitor is in HDR, WPF stops showing its video on the others (the frames are
        /// drawn, but never appear). Anything that fails leaves the usual WPF video in place.
        /// </summary>
        [SupportedOSPlatform("windows")]
        private void StartOwnVideoWindow(IntPtr overlay, System.Drawing.Rectangle bounds)
        {
            var player = _viewModel.Player;
            if (_targetScreen == null || player?.Renderer == null)
                return;

            try
            {
                bool hdr = HdrSwitcher.Shared.SwitchOn(_targetScreen.DeviceName);
                if (hdr)
                {
                    _hdrScreen = _targetScreen.DeviceName;
                    player.Renderer.PreferredVideoProcessor = FlyleafLib.VideoProcessors.Flyleaf; // HDR output needs it
                }

                // Off with WPF's video: the swap chain draws it instead
                FirstVideo.Player = null;
                FirstVideo.Visibility = Visibility.Collapsed;
                Background = System.Windows.Media.Brushes.Transparent;

                _videoSurface = new HdrVideoSurface(bounds, overlay);
                player.Renderer.SwapChain.SetOpacity((float)_viewModel.EffectiveOpacity);
                player.Renderer.SwapChain.Setup(_videoSurface.Handle);
                Logger.Info(hdr
                    ? $"[HDR] {_targetScreen.DeviceName}: video in its own window, in HDR"
                    : $"[HDR] {_targetScreen.DeviceName} can't show HDR: video in its own window, in SDR");
            }
            catch (Exception ex)
            {
                Logger.Error($"[HDR] Couldn't give {_targetScreen.DeviceName} its own video window: the usual video", ex);
                StopOwnVideoWindow();
                if (TryFindResource("VideoBackdropBrush") is System.Windows.Media.Brush backdrop)
                    Background = backdrop;
                FirstVideo.Visibility = Visibility.Visible;
                FirstVideo.Player = player;
            }
        }

        /// <summary>Takes the video off its own window and puts the monitor's HDR back as it was.</summary>
        [SupportedOSPlatform("windows")]
        private void StopOwnVideoWindow()
        {
            try
            {
                if (_videoSurface != null)
                {
                    _viewModel?.Player?.Renderer?.SwapChain.Setup(0);
                    _videoSurface.Dispose();
                    _videoSurface = null;
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"[HDR] Closing the video window: {ex.Message}");
            }

            if (_hdrScreen != null)
            {
                HdrSwitcher.Shared.Restore(_hdrScreen);
                _hdrScreen = null;
            }
        }

        private void SyncTimer_Tick(object sender, EventArgs e)
        {
            // Logic moved to HypnoViewModel.cs to centralize position tracking
        }

        // Removed ViewModel_Request* handlers as they are now handled by Player instance in VM


        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    StopOwnVideoWindow(); // Before the player goes

                    // Unsubscribe from events
                    if (_viewModel != null)
                    {
                        _viewModel.MediaErrorOccurred -= ViewModel_MediaErrorOccurred;
                        _viewModel.MediaOpened -= ViewModel_MediaOpened;
                        _viewModel.TerminalFailure -= ViewModel_TerminalFailure;
                        _viewModel.MediaFailed -= ViewModel_MediaFailed;
                        _viewModel.UpscalingLowered -= ViewModel_UpscalingLowered;
                        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
                        _viewModel.Dispose();
                    }

                    // Unregister from VideoService before closing/disposing
                    App.VideoService?.UnregisterPlayer(this);

                    if (_syncTimer != null)
                    {
                        _syncTimer.Stop();
                        _syncTimer = null;
                    }

                    // Dispose MediaElement
                    if (FirstVideo != null)
                    {
                        // Flyleaf Player disposal is handled by ViewModel
                        FirstVideo = null;
                        Logger.Debug("[HypnoWindow] Flyleaf control reference cleared");
                    }
                }
                _disposed = true;
                Logger.Debug("[HypnoWindow] Disposed");
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        protected override void OnClosed(EventArgs e)
        {

            Dispose();
            base.OnClosed(e);
        }

        // Removed FirstVideo_MediaEnded/Failed as Flyleaf events are mapped in ViewModel

        private void ViewModel_MediaErrorOccurred(object sender, MediaErrorEventArgs e)
        {
            if (_disposed) return;

            try
            {
                // Forward the error to the VideoPlayerService so it can notify subscribers
                App.VideoService?.OnMediaError(e);
            }
            catch (Exception ex)
            {
                Logger.Error("Error in ViewModel_MediaErrorOccurred", ex);
            }
        }

        /// <summary>A small notice in this screen's corner: which video failed and why.</summary>
        private void ViewModel_MediaFailed(object sender, MediaErrorEventArgs e)
        {
            if (_disposed || App.Settings?.ShowPlaybackNotices == false) return;

            string screen = ScreenDeviceName;
            string headline = PlaybackFailureText.Headline(e.Item?.FileName);
            string detail = PlaybackFailureText.Detail(e.ErrorMessage);
            Dispatcher.InvokeAsync(() => OverlayNotice.Show(screen, headline, detail));
        }

        /// <summary>A notice in this screen's corner when it stepped upscaling down for a slow graphics card.</summary>
        private void ViewModel_UpscalingLowered(object sender, string level)
        {
            if (_disposed || App.Settings?.ShowPlaybackNotices == false) return;

            string screen = ScreenDeviceName;
            var (headline, detail) = UpscalingSupport.LoweredNotice(level);
            Dispatcher.InvokeAsync(() => OverlayNotice.Show(screen, headline, detail, isError: false));
        }

        private void ViewModel_TerminalFailure(object sender, EventArgs e)
        {
            if (_disposed) return;

            // Say why the screen went away. The notice is its own window, so it outlives this one.
            if (App.Settings?.ShowPlaybackNotices != false)
            {
                string screen = ScreenDeviceName;
                string reason = _viewModel.StopReason ?? "None of this screen's videos could be played.";
                Dispatcher.InvokeAsync(() => OverlayNotice.Show(screen, "Playback stopped on this screen", reason));
            }

            // Terminal failure means all videos failed. Close the window.
            // Dispatch to UI thread just in case it's called from a background task
            Dispatcher.InvokeAsync(() =>
            {
                if (!_disposed)
                {
                    Logger.Debug("[HypnoWindow] Terminal failure occurred. Closing window.");
                    this.Close();
                }
            });
        }

        private void ViewModel_MediaOpened(object sender, EventArgs e)
        {
            if (_disposed) return;

            try
            {
                // Forward the event to the VideoPlayerService
                App.VideoService?.OnMediaOpened();
            }
            catch (Exception ex)
            {
                Logger.Error("Error in ViewModel_MediaOpened", ex);
            }
        }

        // Removed FirstVideo_MediaOpened as it's now handled by ViewModel's Player.OpenCompleted

        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOMOVE = 0x0002;
        const uint SWP_NOZORDER = 0x0004;
        const uint SWP_SHOWWINDOW = 0x0040;
        const uint SWP_NOACTIVATE = 0x0010;
        const uint SWP_FRAMECHANGED = 0x0020;
        const uint SWP_ASYNCWINDOWPOS = 0x4000;

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        const int SW_MAXIMIZE = 3;
        const int SW_SHOW = 5;

        [SupportedOSPlatform("windows")]
        private void Window_SourceInitialized(object sender, EventArgs e)
        {
            if (_targetScreen != null)
            {
                // Validate that the target screen still exists
                var allScreens = System.Windows.Forms.Screen.AllScreens;
                bool screenExists = allScreens.Any(s => s.DeviceName == _targetScreen.DeviceName);

                if (!screenExists)
                {
                    // Screen was disconnected, fallback to primary screen
                    Logger.Warning($"Target screen {_targetScreen.DeviceName} is no longer available, falling back to primary screen");
                    _targetScreen = System.Windows.Forms.Screen.PrimaryScreen ?? System.Windows.Forms.Screen.AllScreens.FirstOrDefault();

                    if (_targetScreen == null)
                    {
                        Logger.Error("No screens available for window positioning");
                        return;
                    }
                }

                IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;

                // --- HARDWARE DIAGNOSTICS ---
                int renderingTier = (RenderCapability.Tier >> 16);
                Logger.Debug($"[Hardware] Rendering Tier: {renderingTier} (Tier 2 = Full Hardware Acceleration)");
                if (renderingTier < 2)
                {
                    Logger.Warning("[Hardware] WPF is in software rendering mode. This will cause 4K stutter.");
                }

                // Force Hardware Rendering if possible (ensure not SoftWare only)
                var source = HwndSource.FromHwnd(hwnd);
                if (source != null && source.CompositionTarget != null)
                {
                    source.CompositionTarget.RenderMode = RenderMode.Default;
                }
                // ---------------------------

                // 1. Click-through overlay that stays out of the taskbar and Alt-Tab and never takes focus
                int extendedStyle = WindowServices.GetWindowLong(hwnd, WindowServices.GWL_EXSTYLE);
                WindowServices.SetWindowLong(hwnd, WindowServices.GWL_EXSTYLE,
                    extendedStyle | WindowServices.WS_EX_TRANSPARENT | WindowServices.WS_EX_TOOLWINDOW | WindowServices.WS_EX_NOACTIVATE);

                // 2. Physical Placement
                var b = _targetScreen.Bounds;
                Logger.Debug($"[HypnoWindow] Positioning: {b.Left},{b.Top} {b.Width}x{b.Height}");
                SetWindowPos(hwnd, new IntPtr(-1), b.Left, b.Top, b.Width, b.Height,
                    SWP_FRAMECHANGED | SWP_SHOWWINDOW | SWP_NOACTIVATE);

                if (_ownVideoWindow)
                    StartOwnVideoWindow(hwnd, b);

                // 3. WPF Metadata
                this.WindowStartupLocation = WindowStartupLocation.Manual;
                this.WindowState = WindowState.Normal;

                // 4. WPF logical sync for scaling
                // We use the DPI of the specific window handle now that it's moved
                this.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_disposed) return;
                    var dpi = VisualTreeHelper.GetDpi(this);

                    // Update logical properties to match physical placement
                    // This prevents WPF from trying to "correct" the window position later
                    this.Left = b.Left / dpi.DpiScaleX;
                    this.Top = b.Top / dpi.DpiScaleY;
                    this.Width = b.Width / dpi.DpiScaleX;
                    this.Height = b.Height / dpi.DpiScaleY;

                    // Re-assert Topmost state in WPF after move
                    this.Topmost = false;
                    this.Topmost = true;

                    Logger.Debug($"[HypnoWindow] Logical sync: {this.Left},{this.Top} {this.Width}x{this.Height} (DPI: {dpi.DpiScaleX})");
                }), System.Windows.Threading.DispatcherPriority.Loaded);


            }
        }



    }
}

