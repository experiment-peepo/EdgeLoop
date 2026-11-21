using System;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Threading;
using EdgeLoop.Classes;
using EdgeLoop.Controls;

namespace EdgeLoop.Windows
{
    /// <summary>
    /// A choice between two options that picks the second one after <see cref="AutoSelectSeconds"/>
    /// unless the user stops the countdown. Use <see cref="DialogService.Choose"/>.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public partial class ChoiceDialogWindow : EdgeLoopWindow
    {
        public const int AutoSelectSeconds = 20;

        private readonly DispatcherTimer _timer;
        private int _secondsRemaining = AutoSelectSeconds;
        private bool _countdownStopped;

        public string DialogTitle
        {
            get => (string)GetValue(DialogTitleProperty);
            set => SetValue(DialogTitleProperty, value);
        }
        public static readonly DependencyProperty DialogTitleProperty =
            DependencyProperty.Register(nameof(DialogTitle), typeof(string), typeof(ChoiceDialogWindow), new PropertyMetadata("Choose"));

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
            DependencyProperty.Register(nameof(Option2Text), typeof(string), typeof(ChoiceDialogWindow),
                new PropertyMetadata("Option 2", (d, e) => ((ChoiceDialogWindow)d).UpdateCountdownText()));

        public string CountdownText
        {
            get => (string)GetValue(CountdownTextProperty);
            set => SetValue(CountdownTextProperty, value);
        }
        public static readonly DependencyProperty CountdownTextProperty =
            DependencyProperty.Register(nameof(CountdownText), typeof(string), typeof(ChoiceDialogWindow), new PropertyMetadata(""));

        /// <summary>0 = cancelled, 1 = first option, 2 = second option.</summary>
        public int SelectedOption { get; private set; }

        public ChoiceDialogWindow()
        {
            InitializeComponent();
            // Esc and the title bar's close button cancel
            DialogKeys.Attach(this, Option2Button, cancelButton: null);
            DataContext = this;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += Timer_Tick;
            Loaded += (s, e) =>
            {
                if (IsVisible)
                    _timer.Start();
                Option2Button.Focus();
            };
            Closed += (s, e) => _timer.Stop();
            UpdateCountdownText();
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            _secondsRemaining--;
            if (_secondsRemaining <= 0)
                Choose(2);
            else
                UpdateCountdownText();
        }

        private void UpdateCountdownText()
        {
            if (!_countdownStopped)
                CountdownText = $"Choosing “{Option2Text}” in {_secondsRemaining} s";
        }

        private void PauseButton_Click(object sender, RoutedEventArgs e)
        {
            _timer.Stop();
            _countdownStopped = true;
            CountdownText = "Countdown stopped";
            PauseButton.Visibility = Visibility.Collapsed;
            Option2Button.Focus();
        }

        private void Option1Button_Click(object sender, RoutedEventArgs e) => Choose(1);

        private void Option2Button_Click(object sender, RoutedEventArgs e) => Choose(2);

        private void Choose(int option)
        {
            _timer.Stop();
            SelectedOption = option;
            DialogResult = true;
        }
    }
}
