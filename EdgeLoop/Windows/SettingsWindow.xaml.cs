using System;
using System.Windows;
using System.Windows.Input;
using EdgeLoop.ViewModels;
using System.IO;
using System.Diagnostics;

namespace EdgeLoop.Windows
{
    public partial class SettingsWindow : Window
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

        private void Grid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Mark event as handled to prevent event bubbling issues
            e.Handled = true;

            // Call DragMove immediately while the button is definitely pressed
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try
                {
                    this.DragMove();
                }
                catch (InvalidOperationException)
                {
                    // Silently handle the case where DragMove fails
                    // This can happen in rare timing scenarios
                }
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

    }

    public class EnumToVisibilityConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value == null || parameter == null) return Visibility.Collapsed;
            string checkValue = value.ToString();
            string targetValue = parameter.ToString();
            return checkValue.Equals(targetValue, StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}

