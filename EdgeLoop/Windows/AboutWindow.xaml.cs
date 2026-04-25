using System;
using System.Windows;
using System.Windows.Input;
using EdgeLoop.Classes;
using System.Reflection;

namespace EdgeLoop.Windows
{
    public partial class AboutWindow : Window
    {
        public AboutWindow()
        {
            InitializeComponent();
            DataContext = this;

            var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            VersionText.Text = $"v{version?.Split('+')[0]}";
        }

        private void Grid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try
                {
                    this.DragMove();
                }
                catch (InvalidOperationException) { }
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        public ICommand OpenKoFiCommand => new RelayCommand(OpenKoFi);
        public ICommand OpenPatreonCommand => new RelayCommand(OpenPatreon);

        private void OpenKoFi(object obj)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://ko-fi.com/vexfromdestiny",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to open Ko-Fi link", ex);
            }
        }

        private void OpenPatreon(object obj)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://www.patreon.com/cw/vexfromdestiny",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to open Patreon link", ex);
            }
        }
    }
}

