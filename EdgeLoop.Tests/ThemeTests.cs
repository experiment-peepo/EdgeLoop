using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using EdgeLoop.Classes;
using EdgeLoop.Windows;
using FluentAssertions;

namespace EdgeLoop.Tests
{
    /// <summary>
    /// The app's resources and windows on one STA thread. WPF allows a single Application per process,
    /// and its resources belong to the thread that created it, so every WPF test runs here.
    /// </summary>
    internal static class WpfApp
    {
        private static readonly Lazy<Dispatcher> _dispatcher = new(Start);

        private static Dispatcher Start()
        {
            Dispatcher? dispatcher = null;
            Exception? startupError = null;
            using var ready = new ManualResetEventSlim();

            var thread = new Thread(() =>
            {
                try
                {
                    var app = new App();
                    app.InitializeComponent(); // loads App.xaml and the theme, without starting the app
                    // App.xaml says OnMainWindowClose; closing the first test window must not shut the dispatcher down
                    app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    dispatcher = Dispatcher.CurrentDispatcher;
                }
                catch (Exception ex)
                {
                    startupError = ex;
                }
                ready.Set();
                if (startupError == null)
                    Dispatcher.Run();
            })
            { IsBackground = true, Name = "WPF test thread" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.Wait();

            if (startupError != null)
                ExceptionDispatchInfo.Capture(startupError).Throw();
            return dispatcher!;
        }

        public static void Run(Action action)
        {
            Exception? error = null;
            _dispatcher.Value.Invoke(() =>
            {
                try { action(); }
                catch (Exception ex) { error = ex; }
            });
            if (error != null)
                ExceptionDispatchInfo.Capture(error).Throw();
        }
    }

    public class ThemeTests : TestBase
    {
        private static void Layout(FrameworkElement element, double width = 600, double height = 400)
        {
            element.Measure(new Size(width, height));
            element.Arrange(new Rect(0, 0, width, height));
            element.UpdateLayout();
        }

        [Fact]
        public void EveryThemeResource_Loads()
        {
            WpfApp.Run(() =>
            {
                var dictionaries = Application.Current.Resources.MergedDictionaries;
                dictionaries.Should().NotBeEmpty();

                var failures = new List<string>();
                foreach (var dictionary in dictionaries)
                {
                    foreach (var key in dictionary.Keys.Cast<object>().ToList())
                    {
                        try { _ = dictionary[key]; }
                        catch (Exception ex) { failures.Add($"{dictionary.Source} {key}: {ex.Message}"); }
                    }
                }
                failures.Should().BeEmpty();
            });
        }

        [Fact]
        public void EveryControlStyle_AppliesItsTemplate()
        {
            WpfApp.Run(() =>
            {
                var failures = new List<string>();
                var styles = Application.Current.Resources.MergedDictionaries
                    .SelectMany(d => d.Keys.Cast<object>().Select(k => (Key: k, Value: d[k])))
                    .Where(r => r.Value is Style { TargetType: not null } s
                                && typeof(Control).IsAssignableFrom(s.TargetType)
                                && !typeof(Window).IsAssignableFrom(s.TargetType)
                                && !s.TargetType.IsAbstract)
                    .ToList();
                styles.Should().HaveCountGreaterThan(10);

                foreach (var (key, value) in styles)
                {
                    var style = (Style)value;
                    try
                    {
                        var control = (Control)Activator.CreateInstance(style.TargetType)!;
                        control.Style = style;
                        if (control is ContentControl content and not ScrollViewer)
                            content.Content = "Sample";
                        if (control is ItemsControl items)
                            items.ItemsSource = new[] { "One", "Two" };
                        // Tool tips live in their own popup and can't have a parent
                        FrameworkElement host = control is ToolTip ? control : new Border { Child = control };
                        Layout(host);
                        if (control.Template != null)
                            VisualTreeHelper.GetChildrenCount(control).Should().BeGreaterThan(0, $"{key} should build its template");
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{key}: {ex.GetBaseException().Message}");
                    }
                }
                failures.Should().BeEmpty();
            });
        }

