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
using EdgeLoop.Controls;
using EdgeLoop.ViewModels;
using Point = System.Windows.Point;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using System.Windows.Documents;
using DragEventArgs = System.Windows.DragEventArgs;

namespace EdgeLoop.Windows
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public partial class LauncherWindow : EdgeLoopWindow
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
        }

        private Point _startPoint;
        private InsertionAdorner _insertionAdorner;
        private ListViewItem _lastAdornedItem;
        private DragAdorner _dragAdorner;
        private Point _dragGrabOffset;
        private ScrollViewer _playlistScrollViewer;

        private void AddedFilesList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _startPoint = e.GetPosition(null);
        }

        private void AddedFilesList_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;

            Vector diff = _startPoint - e.GetPosition(null);
            if (Math.Abs(diff.X) <= SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(diff.Y) <= SystemParameters.MinimumVerticalDragDistance)
                return;

            // Sliders, the screen picker and buttons keep their own mouse handling
            var originalSource = e.OriginalSource as DependencyObject;
            if (originalSource == null ||
                FindAncestor<Slider>(originalSource) != null ||
                FindAncestor<ComboBox>(originalSource) != null ||
                FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(originalSource) != null)
                return;

            var listViewItem = FindAncestor<ListViewItem>(originalSource);
            if (listViewItem?.DataContext is not VideoItem data) return;

            // A snapshot of the card follows the mouse; the card itself fades while it's moved
            var layer = AdornerLayer.GetAdornerLayer(AddedFilesList);
            if (layer != null)
            {
                _dragGrabOffset = e.GetPosition(listViewItem);
                _dragAdorner = new DragAdorner(AddedFilesList, listViewItem, 0.75)
                {
                    HorizontalOffset = listViewItem.TranslatePoint(new Point(0, 0), AddedFilesList).X
                };
                layer.Add(_dragAdorner);
                MoveDragGhost(e.GetPosition(AddedFilesList));
            }
            listViewItem.Opacity = 0.4;

            try
            {
                DragDrop.DoDragDrop(listViewItem, new DataObject("VideoItem", data), DragDropEffects.Move);
            }
            finally
            {
                listViewItem.Opacity = 1;
                if (_dragAdorner != null)
                {
                    layer?.Remove(_dragAdorner);
                    _dragAdorner = null;
                }
                RemoveInsertionAdorner();
            }
        }

        /// <summary>Keeps the ghost card under the mouse, where it was picked up; it only moves up and down.</summary>
        private void MoveDragGhost(Point positionInList)
        {
            if (_dragAdorner == null) return;
            _dragAdorner.Visibility = Visibility.Visible;
            _dragAdorner.SetPosition(_dragAdorner.HorizontalOffset, positionInList.Y - _dragGrabOffset.Y);
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
            if (_dragAdorner != null)
                _dragAdorner.Visibility = Visibility.Hidden;
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
                MoveDragGhost(e.GetPosition(AddedFilesList));
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

                    e.Effects = Uri.TryCreate(potentialUrl, UriKind.Absolute, out var uri)
                                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                        ? DragDropEffects.Link
                        : DragDropEffects.None;
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
            _playlistScrollViewer ??= FindVisualChild<ScrollViewer>(AddedFilesList);
            if (_playlistScrollViewer == null) return;

            var pos = e.GetPosition(AddedFilesList);
            const double scrollMargin = 40; // pixels from the edge that start scrolling
            const double scrollSpeed = 10;  // pixels per DragOver at the very edge

            if (pos.Y < scrollMargin)
            {
                double intensity = 1 - (pos.Y / scrollMargin);
                _playlistScrollViewer.ScrollToVerticalOffset(_playlistScrollViewer.VerticalOffset - scrollSpeed * intensity);
            }
            else if (pos.Y > AddedFilesList.ActualHeight - scrollMargin)
            {
                double intensity = 1 - ((AddedFilesList.ActualHeight - pos.Y) / scrollMargin);
                _playlistScrollViewer.ScrollToVerticalOffset(_playlistScrollViewer.VerticalOffset + scrollSpeed * intensity);
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
            var result = VisualTreeHelper.HitTest(AddedFilesList, e.GetPosition(AddedFilesList));
            var item = result?.VisualHit is DependencyObject hit ? FindAncestor<ListViewItem>(hit) : null;
            if (item == null)
            {
                RemoveInsertionAdorner();
                return;
            }

            // Before or after the card, by which half the mouse is over
            bool isAfter = e.GetPosition(item).Y > item.ActualHeight / 2;
            if (item != _lastAdornedItem || _insertionAdorner == null || _insertionAdorner.IsAfter != isAfter)
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

        private void RemoveInsertionAdorner()
        {
            _insertionAdorner?.Detach();
            _insertionAdorner = null;
            _lastAdornedItem = null;
        }

        /// <summary>Delete removes the selected card (with Undo); Alt+Up/Down move it.</summary>
        private void AddedFilesList_KeyDown(object sender, KeyEventArgs e)
        {
            // Only when the card itself has focus, not its slider or screen picker
            if (e.OriginalSource is not ListViewItem || AddedFilesList.SelectedItem is not VideoItem selected || ViewModel == null)
                return;

            // With Alt held, WPF reports the key as Key.System and the real key in SystemKey
            var key = e.Key == Key.System ? e.SystemKey : e.Key;

            if (key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
            {
                int index = AddedFilesList.SelectedIndex;
                ViewModel.RemoveItemCommand.Execute(selected);
                if (ViewModel.AddedFiles.Count > 0)
                    SelectAndFocus(ViewModel.AddedFiles[Math.Min(index, ViewModel.AddedFiles.Count - 1)]);
                e.Handled = true;
            }
            else if (Keyboard.Modifiers == ModifierKeys.Alt && (key == Key.Up || key == Key.Down))
            {
                if (ViewModel.MoveVideoItemBy(selected, key == Key.Up ? -1 : 1))
                    SelectAndFocus(selected);
                e.Handled = true;
            }
        }

        /// <summary>Selects an item and gives its card keyboard focus once it's on screen.</summary>
        private void SelectAndFocus(VideoItem item)
        {
            AddedFilesList.SelectedItem = item;
            AddedFilesList.ScrollIntoView(item);
            Dispatcher.InvokeAsync(() =>
                (AddedFilesList.ItemContainerGenerator.ContainerFromItem(item) as ListViewItem)?.Focus(),
                System.Windows.Threading.DispatcherPriority.Loaded);
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
            else if (e.Data.GetDataPresent("VideoItem") && e.Data.GetData("VideoItem") is VideoItem sourceItem && ViewModel != null)
            {
                var files = ViewModel.AddedFiles;
                int oldIndex = files.IndexOf(sourceItem);
                if (oldIndex < 0) return;

                var targetContainer = FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject);
                int destination;
                if (targetContainer?.DataContext is VideoItem targetItem && files.IndexOf(targetItem) is var targetIndex and >= 0)
                {
                    bool isAfter = e.GetPosition(targetContainer).Y > targetContainer.ActualHeight / 2;
                    destination = LauncherViewModel.DropIndex(oldIndex, targetIndex, isAfter, files.Count);
                }
                else
                {
                    destination = files.Count - 1; // Dropped below the last card
                }

                if (destination != oldIndex)
                    ViewModel.MoveVideoItem(sourceItem, destination);
                SelectAndFocus(sourceItem);
            }
        }

        private static T FindAncestor<T>(DependencyObject current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T t) return t;
                // Text elements (Run) aren't visuals; step out through their logical parent
                current = current is Visual or System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(current)
                    : LogicalTreeHelper.GetParent(current);
            }
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
    }
}
