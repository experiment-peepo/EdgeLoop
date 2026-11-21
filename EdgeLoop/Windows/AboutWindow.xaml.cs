using System;
using System.Reflection;
using System.Runtime.Versioning;
using System.Windows.Input;
using EdgeLoop.Classes;
using EdgeLoop.Controls;

namespace EdgeLoop.Windows
{
    [SupportedOSPlatform("windows")]
    public partial class AboutWindow : EdgeLoopWindow
    {
        public AboutWindow()
        {
            InitializeComponent();
            DialogKeys.Attach(this, defaultButton: null, cancelButton: null); // Esc closes
            DataContext = this;

            var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            VersionText.Text = $"v{version?.Split('+')[0]}";
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

