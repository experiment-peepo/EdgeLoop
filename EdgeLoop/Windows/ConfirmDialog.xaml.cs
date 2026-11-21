using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Media;
using EdgeLoop.Classes;
using EdgeLoop.Controls;

namespace EdgeLoop.Windows
{
    /// <summary>
    /// A yes/no question. Use <see cref="DialogService.Confirm"/> rather than creating it directly.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public partial class ConfirmDialog : EdgeLoopWindow
    {
        public ConfirmDialog(string title, string message, string confirmText, string cancelText, bool destructive)
        {
            InitializeComponent();

            Title = title;
            MessageText.Text = message;
            ConfirmButton.Content = confirmText;
            CancelButton.Content = cancelText;

            if (destructive)
            {
                // Red button, warning icon, and Enter doesn't confirm unless the button itself has focus
                ConfirmButton.Style = (Style)FindResource("DangerButtonStyle");
                MessageIcon.Data = (Geometry)FindResource("Icon_Warning");
                MessageIcon.Fill = (Brush)FindResource("DangerBrush");
            }

            DialogKeys.Attach(this, destructive ? null : ConfirmButton, CancelButton);
            Loaded += (s, e) => (destructive ? CancelButton : ConfirmButton).Focus();
        }

        private void ConfirmButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;

        private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
