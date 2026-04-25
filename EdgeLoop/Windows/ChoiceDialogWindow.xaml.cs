using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace EdgeLoop.Windows
{
    public partial class ChoiceDialogWindow : Window
    {
        private DispatcherTimer _timer;
        private int _secondsRemaining = 20;

        public string DialogTitle
        {
            get => (string)GetValue(DialogTitleProperty);
            set => SetValue(DialogTitleProperty, value);
        }
        public static readonly DependencyProperty DialogTitleProperty =
            DependencyProperty.Register(nameof(DialogTitle), typeof(string), typeof(ChoiceDialogWindow), new PropertyMetadata("Choice Required"));

        public string Message
        {
            get => (string)GetValue(MessageProperty);
            set => SetValue(MessageProperty, value);
        }
        public static readonly DependencyProperty MessageProperty =
            DependencyProperty.Register(nameof(Message), typeof(string), typeof(ChoiceDialogWindow), new PropertyMetadata(""));

        public string Option1Text
        {
            get => (string)GetValue(Option1TextProperty);
            set => SetValue(Option1TextProperty, value);
        }
        public static readonly DependencyProperty Option1TextProperty =
            DependencyProperty.Register(nameof(Option1Text), typeof(string), typeof(ChoiceDialogWindow), new PropertyMetadata("Option 1"));

        public string Option2Text
        {
            get => (string)GetValue(Option2TextProperty);
            set => SetValue(Option2TextProperty, value);
        }
        public static readonly DependencyProperty Option2TextProperty =
            DependencyProperty.Register(nameof(Option2Text), typeof(string), typeof(ChoiceDialogWindow), new PropertyMetadata("Option 2"));

        public string CountdownText
        {
            get => (string)GetValue(CountdownTextProperty);
            set => SetValue(CountdownTextProperty, value);
        }
        public static readonly DependencyProperty CountdownTextProperty =
            DependencyProperty.Register(nameof(CountdownText), typeof(string), typeof(ChoiceDialogWindow), new PropertyMetadata(""));

        public int SelectedOption { get; private set; } = 0; // 0=Cancel, 1=Option1, 2=Option2

        public ChoiceDialogWindow()
        {
            InitializeComponent();
            DataContext = this;
            Loaded += ChoiceDialogWindow_Loaded;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += Timer_Tick;
            UpdateCountdownText();
        }

        private void ChoiceDialogWindow_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateMainBorderClip();
            _timer.Start();
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            _secondsRemaining--;
            if (_secondsRemaining <= 0)
            {
                _timer.Stop();
                // Default to Option 2 (usually the "Full Collection" or safer default)
                SelectedOption = 2;
                DialogResult = true;
                Close();
            }
            else
            {
                UpdateCountdownText();
            }
        }

        private void UpdateCountdownText()
        {
            CountdownText = $"{_secondsRemaining}s remaining (Defaulting to {Option2Text})";
        }

        private void MainBorder_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateMainBorderClip();
        }

        private void UpdateMainBorderClip()
        {
            if (MainBorderClip != null && MainBorder != null)
            {
                MainBorderClip.Rect = new Rect(0, 0, MainBorder.ActualWidth, MainBorder.ActualHeight);
            }
        }

        private void Grid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try { this.DragMove(); } catch { }
            }
        }

        private void Option1Button_Click(object sender, RoutedEventArgs e)
        {
            _timer.Stop();
            SelectedOption = 1;
            DialogResult = true;
            Close();
        }

        private void Option2Button_Click(object sender, RoutedEventArgs e)
        {
            _timer.Stop();
            SelectedOption = 2;
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            _timer.Stop();
            SelectedOption = 0;
            DialogResult = false;
            Close();
        }

        public static int ShowDialog(Window owner, string title, string message, string option1, string option2)
        {
            var dialog = new ChoiceDialogWindow
            {
                DialogTitle = title,
                Message = message,
                Option1Text = option1,
                Option2Text = option2
            };

            if (owner != null)
            {
                dialog.Owner = owner;
                dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else if (Application.Current?.MainWindow != null)
            {
                dialog.Owner = Application.Current.MainWindow;
                dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }

            bool? result = dialog.ShowDialog();
            return result == true ? dialog.SelectedOption : 0;
        }
    }
}