        public static TheoryData<string> Dialogs => new() { "About", "Confirm", "ConfirmDestructive", "Choice", "Input", "Settings" };

        private static Window CreateDialog(string name) => name switch
        {
            "About" => new AboutWindow(),
            "Confirm" => new ConfirmDialog("Exit EdgeLoop", "Exit EdgeLoop?", "Exit", "Cancel", destructive: false),
            "ConfirmDestructive" => new ConfirmDialog("Reset", "Clear everything?", "Clear", "Cancel", destructive: true),
            "Choice" => new ChoiceDialogWindow { DialogTitle = "Crawl video 1 or playlist 2", Message = "Both?", Option1Text = "Only video", Option2Text = "Playlist" },
            "Input" => new InputDialogWindow { DialogTitle = "Add video URL", LabelText = "Video URL", Hint = "https://", Note = "Supported: ..." },
            "Settings" => new SettingsWindow(),
            _ => throw new ArgumentOutOfRangeException(nameof(name))
        };

        [Theory]
        [MemberData(nameof(Dialogs))]
        public void Dialogs_BuildWithTheSharedTitleBar(string name)
        {
            WpfApp.Run(() =>
            {
                var window = CreateDialog(name);
                try
                {
                    // A real (hidden) window handle, so SourceInitialized and the chrome code run; never shown
                    new WindowInteropHelper(window).EnsureHandle();
                    window.ApplyTemplate();
                    Layout(window, 520, 700);

                    window.Should().BeAssignableTo<Controls.EdgeLoopWindow>();
                    window.AllowsTransparency.Should().BeFalse("layered windows lose the system shadow and snapping");
                    window.Template.FindName("CloseButton", window).Should().BeOfType<Button>();
                    var maximize = window.Template.FindName("MaximizeButton", window).Should().BeOfType<Button>().Subject;
                    maximize.Visibility.Should().Be(Visibility.Collapsed, "dialogs can't be maximized");
                }
                finally
                {
                    window.Close();
                }
            });
        }

        [Fact]
        public void InputDialog_DisablesOkUntilSomethingIsTyped()
        {
            WpfApp.Run(() =>
            {
                var dialog = new InputDialogWindow();
                try
                {
                    var ok = (ButtonBase)dialog.FindName("OkButton");
                    ok.IsEnabled.Should().BeFalse();

                    dialog.InputText = "   ";
                    ok.IsEnabled.Should().BeFalse();

                    dialog.InputText = "https://example.com/video";
                    ok.IsEnabled.Should().BeTrue();
                }
                finally
                {
                    dialog.Close();
                }
            });
        }
    }

    public class DialogOwnerTests
    {
        private sealed record FakeWindow(string Name, bool Visible = true, bool Active = false, bool IsOverlay = false);

        private static FakeWindow? Pick(FakeWindow? preferred, FakeWindow[] open, FakeWindow? main) =>
            DialogService.PickOwner(preferred, open, main, w => w is { Visible: true }, w => w is { Active: true }, w => w is { IsOverlay: false });

        [Fact]
        public void ThePreferredWindow_OwnsTheDialog()
        {
            var settings = new FakeWindow("Settings");
            var launcher = new FakeWindow("Launcher", Active: true);

            Pick(settings, new[] { launcher, settings }, launcher).Should().Be(settings);
        }

        [Fact]
        public void WithoutAPreference_TheActiveWindowOwnsIt()
        {
            var launcher = new FakeWindow("Launcher");
            var settings = new FakeWindow("Settings", Active: true);

            Pick(null, new[] { launcher, settings }, launcher).Should().Be(settings);
        }

        [Fact]
        public void PlaybackOverlays_NeverOwnDialogs()
        {
            var launcher = new FakeWindow("Launcher");
            var overlay = new FakeWindow("Overlay", Active: true, IsOverlay: true);

            Pick(overlay, new[] { overlay, launcher }, launcher).Should().Be(launcher);
        }

        [Fact]
        public void AHiddenMainWindow_LeavesTheDialogUnowned()
        {
            var launcher = new FakeWindow("Launcher", Visible: false);

            Pick(null, new[] { launcher }, launcher).Should().BeNull();
        }
    }
}
