using System.Runtime.Versioning;
using System.Windows;
using EdgeLoop.Classes;
using EdgeLoop.Controls;

namespace EdgeLoop.Windows
{
    /// <summary>
    /// Asks for one line of text, e.g. a URL. Use <see cref="DialogService.Prompt"/>.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public partial class InputDialogWindow : EdgeLoopWindow
    {
        public string DialogTitle
        {
            get => (string)GetValue(DialogTitleProperty);
            set => SetValue(DialogTitleProperty, value);
        }

        public static readonly DependencyProperty DialogTitleProperty =
            DependencyProperty.Register(nameof(DialogTitle), typeof(string), typeof(InputDialogWindow), new PropertyMetadata("Input"));

        public string LabelText
        {
            get => (string)GetValue(LabelTextProperty);
            set => SetValue(LabelTextProperty, value);
        }

        public static readonly DependencyProperty LabelTextProperty =
            DependencyProperty.Register(nameof(LabelText), typeof(string), typeof(InputDialogWindow), new PropertyMetadata("Enter value:"));

        /// <summary>Greyed-out example shown inside the empty text box.</summary>
        public string Hint
        {
            get => (string)GetValue(HintProperty);
            set => SetValue(HintProperty, value);
        }

        public static readonly DependencyProperty HintProperty =
            DependencyProperty.Register(nameof(Hint), typeof(string), typeof(InputDialogWindow), new PropertyMetadata(null));

        /// <summary>Small print under the text box, e.g. the supported sites. Hidden when empty.</summary>
        public string Note
        {
            get => (string)GetValue(NoteProperty);
            set => SetValue(NoteProperty, value);
        }

        public static readonly DependencyProperty NoteProperty =
            DependencyProperty.Register(nameof(Note), typeof(string), typeof(InputDialogWindow), new PropertyMetadata(null));

        public string InputText
        {
            get => (string)GetValue(InputTextProperty);
            set => SetValue(InputTextProperty, value);
        }

        public static readonly DependencyProperty InputTextProperty =
            DependencyProperty.Register(nameof(InputText), typeof(string), typeof(InputDialogWindow),
                new PropertyMetadata("", (d, e) => ((InputDialogWindow)d).UpdateOkButton()));

        public InputDialogWindow()
        {
            InitializeComponent();
            DialogKeys.Attach(this, OkButton, CancelButton);
            DataContext = this;
            Loaded += (s, e) =>
            {
                InputTextBox.Focus();
                InputTextBox.SelectAll();
            };
            UpdateOkButton();
        }

        private void UpdateOkButton()
        {
            // OK means nothing until something has been typed
            if (OkButton != null)
                OkButton.IsEnabled = !string.IsNullOrWhiteSpace(InputText);
        }

        private void OkButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;

        private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
