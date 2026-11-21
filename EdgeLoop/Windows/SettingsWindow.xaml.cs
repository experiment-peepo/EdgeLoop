using System;
using System.Runtime.Versioning;
using System.Windows;
using EdgeLoop.Controls;
using EdgeLoop.ViewModels;

namespace EdgeLoop.Windows
{
    [SupportedOSPlatform("windows")]
    public partial class SettingsWindow : EdgeLoopWindow
    {
        private SettingsViewModel _viewModel;

        public SettingsWindow()
        {
            InitializeComponent();
            EdgeLoop.Classes.DialogKeys.Attach(this, SaveButton, CancelButton, enterFromTextBoxes: false);
            _viewModel = new SettingsViewModel();
            _viewModel.RequestClose += (s, e) =>
            {
                // Apply settings changes in owner window if it's LauncherWindow
                if (Owner is LauncherWindow launcherWindow)
                {
                    launcherWindow.ReloadHotkeys();
                    App.VideoService.RefreshAllOpacities();
                }
                this.Close();
            };
            DataContext = _viewModel;

            SourceInitialized += (s, e) => FitToScreen();
            SizeChanged += (s, e) => FitToScreen();
        }

        private const double ScreenMargin = 20;

        /// <summary>
        /// Caps the height to the screen's work area so the Save/Cancel footer stays visible and the
        /// content scrolls, and moves the window up when expanding a section would push it off screen.
        /// </summary>
        private void FitToScreen()
        {
            var reference = Owner ?? this;
            var handle = new System.Windows.Interop.WindowInteropHelper(reference).Handle;
            if (handle == IntPtr.Zero) return;

            var area = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(reference);
            double areaTop = area.Top / dpi.DpiScaleY;
            double areaHeight = area.Height / dpi.DpiScaleY;

            MaxHeight = Math.Max(200, areaHeight - 2 * ScreenMargin);

            if (IsLoaded && ActualHeight > 0 && Top + ActualHeight > areaTop + areaHeight - ScreenMargin)
                Top = Math.Max(areaTop + ScreenMargin, areaTop + areaHeight - ScreenMargin - ActualHeight);
        }
    }
}
