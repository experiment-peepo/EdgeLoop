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

