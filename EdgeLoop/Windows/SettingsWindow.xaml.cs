using System;
using System.ComponentModel;
using System.Runtime.Versioning;
using System.Windows;
using EdgeLoop.Classes;
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
            DialogKeys.Attach(this, SaveButton, CancelButton, enterFromTextBoxes: false);
            _viewModel = new SettingsViewModel();
            _viewModel.RequestClose += (s, e) => Close();
            _viewModel.PropertyChanged += (s, e) =>
            {
                // Each section starts at its top
                if (e.PropertyName == nameof(SettingsViewModel.SelectedSection))
                    ContentScroller.ScrollToTop();
            };
            DataContext = _viewModel;

            // A password box can't be bound: fill it once, then pass every change to the view model
            IwaraPasswordBox.Password = _viewModel.IwaraPassword ?? string.Empty;
            IwaraPasswordBox.PasswordChanged += (s, e) => _viewModel.IwaraPassword = IwaraPasswordBox.Password;
            HypnotubePasswordBox.Password = _viewModel.HypnotubeAccount.Password ?? string.Empty;
            HypnotubePasswordBox.PasswordChanged += (s, e) => _viewModel.HypnotubeAccount.Password = HypnotubePasswordBox.Password;
            Rule34VideoPasswordBox.Password = _viewModel.Rule34VideoAccount.Password ?? string.Empty;
            Rule34VideoPasswordBox.PasswordChanged += (s, e) => _viewModel.Rule34VideoAccount.Password = Rule34VideoPasswordBox.Password;

            SourceInitialized += (s, e) => FitToScreen();
            SizeChanged += (s, e) => FitToScreen();
        }

        /// <summary>Asks before throwing away unsaved changes (Cancel, Esc, or the title bar's close button).</summary>
        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);
            if (e.Cancel || !_viewModel.HasChanges || _viewModel.IsSaved)
                return;

            if (!DialogService.Confirm("Unsaved changes", "Close Settings without saving your changes?",
                    "Discard changes", "Keep editing", destructive: true, owner: this))
                e.Cancel = true;
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            // A recorder that was listening when the window closed must give the hotkeys back
            HotkeyCapture.SetRecording(false);

            if (_viewModel.IsSaved && Owner is LauncherWindow launcherWindow)
            {
                launcherWindow.ReloadHotkeys();
                App.VideoService?.RefreshAllOpacities();
            }
        }

        private const double ScreenMargin = 20;

        /// <summary>
        /// Caps the height to the screen's work area so the Save/Cancel footer stays visible and the
        /// content scrolls, and moves the window up if it would hang off the bottom of the screen.
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
