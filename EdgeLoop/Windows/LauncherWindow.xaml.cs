using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using EdgeLoop.Classes;
using EdgeLoop.ViewModels;
using Point = System.Windows.Point;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using System.Windows.Documents;
using DragEventArgs = System.Windows.DragEventArgs;

namespace EdgeLoop.Windows
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public partial class LauncherWindow : Window
    {
        private LauncherViewModel ViewModel => DataContext as LauncherViewModel;

        public LauncherWindow()
        {
            InitializeComponent();
            DataContext = new LauncherViewModel();
            LoadWindowBounds();
        }

        private PlaybackHotkeyManager _hotkeyManager;
        private const string HotkeyWarningPrefix = "Warning: Some hotkeys";

        // Triple-press tracking
        private int _opaquePanicPressCount = 0;
        private DateTime _lastOpaquePanicTime = DateTime.MinValue;

        private void InitializeHotkeys()
        {
            var hotkeys = App.Hotkeys;
            if (hotkeys == null) return;

            hotkeys.Initialize(new System.Windows.Interop.WindowInteropHelper(this).Handle);

            _hotkeyManager = new PlaybackHotkeyManager(hotkeys, () => App.Settings, new PlaybackHotkeyActions
            {
                Panic = () => App.VideoService.StopAll(),
                WipeAndExit = () =>
                {
                    ViewModel?.ClearAllCommand?.Execute(null);
                    Application.Current?.Shutdown();
                },
                OpaquePanic = HandleOpaquePanic,
                Skip = forward =>
                {
                    foreach (var vm in GetContextualSkipTargets())
                    {
                        if (forward) vm.PlayNext(true);
                        else vm.PlayPrevious(true);
                    }
                }
            });

            // Skip and the triple-Escape exit are only registered while something is playing
            _hotkeyManager.SetPlaying(App.VideoService?.ActivePlayers.Count > 0);
            if (App.VideoService != null)
                App.VideoService.ActivePlayers.CollectionChanged += ActivePlayers_CollectionChanged;

            ReportHotkeyFailures(_hotkeyManager.RegisterAll());
        }

        private void ActivePlayers_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (_hotkeyManager == null) return;

            var failures = _hotkeyManager.SetPlaying(App.VideoService.ActivePlayers.Count > 0);
            if (failures.Count > 0)
                ReportHotkeyFailures(failures);
        }

        public void ReloadHotkeys()
        {
            if (_hotkeyManager != null)
                ReportHotkeyFailures(_hotkeyManager.RegisterAll());
        }

        private void ReportHotkeyFailures(IReadOnlyList<string> failures)
        {
            if (failures.Count > 0)
            {
                string msg = $"{HotkeyWarningPrefix} could not be registered: {string.Join(", ", failures)}. They may be in use by another app.";
                ViewModel?.SetStatusMessage(msg, StatusMessageType.Warning);
                Logger.Warning(msg);
            }
            else if (ViewModel?.StatusMessage?.StartsWith(HotkeyWarningPrefix) == true)
            {
                // Clear any previous hotkey warning if everything is now OK
                ViewModel.SetStatusMessage(null);
            }
        }

        private IReadOnlyList<HypnoViewModel> GetContextualSkipTargets()
        {
            var cursorScreen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position)?.DeviceName;
            var primaryScreen = System.Windows.Forms.Screen.PrimaryScreen?.DeviceName;
            return SkipTargetResolver.Resolve(App.VideoService.ActiveViewModels, cursorScreen, primaryScreen);
        }

        private void HandleOpaquePanic()
        {
            var settings = App.Settings;
            if (!settings.AlwaysOpaque) return;

            var now = DateTime.Now;
            if ((now - _lastOpaquePanicTime).TotalSeconds > 1.0)
            {
                _opaquePanicPressCount = 1;
            }
            else
            {
                _opaquePanicPressCount++;
            }

            _lastOpaquePanicTime = now;

            if (_opaquePanicPressCount >= 3)
            {
                Logger.Debug("Emergency stop triggered by triple-press hotkey!");
                App.VideoService.StopAll();
                _opaquePanicPressCount = 0;
            }
        }


        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Responsive layout: Switch to compact buttons if width < 650
            if (e.NewSize.Width < 650)
            {
                if (FullPlaylistButtons != null) FullPlaylistButtons.Visibility = Visibility.Collapsed;
                if (CompactPlaylistButtons != null) CompactPlaylistButtons.Visibility = Visibility.Visible;
            }
            else
            {
                if (FullPlaylistButtons != null) FullPlaylistButtons.Visibility = Visibility.Visible;
                if (CompactPlaylistButtons != null) CompactPlaylistButtons.Visibility = Visibility.Collapsed;
            }
        }

        private void SaveWindowBounds()
        {
            var settings = App.Settings;
            if (settings == null) return;

            if (this.WindowState == WindowState.Normal)
            {
                settings.LauncherWindowWidth = this.ActualWidth;
                settings.LauncherWindowHeight = this.ActualHeight;
                settings.LauncherWindowTop = this.Top;
                settings.LauncherWindowLeft = this.Left;
            }
            else
            {
                settings.LauncherWindowWidth = this.RestoreBounds.Width;
                settings.LauncherWindowHeight = this.RestoreBounds.Height;
                settings.LauncherWindowTop = this.RestoreBounds.Top;
                settings.LauncherWindowLeft = this.RestoreBounds.Left;
            }
            settings.Save();
        }

        private void LoadWindowBounds()
        {
            var settings = App.Settings;
            if (settings == null) return;

            try
            {
                if (settings.LauncherWindowTop != -1 &&
                    double.IsFinite(settings.LauncherWindowTop) &&
                    double.IsFinite(settings.LauncherWindowLeft))
                {
                    if (double.IsFinite(settings.LauncherWindowWidth) && settings.LauncherWindowWidth > 0)
                        this.Width = settings.LauncherWindowWidth;
                    if (double.IsFinite(settings.LauncherWindowHeight) && settings.LauncherWindowHeight > 0)
                        this.Height = settings.LauncherWindowHeight;

                    // Monitors left of or above the primary screen have negative coordinates, so don't clamp
                    // to 0; only fall back to the centred default when the title bar would be off every screen
                    // (e.g. the monitor it was on has been disconnected).
                    if (IsOnConnectedScreen(settings.LauncherWindowLeft, settings.LauncherWindowTop, this.Width))
                    {
                        this.WindowStartupLocation = WindowStartupLocation.Manual;
                        this.Top = settings.LauncherWindowTop;
                        this.Left = settings.LauncherWindowLeft;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning("Failed to load window bounds from settings, using defaults.", ex);
            }
        }

        /// <summary>
        /// Whether a point on the title bar of a window at (left, top) lies in a connected monitor's work area.
        /// Positions are in DIPs; screen areas are scaled from pixels with the primary screen's DPI.
        /// </summary>
        private static bool IsOnConnectedScreen(double left, double top, double width)
        {
            var primary = System.Windows.Forms.Screen.PrimaryScreen;
            if (primary == null || primary.Bounds.Width <= 0)
                return false;

            double pxToDip = SystemParameters.PrimaryScreenWidth / primary.Bounds.Width;
            double x = left + Math.Min(100, Math.Max(0, width) / 2);
            double y = top + 20;

            return System.Windows.Forms.Screen.AllScreens.Any(s =>
            {
                var a = s.WorkingArea;
                return x >= a.Left * pxToDip && x < a.Right * pxToDip &&
                       y >= a.Top * pxToDip && y < a.Bottom * pxToDip;
            });
        }

        protected override void OnClosed(EventArgs e)
        {
            SaveWindowBounds();
            if (App.VideoService != null)
                App.VideoService.ActivePlayers.CollectionChanged -= ActivePlayers_CollectionChanged;
            _hotkeyManager?.Dispose();
            App.Hotkeys?.Dispose();
            (DataContext as IDisposable)?.Dispose();
            base.OnClosed(e);
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            InitializeHotkeys();

            ((System.Windows.Interop.HwndSource)PresentationSource.FromVisual(this)).AddHook(HookProc);
        }

        private IntPtr HookProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == 0x0024)
            { // WM_GETMINMAXINFO
                WmGetMinMaxInfo(hwnd, lParam);
                handled = true;
            }
            return IntPtr.Zero;
        }

        private void WmGetMinMaxInfo(IntPtr hwnd, IntPtr lParam)
        {
            var mmi = (MINMAXINFO)System.Runtime.InteropServices.Marshal.PtrToStructure(lParam, typeof(MINMAXINFO));
            var currentScreen = System.Windows.Forms.Screen.FromHandle(hwnd);
            var workArea = currentScreen.WorkingArea;
            var monitorArea = currentScreen.Bounds;

            // Maximized position should be relative to the monitor it's on
            mmi.ptMaxPosition.x = Math.Abs(workArea.Left - monitorArea.Left);
            mmi.ptMaxPosition.y = Math.Abs(workArea.Top - monitorArea.Top);

            // Max size is the work area size (excludes taskbar)
            mmi.ptMaxSize.x = Math.Abs(workArea.Right - workArea.Left);
            mmi.ptMaxSize.y = Math.Abs(workArea.Bottom - workArea.Top);

            // Crucial: Set max track size to allow the window to actually reach the max size
            // If this isn't set, Windows might caps it at the primary monitor's dimensions.
            mmi.ptMaxTrackSize.x = mmi.ptMaxSize.x;
            mmi.ptMaxTrackSize.y = mmi.ptMaxSize.y;

            System.Runtime.InteropServices.Marshal.StructureToPtr(mmi, lParam, true);
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct POINT
        {
            public int x;
            public int y;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        private void Grid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Mark event as handled to prevent event bubbling issues
            e.Handled = true;

            // Allow dragging even when maximized - restore first then drag
            if (WindowState == WindowState.Maximized)
            {
                // Calculate the position to restore to based on mouse position
                var point = PointToScreen(e.GetPosition(this));
                WindowState = WindowState.Normal;

                // Set window position relative to mouse
                Left = point.X - (RestoreBounds.Width * 0.5);
                Top = point.Y - 10; // Small offset from top
            }

            // Call DragMove immediately while the button is definitely pressed
            // Use the event args button state which is guaranteed to be pressed at this point
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

        // Make the entire window draggable, not just the header
        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Only drag if clicking on the window background, not on controls
            if (e.OriginalSource is FrameworkElement element)
            {
                // Don't drag if clicking on buttons, textboxes, or other interactive controls
                if (element is Button || element is TextBox || element is ComboBox ||
                    element is Slider || element is ListView || element is ListViewItem ||
                    element is ScrollViewer || element is System.Windows.Controls.Primitives.ScrollBar)
                {
                    return;
                }

                // Check if we're clicking on a child of an interactive control
                var parent = VisualTreeHelper.GetParent(element);
                while (parent != null)
                {
                    if (parent is Button || parent is TextBox || parent is ComboBox ||
                        parent is Slider || parent is ListView || parent is ListViewItem ||
                        parent is ScrollViewer || parent is System.Windows.Controls.Primitives.ScrollBar)
                    {
                        return;
                    }
                    parent = VisualTreeHelper.GetParent(parent);
                }
            }

            // Mark event as handled to prevent event bubbling issues
            e.Handled = true;

            // Allow dragging even when maximized - restore first then drag
            if (WindowState == WindowState.Maximized)
            {
                // Calculate the position to restore to based on mouse position
                var point = PointToScreen(e.GetPosition(this));
                WindowState = WindowState.Normal;

                // Set window position relative to mouse, ensuring it's visible
                var windowWidth = ActualWidth > 0 ? ActualWidth : Width;
                var windowHeight = ActualHeight > 0 ? ActualHeight : Height;

                // Center window on mouse cursor
                Left = Math.Max(0, point.X - (windowWidth * 0.5));
                Top = Math.Max(0, point.Y - 10); // Small offset from top

                // Ensure window doesn't go off screen
                var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)point.X, (int)point.Y));
                if (screen != null)
                {
                    var screenBounds = screen.WorkingArea;
                    if (Left + windowWidth > screenBounds.Right)
                    {
                        Left = screenBounds.Right - windowWidth;
                    }
                    if (Top + windowHeight > screenBounds.Bottom)
                    {
                        Top = screenBounds.Bottom - windowHeight;
                    }
                }
            }

            // Call DragMove immediately while the button is definitely pressed
            // Use the event args button state which is guaranteed to be pressed at this point
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



        private Point _startPoint;
        private InsertionAdorner _insertionAdorner;
        private ListViewItem _lastAdornedItem;
        private DragAdorner _dragAdorner;
        private ScrollViewer _playlistScrollViewer;

        private void AddedFilesList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _startPoint = e.GetPosition(null);
        }

        private void AddedFilesList_MouseMove(object sender, MouseEventArgs e)
        {
            Point mousePos = e.GetPosition(null);
            Vector diff = _startPoint - mousePos;

            if (e.LeftButton == MouseButtonState.Pressed &&
                (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                 Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance))
            {

                // Don't trigger drag-drop if we are clicking on a Slider or ComboBox
                DependencyObject originalSource = e.OriginalSource as DependencyObject;
                if (originalSource != null && (FindAncestor<Slider>(originalSource) != null || FindAncestor<ComboBox>(originalSource) != null))
                {
                    return;
                }

                ListView listView = sender as ListView;
                ListViewItem listViewItem = FindAncestor<ListViewItem>(originalSource);
                if (listViewItem == null) return;

                VideoItem data = (VideoItem)listView.ItemContainerGenerator.ItemFromContainer(listViewItem);

                // Create drag adorner (ghost)
                var layer = AdornerLayer.GetAdornerLayer(listView);
                if (layer != null)
                {
                    _dragAdorner = new DragAdorner(listView, listViewItem, 0.6);
                    layer.Add(_dragAdorner);
                }

                DataObject dragData = new DataObject("VideoItem", data);

                try
                {
                    DragDrop.DoDragDrop(listViewItem, dragData, DragDropEffects.Move);
                }
                finally
                {
                    if (_dragAdorner != null)
                    {
                        layer?.Remove(_dragAdorner);
                        _dragAdorner = null;
                    }
                }
            }
        }

        private void AddedFilesList_QueryContinueDrag(object sender, QueryContinueDragEventArgs e)
        {
            if (_dragAdorner != null)
            {
                // We need to update the adorner position. 
                // Since DoDragDrop is blocking, we can't easily get mouse position relative to ListView here 
                // without using Win32 API or tracking it elsewhere.
                // However, GiveFeedback occurs during the drag and can be used too.
            }
        }

        private void AddedFilesList_GiveFeedback(object sender, GiveFeedbackEventArgs e)
        {
            if (_dragAdorner != null)
            {
                // Update ghost position
                var pos = Mouse.GetPosition(AddedFilesList);
                _dragAdorner.SetPosition(pos.X - 20, pos.Y - 20);
            }
        }

        private void AddedFilesList_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop) ||
                e.Data.GetDataPresent(DataFormats.Text) ||
                e.Data.GetDataPresent(DataFormats.UnicodeText) ||
                e.Data.GetDataPresent("VideoItem"))
            {
                PlaylistDropZone.Tag = "DragOver";
            }
        }

        private void AddedFilesList_DragLeave(object sender, DragEventArgs e)
        {
            PlaylistDropZone.Tag = null;
            RemoveInsertionAdorner();
        }

        private void AddedFilesList_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
            }
            else if (e.Data.GetDataPresent("VideoItem"))
            {
                e.Effects = DragDropEffects.Move;
                UpdateInsertionAdorner(e);
            }
            else if (e.Data.GetDataPresent(DataFormats.Text) || e.Data.GetDataPresent(DataFormats.UnicodeText))
            {
                // Check if text is a valid URL
                var text = e.Data.GetData(DataFormats.UnicodeText) as string
                        ?? e.Data.GetData(DataFormats.Text) as string;
                if (!string.IsNullOrEmpty(text))
                {
                    var potentialUrl = text.Trim();
                    if (!potentialUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                        !potentialUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                        !Path.IsPathRooted(potentialUrl) && potentialUrl.Contains("."))
                    {
                        potentialUrl = "https://" + potentialUrl;
                    }

                    if (Uri.TryCreate(potentialUrl, UriKind.Absolute, out var uri)
                        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                    {
                        e.Effects = DragDropEffects.Link;
                    }
                    else
                    {
                        e.Effects = DragDropEffects.None;
                    }
                }
                else
                {
                    e.Effects = DragDropEffects.None;
                }
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }

            if (e.Effects == DragDropEffects.None)
            {
                RemoveInsertionAdorner();
            }

            AutoScrollDuringDrag(e);

            e.Handled = true;
        }

        private void AutoScrollDuringDrag(DragEventArgs e)
        {
            if (_playlistScrollViewer == null)
            {
                _playlistScrollViewer = FindVisualChild<ScrollViewer>(AddedFilesList);
            }

            if (_playlistScrollViewer == null) return;

            var pos = e.GetPosition(AddedFilesList);
            double scrollMargin = 40; // pixels from edge to trigger scroll
            double scrollSpeed = 5;   // pixels per tick

            if (pos.Y < scrollMargin)
            {
                // Scroll up
                double intensity = 1 - (pos.Y / scrollMargin);
                _playlistScrollViewer.ScrollToVerticalOffset(
                    _playlistScrollViewer.VerticalOffset - (scrollSpeed * intensity * 2));
            }
            else if (pos.Y > AddedFilesList.ActualHeight - scrollMargin)
            {
                // Scroll down
                double intensity = 1 - ((AddedFilesList.ActualHeight - pos.Y) / scrollMargin);
                _playlistScrollViewer.ScrollToVerticalOffset(
                    _playlistScrollViewer.VerticalOffset + (scrollSpeed * intensity * 2));
            }
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) return null;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild) return typedChild;
                var result = FindVisualChild<T>(child);
                if (result != null) return result;
            }
            return null;
        }

        private void UpdateInsertionAdorner(DragEventArgs e)
        {
            var pos = e.GetPosition(AddedFilesList);
            var result = VisualTreeHelper.HitTest(AddedFilesList, pos);

            if (result?.VisualHit != null)
            {
                var item = FindAncestor<ListViewItem>(result.VisualHit as DependencyObject);
                if (item != null)
                {
                    // Determine if inserting before or after based on mouse Y position
                    var itemPos = e.GetPosition(item);
                    bool isAfter = itemPos.Y > item.ActualHeight / 2;

                    if (item != _lastAdornedItem || _insertionAdorner == null)
                    {
                        RemoveInsertionAdorner();
                        var layer = AdornerLayer.GetAdornerLayer(item);
                        if (layer != null)
                        {
                            _insertionAdorner = new InsertionAdorner(item, isAfter, layer);
                            _lastAdornedItem = item;
                        }
                    }
                }
                else
                {
                    RemoveInsertionAdorner();
                }
            }
            else
            {
                RemoveInsertionAdorner();
            }
        }

        private void RemoveInsertionAdorner()
        {
            _insertionAdorner?.Detach();
            _insertionAdorner = null;
            _lastAdornedItem = null;
        }

        private void AddedFilesList_KeyDown(object sender, KeyEventArgs e)
        {
            if (AddedFilesList.SelectedItem is VideoItem selectedItem)
            {
                int index = ViewModel.AddedFiles.IndexOf(selectedItem);

                if (e.Key == Key.Delete)
                {
                    ViewModel.AddedFiles.Remove(selectedItem);
                    ViewModel.UpdateButtons();
                    ViewModel.SaveSession();
                    e.Handled = true;
                }
                else if ((Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
                {
                    if (e.Key == Key.Up && index > 0)
                    {
                        ViewModel.AddedFiles.Move(index, index - 1);
                        ViewModel.SaveSession();
                        e.Handled = true;
                    }
                    else if (e.Key == Key.Down && index < ViewModel.AddedFiles.Count - 1)
                    {
                        ViewModel.AddedFiles.Move(index, index + 1);
                        ViewModel.SaveSession();
                        e.Handled = true;
                    }
                }
            }
        }

        private void AddedFilesList_Drop(object sender, DragEventArgs e)
        {
            PlaylistDropZone.Tag = null; // Clear the glow effect
            RemoveInsertionAdorner();

            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                ViewModel?.AddDroppedFiles(files);
            }
            else if (e.Data.GetDataPresent(DataFormats.Text) || e.Data.GetDataPresent(DataFormats.UnicodeText))
            {
                var text = e.Data.GetData(DataFormats.UnicodeText) as string
                        ?? e.Data.GetData(DataFormats.Text) as string;
                if (!string.IsNullOrEmpty(text))
                {
                    ViewModel?.AddDroppedUrl(text.Trim());
                }
            }
            else if (e.Data.GetDataPresent("VideoItem"))
            {
                VideoItem sourceItem = (VideoItem)e.Data.GetData("VideoItem");

                // Find the target item by walking up from the original source
                DependencyObject depObj = e.OriginalSource as DependencyObject;
                ListViewItem listViewItem = FindAncestor<ListViewItem>(depObj);
                VideoItem targetItem = listViewItem?.DataContext as VideoItem;

                if (sourceItem != null && targetItem != null && sourceItem != targetItem)
                {
                    // Find the precise drop position (before or after)
                    var itemPos = e.GetPosition(listViewItem);
                    bool isAfter = itemPos.Y > listViewItem.ActualHeight / 2;

                    int oldIndex = ViewModel.AddedFiles.IndexOf(sourceItem);
                    int targetIndex = ViewModel.AddedFiles.IndexOf(targetItem);

                    if (oldIndex >= 0 && targetIndex >= 0)
                    {
                        int destinationIndex = targetIndex;
                        if (isAfter)
                        {
                            if (oldIndex > targetIndex) destinationIndex++;
                        }
                        else
                        {
                            if (oldIndex < targetIndex) destinationIndex--;
                        }

                        // Bounds check
                        destinationIndex = Math.Max(0, Math.Min(destinationIndex, ViewModel.AddedFiles.Count - 1));

                        if (oldIndex != destinationIndex)
                        {
                            ViewModel.AddedFiles.Move(oldIndex, destinationIndex);
                            ViewModel.SaveSession();
                        }
                    }
                }
                else if (sourceItem != null && targetItem == null)
                {
                    // Dropped in empty space (e.g. at the end), add to end
                    int oldIndex = ViewModel.AddedFiles.IndexOf(sourceItem);
                    if (oldIndex >= 0)
                    {
                        ViewModel.AddedFiles.Move(oldIndex, ViewModel.AddedFiles.Count - 1);
                        ViewModel.SaveSession(); // Persist reorder
                    }
                }
            }
        }

        private static T FindAncestor<T>(DependencyObject current) where T : DependencyObject
        {
            do
            {
                if (current is T t) return t;
                current = VisualTreeHelper.GetParent(current);
            } while (current != null);
            return null;
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var settingsWindow = new SettingsWindow
            {
                Owner = this
            };
            settingsWindow.ShowDialog();
        }

        private void AboutButton_Click(object sender, RoutedEventArgs e)
        {
            var aboutWindow = new AboutWindow
            {
                Owner = this
            };
            aboutWindow.ShowDialog();
        }

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            if (WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;
            }
            else
            {
                WindowState = WindowState.Maximized;
            }
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);

            // Update border corner radius based on window state
            if (WindowState == WindowState.Maximized)
            {
                if (MainBorder != null)
                {
                    MainBorder.CornerRadius = new CornerRadius(0);
                }
                if (HeaderBorder != null)
                {
                    HeaderBorder.CornerRadius = new CornerRadius(0);
                }
            }
            else
            {
                if (MainBorder != null)
                {
                    MainBorder.CornerRadius = new CornerRadius(8);
                }
                if (HeaderBorder != null)
                {
                    HeaderBorder.CornerRadius = new CornerRadius(8, 8, 0, 0);
                }
            }
        }
    }

    public class StringToVisibilityConverter : IValueConverter
    {
        public static readonly StringToVisibilityConverter Instance = new StringToVisibilityConverter();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string str && !string.IsNullOrWhiteSpace(str))
            {
                return Visibility.Visible;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class PluralConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int count)
            {
                return count == 1 ? "video" : "videos";
            }
            return "videos";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class BooleanToVisibilityConverter : IValueConverter
    {
        public static readonly BooleanToVisibilityConverter Instance = new BooleanToVisibilityConverter();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue && boolValue)
            {
                return Visibility.Visible;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Visibility visibility)
            {
                return visibility == Visibility.Visible;
            }
            return false;
        }
    }

    public class InverseBooleanToVisibilityConverter : IValueConverter
    {
        public static readonly InverseBooleanToVisibilityConverter Instance = new InverseBooleanToVisibilityConverter();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue && !boolValue)
            {
                return Visibility.Visible;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Visibility visibility)
            {
                return visibility != Visibility.Visible;
            }
            return false;
        }
    }

    public class InvertedBooleanConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                return !boolValue;
            }
            return true;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                return !boolValue;
            }
            return false;
        }
    }

    public class StatusMessageTypeToBrushConverter : IValueConverter
    {
        public static readonly StatusMessageTypeToBrushConverter Instance = new StatusMessageTypeToBrushConverter();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is ViewModels.StatusMessageType messageType)
            {
                return messageType switch
                {
                    ViewModels.StatusMessageType.Success => new SolidColorBrush(Color.FromArgb(0x33, 0x90, 0xEE, 0x90)), // Soft green with transparency
                    ViewModels.StatusMessageType.Warning => new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xD7, 0x00)), // Golden yellow with transparency
                    ViewModels.StatusMessageType.Error => new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0x44, 0x44)),   // ErrorColor tint: errors must not look like the pink accent
                    _ => new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xB6, 0xD9)) // Info: TextSecondary tint, visible on the dark background
                };
            }
            return new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xB6, 0xD9));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class StatusMessageTypeToForegroundConverter : IValueConverter
    {
        public static readonly StatusMessageTypeToForegroundConverter Instance = new StatusMessageTypeToForegroundConverter();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is ViewModels.StatusMessageType messageType)
            {
                return messageType switch
                {
                    ViewModels.StatusMessageType.Success => new SolidColorBrush(Color.FromArgb(0xCC, 0x90, 0xEE, 0x90)), // Light green
                    ViewModels.StatusMessageType.Warning => new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xD7, 0x00)), // Golden yellow
                    ViewModels.StatusMessageType.Error => new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0x7A, 0x7A)),   // Light red, readable on the dark background
                    _ => new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)) // Default: white
                };
            }
            return new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class ValidationStatusToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is ViewModels.FileValidationStatus status)
            {
                return status switch
                {
                    ViewModels.FileValidationStatus.Valid => new SolidColorBrush(Color.FromArgb(0x66, 0x90, 0xEE, 0x90)), // Soft green border
                    ViewModels.FileValidationStatus.Missing => new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0x69, 0xB4)), // HotPink border
                    ViewModels.FileValidationStatus.Invalid => new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xD7, 0x00)), // Golden yellow border
                    _ => new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0x69, 0xB4)) // Default: HotPink border
                };
            }
            return new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0x69, 0xB4));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class ValidationStatusToIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is ViewModels.FileValidationStatus status)
            {
                return status switch
                {
                    ViewModels.FileValidationStatus.Valid => "✓", // Checkmark
                    ViewModels.FileValidationStatus.Missing => "⚠", // Warning
                    ViewModels.FileValidationStatus.Invalid => "✗", // X mark
                    _ => "▶" // Play icon for unknown/not validated
                };
            }
            return "▶";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class ValidationStatusToOpacityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is ViewModels.FileValidationStatus status)
            {
                return status == ViewModels.FileValidationStatus.Valid ? 1.0 : 0.7; // Gray out invalid files
            }
            return 1.0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class ValidationStatusToForegroundConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is ViewModels.FileValidationStatus status)
            {
                return status switch
                {
                    ViewModels.FileValidationStatus.Valid => new SolidColorBrush(Color.FromRgb(0x90, 0xEE, 0x90)), // Soft green
                    ViewModels.FileValidationStatus.Missing => new SolidColorBrush(Color.FromRgb(0xFF, 0x69, 0xB4)), // HotPink
                    ViewModels.FileValidationStatus.Invalid => new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00)), // Golden yellow
                    _ => new SolidColorBrush(Color.FromRgb(0xFF, 0x69, 0xB4)) // HotPink for unknown
                };
            }
            return new SolidColorBrush(Color.FromRgb(0xFF, 0x69, 0xB4));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class OpacityToIntConverter : IValueConverter
    {
        private const double MaxOpacity = 0.90;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double opacity)
            {
                // Scale 0-0.90 opacity range to 0-100 display range
                // Cap at 100 to avoid "111" during AlwaysOpaque mode
                int displayValue = (int)Math.Round((opacity / MaxOpacity) * 100);
                return Math.Min(100, displayValue).ToString();
            }
            return "100";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class OpacityScaleConverter : IValueConverter
    {
        private const double MaxOpacity = 0.90;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // Convert: opacity (0-0.90) -> slider value (0-1.0)
            if (value is double opacity)
            {
                return opacity / MaxOpacity;
            }
            return 1.0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // ConvertBack: slider value (0-1.0) -> opacity (0-0.90)
            if (value is double sliderValue)
            {
                return sliderValue * MaxOpacity;
            }
            return 0.0;
        }
    }
    public class InvertedBooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool boolValue = value is bool b && b;
            return boolValue ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class IsProgressIndeterminateConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double progress)
            {
                return progress <= 0;
            }
            return true;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}


