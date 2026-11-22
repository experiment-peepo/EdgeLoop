using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using EdgeLoop.Classes;
using EdgeLoop.Windows;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace EdgeLoop.ViewModels
{
    /// <summary>
    /// Type of status message for styling purposes
    /// </summary>
    public enum StatusMessageType
    {
        Info,
        Success,
        Warning,
        Error
    }

    /// <summary>
    /// ViewModel for the main launcher window, managing video files, screens, and playback
    /// </summary>
    [SupportedOSPlatform("windows")]
    public class LauncherViewModel : ObservableObject, IDisposable
    {
        public ObservableCollection<VideoItem> AddedFiles { get; } = new ObservableCollection<VideoItem>();
        public ObservableCollection<ScreenViewer> AvailableScreens { get; } = new ObservableCollection<ScreenViewer>();
        public ObservableCollection<ActivePlayerViewModel> ActivePlayers => App.VideoService.ActivePlayers;

        public bool HasActivePlayers => ActivePlayers.Count > 0;

        /// <summary>Playlist entries that failed to play in this playback session.</summary>
        public int FailedCount => AddedFiles.Count(f => f.PlaybackFailed);

        private string _startHint = "Add videos first";
        /// <summary>What Start will do, or why it's disabled. Shown as the button's tooltip.</summary>
        public string StartHint
        {
            get => _startHint;
            private set => SetProperty(ref _startHint, value);
        }

        private Random random = new Random();

        public bool Shuffle
        {
            get => App.Settings?.VideoShuffle ?? false;
            set
            {
                if (App.Settings != null)
                {
                    if (App.Settings.VideoShuffle != value)
                    {
                        App.Settings.VideoShuffle = value;
                        OnPropertyChanged(nameof(Shuffle));
                        App.Settings.Save(); // Save immediately when toggled
                    }
                }
            }
        }

        private bool _isHypnotizeEnabled;
        public bool IsHypnotizeEnabled
        {
            get => _isHypnotizeEnabled;
            set => SetProperty(ref _isHypnotizeEnabled, value);
        }

        private bool _allFilesAssigned = false;
        private readonly StatusBanner _status = new StatusBanner();
        private bool _isLoading;
        private double _importProgressValue;
        private string _importProgressText;

        public string StatusMessage => _status.Message;

        /// <summary>Label of the status message's button (e.g. "Undo"), or null.</summary>
        public string StatusActionText => _status.ActionText;

        public double ImportProgressValue
        {
            get => _importProgressValue;
            set => SetProperty(ref _importProgressValue, value);
        }

        public string ImportProgressText
        {
            get => _importProgressText;
            set => SetProperty(ref _importProgressText, value);
        }

        public StatusMessageType StatusMessageType => _status.Type;

        /// <summary>
        /// Shows a status message. Info and success messages clear themselves after a few seconds
        /// (but not while an import is still running); warnings and errors stay until dismissed or replaced.
        /// </summary>
        public void SetStatusMessage(string message, StatusMessageType type = StatusMessageType.Info)
        {
            if (!string.IsNullOrEmpty(message))
            {
                Logger.Debug($"[Launcher] Status: {message} ({type})");
            }
            _status.Show(message, type);
        }

        public bool IsLoading
        {
            get => _isLoading;
            set => SetProperty(ref _isLoading, value);
        }

        public ICommand HypnotizeCommand { get; }
        public ICommand DehypnotizeCommand { get; }

        public ICommand BrowseCommand { get; }
        public ICommand AddUrlOrPlaylistCommand { get; }
        public ICommand ClearAllCommand { get; }
        public ICommand ExitCommand { get; }
        public ICommand CancelImportCommand { get; }
        public ICommand DismissStatusCommand { get; }
        public ICommand StatusActionCommand { get; }
        public ICommand RemoveFailedCommand { get; }

        private System.Windows.Threading.DispatcherTimer _saveTimer;
        private CancellationTokenSource _cancellationTokenSource;
        private CancellationTokenSource _importCts;
        private readonly SemaphoreSlim _saveSemaphore = new SemaphoreSlim(1, 1);
        private readonly object _pendingSessionLock = new object();
        private List<PlaylistItem> _pendingSession; // Latest playlist snapshot not yet written, or null
        private bool _isHypnotizing = false;
        private readonly IVideoUrlExtractor _urlExtractor;
        private readonly PlaylistImporter _playlistImporter;

        public LauncherViewModel()
        {
            _status.IsBusy = () => IsLoading;
            _status.PropertyChanged += (s, e) => OnPropertyChanged(e.PropertyName switch
            {
                nameof(StatusBanner.Message) => nameof(StatusMessage),
                nameof(StatusBanner.ActionText) => nameof(StatusActionText),
                _ => nameof(StatusMessageType)
            });

            _cancellationTokenSource = new CancellationTokenSource();
            _urlExtractor = App.UrlExtractor ?? new VideoUrlExtractor();
            _playlistImporter = new PlaylistImporter(_urlExtractor);
            RefreshScreens();

            HypnotizeCommand = new RelayCommand(Hypnotize, _ => IsHypnotizeEnabled);
            DehypnotizeCommand = new RelayCommand(Dehypnotize);

            BrowseCommand = new RelayCommand(Browse);
            AddUrlOrPlaylistCommand = new RelayCommand(AddUrlOrPlaylist);
            RemoveItemCommand = new RelayCommand(RemoveItem);
            ClearAllCommand = new RelayCommand(ClearAll);
            DismissStatusCommand = new RelayCommand(_ => SetStatusMessage(null));
            StatusActionCommand = new RelayCommand(_ => _status.RunAction());
            RemoveFailedCommand = new RelayCommand(RemoveFailed, _ => FailedCount > 0);
            SavePlaylistCommand = new RelayCommand(SavePlaylist);
            LoadPlaylistCommand = new RelayCommand(LoadPlaylist);
            ExitCommand = new RelayCommand(Exit);
            CancelImportCommand = new RelayCommand(CancelImport);

            // Subscribe to media events
            App.VideoService.MediaErrorOccurred += VideoService_MediaErrorOccurred;
            App.VideoService.MediaOpened += VideoService_MediaOpened;

            UpdateButtons();

            // Subscribe to ActivePlayers changes to update HasActivePlayers property
            ActivePlayers.CollectionChanged += (s, e) => OnPropertyChanged(nameof(HasActivePlayers));

            // Load session if auto-load is enabled (async to avoid blocking UI)
            try
            {
                if (App.Settings != null && App.Settings.RememberLastPlaylist)
                {
                    _ = InitializeAsync(_cancellationTokenSource.Token);
                }
                else
                {
                    _ = InitializeAsyncWithoutSession(_cancellationTokenSource.Token);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning("Failed to auto-load session", ex);
            }

            // Subscribe to display settings changes to invalidate screen cache
            SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;

            try
            {
                // Check if we have a valid dispatcher context
                if (Application.Current != null || System.Windows.Threading.Dispatcher.FromThread(Thread.CurrentThread) != null)
                {
                    _saveTimer = new System.Windows.Threading.DispatcherTimer();
                    _saveTimer.Interval = TimeSpan.FromMilliseconds(500);
                    _saveTimer.Tick += (s, e) =>
                    {
                        _saveTimer.Stop();
                        SaveSession();
                    };
                }
            }
            catch (Exception ex)
            {
                Logger.Warning("Failed to initialize save timer (likely due to missing Dispatcher in test environment)", ex);
            }

            AddedFiles.CollectionChanged += (s, e) =>
            {
                if (e.NewItems != null)
                {
                    foreach (VideoItem item in e.NewItems)
                        item.PropertyChanged += VideoItem_PropertyChanged;
                    DurationProbe.Shared.Request(e.NewItems.Cast<VideoItem>());
                }
                if (e.OldItems != null)
                {
                    foreach (VideoItem item in e.OldItems) item.PropertyChanged -= VideoItem_PropertyChanged;
                }
                // Bulk changes re-check the playlist once, when they finish
                if (_bulkChangeDepth == 0)
                    OnPlaylistChanged();
            };
            foreach (var item in AddedFiles) item.PropertyChanged += VideoItem_PropertyChanged;
            UpdateAllFilesAssigned();
        }

        [SupportedOSPlatform("windows")]
        private void SystemEvents_DisplaySettingsChanged(object sender, EventArgs e)
        {
            // Invalidate screen cache when display settings change
            InvalidateScreenCache();
            // Refresh screens on UI thread
            Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                RefreshScreens();
            });
        }

        private void VideoItem_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(VideoItem.PlaybackFailed))
            {
                OnPropertyChanged(nameof(FailedCount));
                CommandManager.InvalidateRequerySuggested();
            }
            else if (e.PropertyName == nameof(VideoItem.Duration))
            {
                TriggerDebouncedSave(); // Streams learn their length while playing; keep it for next time
            }

            if (e.PropertyName == nameof(VideoItem.Opacity) || e.PropertyName == nameof(VideoItem.Volume) || e.PropertyName == nameof(VideoItem.AssignedScreen))
            {
                TriggerDebouncedSave();
                // Update assignment status when AssignedScreen changes
                if (e.PropertyName == nameof(VideoItem.AssignedScreen))
                {
                    UpdateAllFilesAssigned();
                }
            }
        }

        private void TriggerDebouncedSave()
        {
            if (_saveTimer != null)
            {
                _saveTimer.Stop();
                _saveTimer.Start();
            }
            else
            {
                // If timer is not available (e.g. in tests), just save immediately or skip
                // Ideally in tests we might not care about saving, or we mock it.
                // For now, let's just skip saving to avoid errors.
            }
        }

        public ICommand RemoveItemCommand { get; }
        public ICommand SavePlaylistCommand { get; }
        public ICommand LoadPlaylistCommand { get; }

        private void RemoveItem(object parameter)
        {
            if (parameter is VideoItem item)
                RemoveItems(new[] { item }, $"Removed \u201C{item.FileName}\u201D");
        }

        /// <summary>
        /// Removes items from the playlist and from any running players, then offers Undo in the status bar.
        /// </summary>
        private void RemoveItems(IReadOnlyCollection<VideoItem> items, string message)
        {
            var removed = items
                .Select(item => (Item: item, Index: AddedFiles.IndexOf(item)))
                .Where(r => r.Index >= 0)
                .OrderBy(r => r.Index)
                .ToList();
            if (removed.Count == 0) return;

            using (BeginBulkChange())
            {
                foreach (var r in removed)
                    AddedFiles.Remove(r.Item);
            }

            // CRITICAL: Notify active players to remove these from their queues
            App.VideoService.RemoveItemsFromAllPlayers(removed.Select(r => r.Item).ToList());

            UpdateButtons();
            SaveSession();
            _status.Show(message, StatusMessageType.Info, "Undo", () => RestoreItems(removed));
        }

        private void RestoreItems(List<(VideoItem Item, int Index)> removed)
        {
            using (BeginBulkChange())
            {
                // In ascending order, inserting each at its old index rebuilds the original order
                foreach (var (item, index) in removed)
                    AddedFiles.Insert(Math.Min(index, AddedFiles.Count), item);
            }

            UpdateButtons();
            SaveSession();
            SetStatusMessage(removed.Count == 1 ? "Restored 1 video" : $"Restored {removed.Count} videos", StatusMessageType.Success);
        }

        private void RemoveFailed(object obj)
        {
            var failed = AddedFiles.Where(f => f.PlaybackFailed).ToList();
            if (failed.Count > 0)
                RemoveItems(failed, failed.Count == 1 ? "Removed 1 video that failed to play" : $"Removed {failed.Count} videos that failed to play");
        }

        private int _bulkChangeDepth;

        /// <summary>
        /// While open, adding or removing items doesn't re-check the whole playlist (and every button)
        /// after each item; it's checked once when the last scope closes.
        /// </summary>
        private IDisposable BeginBulkChange()
        {
            _bulkChangeDepth++;
            return new ScopeExit(() =>
            {
                if (--_bulkChangeDepth == 0)
                    OnPlaylistChanged();
            });
        }

        private sealed class ScopeExit(Action onExit) : IDisposable
        {
            private Action _onExit = onExit;
            public void Dispose() => Interlocked.Exchange(ref _onExit, null)?.Invoke();
        }

        private void OnPlaylistChanged()
        {
            UpdateAllFilesAssigned();
            OnPropertyChanged(nameof(FailedCount));
        }

        /// <summary>Moves an item one place up (-1) or down (+1), as Alt+Up/Down do.</summary>
        public bool MoveVideoItemBy(VideoItem item, int offset)
        {
            int index = AddedFiles.IndexOf(item);
            int target = index + offset;
            if (index < 0 || target < 0 || target >= AddedFiles.Count) return false;
            MoveVideoItem(item, target);
            return true;
        }

        /// <summary>
        /// The index an item dragged from <paramref name="oldIndex"/> moves to when dropped before
        /// (or <paramref name="after"/>) the item at <paramref name="targetIndex"/>.
        /// </summary>
        internal static int DropIndex(int oldIndex, int targetIndex, bool after, int count)
        {
            int destination = targetIndex;
            if (after && oldIndex > targetIndex) destination++;
            else if (!after && oldIndex < targetIndex) destination--;
            return Math.Clamp(destination, 0, Math.Max(0, count - 1));
        }

        private List<ScreenViewer> _cachedScreens;
        private DateTime _screensCacheTime = DateTime.MinValue;
        private static readonly TimeSpan ScreenCacheTimeout = TimeSpan.FromSeconds(5);

        [SupportedOSPlatform("windows")]
        private void RefreshScreens()
        {
            // Use cached screens if available and recent
            if (_cachedScreens != null && DateTime.Now - _screensCacheTime < ScreenCacheTimeout)
            {
                // If we already have screens and they match one-to-one, don't clear (preserves selection better)
                if (AvailableScreens.Count > 0 && AvailableScreens.Skip(1).Select(s => s.DeviceName).SequenceEqual(_cachedScreens.Select(s => s.DeviceName)))
                {
                    return;
                }

                AvailableScreens.Clear();
                AvailableScreens.Add(ScreenViewer.CreateAllScreens());
                foreach (var s in _cachedScreens)
                {
                    AvailableScreens.Add(s);
                }
                return;
            }

            try
            {
                var screens = WindowServices.GetAllScreenViewers();

                // Store existing assignments by device name to restore them after refresh
                // Use a loop to handle potential duplicates in AddedFiles safely
                var assignments = new Dictionary<VideoItem, string>();
                foreach (var f in AddedFiles)
                {
                    if (f != null && f.AssignedScreen != null)
                    {
                        assignments[f] = f.AssignedScreen.DeviceName;
                    }
                }

                _cachedScreens = screens;
                _screensCacheTime = DateTime.Now;

                AvailableScreens.Clear();
                // Add "All Monitors" option first
                AvailableScreens.Add(ScreenViewer.CreateAllScreens());
                foreach (var s in screens)
                {
                    AvailableScreens.Add(s);
                }

                // Restore assignments
                foreach (var f in AddedFiles)
                {
                    if (assignments.TryGetValue(f, out var deviceName))
                    {
                        var newScreen = AvailableScreens.FirstOrDefault(s => s.DeviceName == deviceName);
                        if (newScreen != null)
                        {
                            f.AssignedScreen = newScreen;
                        }
                    }
                }

                // Ensure we have at least one screen
                if (AvailableScreens.Count <= 1)
                { // 1 because of "All Monitors"
                    Logger.Warning("No real screens detected, using default screen");
                    SetStatusMessage("No screens were detected, so the main screen is used.", StatusMessageType.Warning);

                    var defaultScreen = System.Windows.Forms.Screen.PrimaryScreen ?? System.Windows.Forms.Screen.AllScreens.FirstOrDefault();
                    if (defaultScreen != null)
                    {
                        var defaultViewer = new ScreenViewer(defaultScreen);
                        if (!AvailableScreens.Any(s => s.DeviceName == defaultViewer.DeviceName))
                        {
                            AvailableScreens.Add(defaultViewer);
                            _cachedScreens = new List<ScreenViewer> { defaultViewer };
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to refresh screens", ex);
                SetStatusMessage($"Couldn't read the screens: {ex.Message}", StatusMessageType.Error);
                // Add a fallback screen
                try
                {
                    var fallbackScreen = System.Windows.Forms.Screen.PrimaryScreen;
                    if (fallbackScreen != null)
                    {
                        var fallbackViewer = new ScreenViewer(fallbackScreen);
                        AvailableScreens.Add(fallbackViewer);
                        _cachedScreens = new List<ScreenViewer> { fallbackViewer };
                    }
                }
                catch (Exception ex2)
                {
                    Logger.Error("Failed to add fallback screen", ex2);
                }
            }
        }

        public void InvalidateScreenCache()
        {
            _cachedScreens = null;
            _screensCacheTime = DateTime.MinValue;
        }

        /// <summary>
        /// Gets the default screen based on settings, falling back to primary screen if the saved monitor is unavailable
        /// </summary>
        [SupportedOSPlatform("windows")]
        private ScreenViewer GetDefaultScreen()
        {
            if (AvailableScreens.Count == 0) RefreshScreens();

            var settings = App.Settings;
            if (!string.IsNullOrEmpty(settings.DefaultMonitorDeviceName))
            {
                var defaultScreen = AvailableScreens.FirstOrDefault(s => s.DeviceName == settings.DefaultMonitorDeviceName);
                if (defaultScreen != null)
                {
                    return defaultScreen;
                }
            }

            // Fall back to primary screen, or first available REAL screen if no primary
            return AvailableScreens.FirstOrDefault(v => v.Screen != null && v.Screen.Primary)
                ?? AvailableScreens.FirstOrDefault(v => !v.IsAllScreens);
        }

        public void UpdateButtons()
        {
            bool hasFiles = AddedFiles.Count > 0;
            IsHypnotizeEnabled = hasFiles && _allFilesAssigned;
            StartHint = !hasFiles ? "Add videos first"
                : !_allFilesAssigned ? "Choose a screen for every video first"
                : "Start playback on the assigned screens";

            // Force re-evaluation of command CanExecute states
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }

        private void UpdateAllFilesAssigned()
        {
            _allFilesAssigned = AddedFiles.Count > 0 && AddedFiles.All(f => f.AssignedScreen != null);
            UpdateButtons();
        }

        private void Hypnotize(object parameter)
        {
            if (_isHypnotizing) return;
            _isHypnotizing = true;

            // Clear any previous error/status message ONLY when starting a new session manually
            SetStatusMessage(null);

            try
            {
                var (assignments, isAllMonitors) = BuildAssignments();

                // Handle null assignments from BuildAssignmentsFromSelection
                if (assignments == null)
                {
                    SetStatusMessage("None of the videos could be given a screen. Choose a screen for each video.", StatusMessageType.Error);
                    return;
                }

                int totalItems = assignments.Values.Sum(v => v.Count());
                Logger.Debug($"Hypnotize called. Queuing {totalItems} items across {assignments.Count} screens.");

                if (assignments.Count == 0)
                {
                    SetStatusMessage("Choose a screen for every video first.", StatusMessageType.Error);
                    return;
                }

                // Use async version to avoid deadlocks
                _ = PlayPerMonitorAsync(assignments, isAllMonitors).ContinueWith(task =>
                {
                    _isHypnotizing = false;
                    if (task.IsFaulted)
                    {
                        var ex = task.Exception?.GetBaseException() ?? task.Exception;
                        Logger.Error("Error starting playback", ex);
                        Application.Current?.Dispatcher.InvokeAsync(() =>
                        {
                            SetStatusMessage($"Couldn't start playback: {ex?.Message ?? "unknown error"}", StatusMessageType.Error);
                        });
                    }
                }, TaskContinuationOptions.ExecuteSynchronously);
            }
            catch (Exception ex)
            {
                _isHypnotizing = false;
                Logger.Error("Unexpected error in Hypnotize", ex);
            }
        }

        private async Task PlayPerMonitorAsync(IDictionary<ScreenViewer, IEnumerable<VideoItem>> assignments, bool showGroupControl)
        {
            await App.VideoService.PlayPerMonitorAsync(assignments, showGroupControl, null, _cancellationTokenSource.Token).ConfigureAwait(false);
        }

        private (Dictionary<ScreenViewer, IEnumerable<VideoItem>> Assignments, bool HasAllMonitors) BuildAssignments()
        {
            var selectedFiles = AddedFiles.ToList();

            if (selectedFiles.Count < 1) return (null, false);

            // Simple validation again just in case
            if (selectedFiles.Any(x => x.AssignedScreen == null)) return (null, false);

            // A new playback session retries items that failed last time (failures can be temporary)
            foreach (var f in selectedFiles)
                f.PlaybackError = null;

            if (Shuffle)
            {
                // Fisher-Yates shuffle - O(n) instead of O(n log n)
                for (int i = selectedFiles.Count - 1; i > 0; i--)
                {
                    int j = random.Next(i + 1);
                    var temp = selectedFiles[i];
                    selectedFiles[i] = selectedFiles[j];
                    selectedFiles[j] = temp;
                }
            }

            var assignments = new Dictionary<ScreenViewer, IEnumerable<VideoItem>>();
            var allMonitorsItems = new List<VideoItem>();

            foreach (var f in selectedFiles)
            {
                var assigned = f.AssignedScreen;
                if (assigned == null) continue;

                if (assigned.IsAllScreens)
                {
                    allMonitorsItems.Add(f);
                }
                else
                {
                    if (!assignments.ContainsKey(assigned)) assignments[assigned] = new List<VideoItem>();
                    ((List<VideoItem>)assignments[assigned]).Add(f);
                }
            }

            // If we have "All Monitors" items, add them to ALL assigned screens
            // OR if only "All Monitors" items exist, add them to ALL available screens
            if (allMonitorsItems.Count > 0)
            {
                var targetScreens = assignments.Keys.ToList();

                // If no specific screens are assigned, use all available screens (excluding the "All Monitors" placeholder itself)
                if (targetScreens.Count == 0)
                {
                    targetScreens = AvailableScreens.Where(s => !s.IsAllScreens).ToList();
                }

                foreach (var screen in targetScreens)
                {
                    if (!assignments.ContainsKey(screen)) assignments[screen] = new List<VideoItem>();
                    var list = (List<VideoItem>)assignments[screen];
                    // Add items from allMonitorsItems, but respect original sequence if possible
                    // Here we just append them
                    list.AddRange(allMonitorsItems);
                }
            }

            return (assignments, allMonitorsItems.Count > 0);
        }

        private void Dehypnotize(object obj)
        {
            App.VideoService.StopAll();
        }



        private void Browse(object obj)
        {
            // Capture dispatcher before starting the background task
            var dispatcher = Application.Current?.Dispatcher;

            // Safely execute async code from void command handler
            // This pattern ensures exceptions are properly caught and handled
            _ = BrowseAsync().ContinueWith(task =>
            {
                if (task.IsFaulted)
                {
                    var ex = task.Exception?.GetBaseException() ?? task.Exception;
                    Logger.Error("Error in Browse operation", ex);
                    dispatcher?.InvokeAsync(() =>
                    {
                        SetStatusMessage($"Couldn't add the files: {ex?.Message ?? "unknown error"}", StatusMessageType.Error);
                    });
                }
            }, TaskContinuationOptions.OnlyOnFaulted);
        }

        private async Task BrowseAsync()
        {
            var dlg = new OpenFileDialog
            {
                Multiselect = true,
                Filter = $"Video Files|{string.Join(";", Constants.VideoExtensions.Select(e => $"*{e}"))}|All Files|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                await AddFilesAsync(dlg.FileNames, BeginImport());
            }
        }

        private async Task ProcessUrlAsync(string inputUrl, CancellationToken cancellationToken = default)
        {
            ShowImportProgress("Processing URL...");

            try
            {
                // Automatically prepend https:// if missing
                if (!inputUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !inputUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                    !Path.IsPathRooted(inputUrl))
                {
                    inputUrl = "https://" + inputUrl;
                }
                // Check if it's a page URL that needs extraction
                string finalUrl = inputUrl;
                // Sites that need a download (YouTube, Vimeo, ...) are downloaded when the video plays, not while adding it
                if (FileValidator.IsPageUrl(inputUrl) && !YtDlpService.IsDownloadRequiredSite(new Uri(inputUrl).Host))
                {
                    ShowImportProgress("Extracting video URL from page...");
                    finalUrl = await _urlExtractor.ExtractVideoUrlAsync(inputUrl, cancellationToken);

                    if (string.IsNullOrWhiteSpace(finalUrl))
                    {
                        SetStatusMessage(SiteRestrictions.Describe(inputUrl) is string blocked
                            ? $"Couldn't add that video. {blocked}"
                            : "Couldn't find a video on that page. It may not have one, or the site may have changed.", StatusMessageType.Error);
                        return;
                    }
                }

                // Validate the result (could be a streaming URL or a local file path from extraction)
                bool isValid;
                string errorMessage;
                if (Path.IsPathRooted(finalUrl) || finalUrl.StartsWith("file://"))
                {
                    // It's a local file (e.g. from yt-dlp download+mux)
                    string localPath = finalUrl.StartsWith("file://") ? finalUrl.Substring(7) : finalUrl;
                    isValid = FileValidator.ValidateVideoFile(localPath, out errorMessage);
                }
                else
                {
                    // It's a streaming URL
                    isValid = FileValidator.ValidateVideoUrl(finalUrl, out errorMessage);
                }

                if (!isValid)
                {
                    SetStatusMessage($"That video can't be played: {errorMessage}", StatusMessageType.Error);
                    return;
                }

                // Check for duplicates
                var normalizedUrl = FileValidator.NormalizeUrl(finalUrl);
                var existingPaths = new HashSet<string>(AddedFiles.Select(x =>
                    x.IsUrl ? FileValidator.NormalizeUrl(x.FilePath) : x.FilePath),
                    StringComparer.OrdinalIgnoreCase);

                if (existingPaths.Contains(normalizedUrl))
                {
                    SetStatusMessage("That video is already in the playlist.", StatusMessageType.Warning);
                    return;
                }

                // Ensure screens are up to date
                if (AvailableScreens.Count == 0) RefreshScreens();
                var defaultScreen = GetDefaultScreen();

                // Create and add video item
                var item = new VideoItem(finalUrl, defaultScreen);
                // CRITICAL: Set OriginalPageUrl if the input was a page URL
                // This ensures stable position tracking even if the finalUrl is a resolved expiring link
                if (FileValidator.IsPageUrl(inputUrl))
                {
                    item.OriginalPageUrl = inputUrl;
                }
                var settings = App.Settings;
                item.Opacity = settings.DefaultOpacity;
                item.Volume = settings.DefaultVolume;

                // Try to extract title if it was a page URL (but never fail if extraction fails)
                if (FileValidator.IsPageUrl(inputUrl))
                {
                    try
                    {
                        var title = await _urlExtractor.ExtractVideoTitleAsync(inputUrl, cancellationToken);
                        if (!string.IsNullOrWhiteSpace(title))
                        {
                            item.Title = title;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"Error extracting title from {inputUrl}: {ex.Message}. VideoItem created without title.");
                        // Continue - VideoItem will use URL-based name extraction
                    }
                }

                item.Validate();

                if (item.ValidationStatus == FileValidationStatus.Valid)
                {
                    AddedFiles.Add(item);

                    SetStatusMessage($"Added \u201C{item.FileName}\u201D", StatusMessageType.Success);
                    UpdateButtons();
                    SaveSession();
                }
                else
                {
                    SetStatusMessage($"That link can't be played: {item.ValidationError}", StatusMessageType.Error);
                }
            }
            catch (OperationCanceledException)
            {
                SetStatusMessage("Cancelled", StatusMessageType.Warning);
            }
            catch (Exception ex)
            {
                Logger.Error("Error processing URL", ex);
                SetStatusMessage($"Couldn't add the link: {ex.Message}", StatusMessageType.Error);
            }
        }


        private void SavePlaylist(object parameter)
        {
            var sfd = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "M3U Playlist (*.m3u)|*.m3u|JSON Playlist (*.json)|*.json",
                FileName = $"Playlist_{DateTime.Now:yyyyMMdd_HHmm}.m3u"
            };

            if (sfd.ShowDialog() == true)
            {
                _ = SavePlaylistAsync(sfd.FileName);
            }
        }

        private async Task SavePlaylistAsync(string filePath)
        {
            try
            {
                if (filePath.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase))
                {
                    using (var writer = new StreamWriter(filePath))
                    {
                        await writer.WriteLineAsync("#EXTM3U");
                        foreach (var item in AddedFiles)
                        {
                            var title = item.FileName;
                            // Prefer OriginalPageUrl for compatibility if it's a resolved expiring link
                            var path = !string.IsNullOrEmpty(item.OriginalPageUrl) ? item.OriginalPageUrl : item.FilePath;
                            await writer.WriteLineAsync($"#EXTINF:0,{title}");
                            await writer.WriteLineAsync(path);
                        }
                    }
                }
                else
                {
                    // Save as JSON (Full session compatibility)
                    var playlist = new Playlist
                    {
                        Items = AddedFiles.Select(item => new PlaylistItem
                        {
                            FilePath = item.FilePath,
                            Title = item.Title,
                            OriginalPageUrl = item.OriginalPageUrl,
                            Opacity = item.Opacity,
                            Volume = item.Volume,
                            ScreenDeviceName = item.AssignedScreen?.Screen?.DeviceName,
                            DurationSeconds = item.Duration?.TotalSeconds
                        }).ToList()
                    };
                    var json = JsonSerializer.Serialize(playlist, new JsonSerializerOptions { WriteIndented = true });
                    await File.WriteAllTextAsync(filePath, json);
                }
                SetStatusMessage($"Playlist saved to {Path.GetFileName(filePath)}", StatusMessageType.Success);
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to save playlist", ex);
                SetStatusMessage($"Couldn't save the playlist: {ex.Message}", StatusMessageType.Error);
            }
        }

        private void LoadPlaylist(object parameter)
        {
            var ofd = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Playlist Files (*.m3u;*.json)|*.m3u;*.json|M3U Playlist (*.m3u)|*.m3u|JSON Playlist (*.json)|*.json|All Files (*.*)|*.*",
                Title = "Select Playlist File"
            };

            if (ofd.ShowDialog() == true)
            {
                _ = LoadPlaylistContentAsync(ofd.FileName);
            }
        }

        private async Task LoadPlaylistContentAsync(string filePath)
        {
            IsLoading = true;
            SetStatusMessage($"Loading playlist {Path.GetFileName(filePath)}\u2026", StatusMessageType.Info);

            try
            {
                List<VideoItem> itemsToLoad = new List<VideoItem>();

                if (filePath.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase))
                {
                    // Basic M3U Parser
                    var lines = await File.ReadAllLinesAsync(filePath);
                    foreach (var line in lines)
                    {
                        var trimmed = line.Trim();
                        if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#")) continue;

                        // Treat line as a URL or FilePath
                        var item = new VideoItem(trimmed, GetDefaultScreen());
                        item.Validate();
                        itemsToLoad.Add(item);
                    }
                }
                else
                {
                    // JSON Loader
                    var json = await File.ReadAllTextAsync(filePath);
                    var playlist = JsonSerializer.Deserialize<Playlist>(json);
                    if (playlist?.Items != null)
                    {
                        foreach (var pi in playlist.Items)
                        {
                            var item = new VideoItem(pi.FilePath)
                            {
                                Title = pi.Title,
                                OriginalPageUrl = pi.OriginalPageUrl,
                                Opacity = pi.Opacity,
                                Volume = pi.Volume,
                                Duration = pi.Duration
                            };

                            if (!string.IsNullOrEmpty(pi.ScreenDeviceName))
                            {
                                item.AssignedScreen = AvailableScreens.FirstOrDefault(s => s.Screen?.DeviceName == pi.ScreenDeviceName);
                            }

                            if (item.AssignedScreen == null) item.AssignedScreen = GetDefaultScreen();

                            // A web video saved while it played from a cached copy that has since been cleaned up
                            HypnoViewModel.RevertDeletedCacheFile(item);
                            item.Validate();
                            itemsToLoad.Add(item);
                        }
                    }
                }

                if (itemsToLoad.Count > 0)
                {
                    using (BeginBulkChange())
                    {
                        foreach (var item in itemsToLoad)
                            AddedFiles.Add(item);
                    }
                    SetStatusMessage($"Loaded {Videos(itemsToLoad.Count)} from the playlist", StatusMessageType.Success);
                    UpdateButtons();
                    SaveSession();
                }
                else
                {
                    SetStatusMessage("That playlist file has no videos EdgeLoop can play.", StatusMessageType.Warning);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to load playlist file", ex);
                SetStatusMessage($"Couldn't load the playlist: {ex.Message}", StatusMessageType.Error);
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>"1 video", "3 videos".</summary>
        private static string Videos(int count) => count == 1 ? "1 video" : $"{count} videos";

        private const string SupportedSitesNote = "Supported: Hypnotube, Iwara, PMVHaven, Rule34Video";

        private void AddUrlOrPlaylist(object obj)
        {
            _ = AddUrlOrPlaylistAsync().ContinueWith(task =>
            {
                if (task.IsFaulted)
                {
                    var ex = task.Exception?.GetBaseException() ?? task.Exception;
                    Logger.Error("Error in AddUrlOrPlaylist operation", ex);
                    Application.Current?.Dispatcher.InvokeAsync(() =>
                    {
                        SetStatusMessage($"Error adding URL or playlist: {ex?.Message ?? "Unknown error"}", StatusMessageType.Error);
                    });
                }
            }, TaskContinuationOptions.OnlyOnFaulted);
        }

        private async Task AddUrlOrPlaylistAsync()
        {
            try
            {
                var inputUrl = DialogService.Prompt("Add URL or playlist", "Video or playlist URL",
                    hint: "https://", note: SupportedSitesNote);
                if (string.IsNullOrWhiteSpace(inputUrl))
                    return;

                // Show the busy overlay only once there is work to do, not behind the input dialog
                IsLoading = true;
                var importToken = BeginImport();
                var trimmedUrl = inputUrl.Trim();

                // Validate URL
                if (!FileValidator.IsValidUrl(trimmedUrl))
                {
                    SetStatusMessage("That isn't a valid link.", StatusMessageType.Error);
                    IsLoading = false;
                    return;
                }

                // Handle Ambiguous URLs
                ImportMode importMode = PromptForAmbiguityIfRequired(trimmedUrl);
                if (importMode == (ImportMode)(-1))
                {
                    IsLoading = false;
                    return;
                }

                // First, try to import as a playlist
                ShowImportProgress("Checking if URL is a playlist...");

                var videoItems = await _playlistImporter.ImportPlaylistAsync(
                    trimmedUrl,
                    (c, t) =>
                    {
                        Application.Current?.Dispatcher.InvokeAsync(() =>
                            ShowImportProgress($"Importing playlist... {c} of {t} videos", c, t));
                    },
                    importToken,
                    importMode
                );

                // If playlist import returned videos, add them all
                if (videoItems != null && videoItems.Count > 0)
                {
                    // Ensure screens are up to date
                    if (AvailableScreens.Count == 0) RefreshScreens();
                    var defaultScreen = GetDefaultScreen();

                    // Check for duplicates and add items
                    var existingPaths = new HashSet<string>(AddedFiles.Select(x =>
                        x.IsUrl ? FileValidator.NormalizeUrl(x.FilePath) : x.FilePath),
                        StringComparer.OrdinalIgnoreCase);

                    var settings = App.Settings;
                    int addedCount = 0;
                    int skippedCount = 0;

                    using var bulk = BeginBulkChange();
                    foreach (var item in videoItems)
                    {
                        var normalizedUrl = item.IsUrl ? FileValidator.NormalizeUrl(item.FilePath) : item.FilePath;
                        if (existingPaths.Contains(normalizedUrl))
                        {
                            skippedCount++;
                            continue;
                        }

                        item.AssignedScreen = defaultScreen;
                        item.Opacity = settings.DefaultOpacity;
                        item.Volume = settings.DefaultVolume;
                        AddedFiles.Add(item);
                        existingPaths.Add(normalizedUrl);

                        addedCount++;
                    }

                    var added = skippedCount > 0
                        ? $"Added {Videos(addedCount)} from the playlist ({skippedCount} already in it)"
                        : $"Added {Videos(addedCount)} from the playlist";
                    // e.g. Iwara leaving videos out for a signed-out account
                    if (_playlistImporter.ImportNotice is string notice)
                        SetStatusMessage($"{added}. {notice}", StatusMessageType.Warning);
                    else
                        SetStatusMessage(added, StatusMessageType.Success);

                    UpdateButtons();
                    SaveSession();
                    IsLoading = false;
                    return;
                }

                // The site said why it showed nothing (e.g. Hypnotube blocks playlists in some locations,
                // Iwara hides explicit videos from signed-out accounts)
                if ((SiteRestrictions.Describe(trimmedUrl) ?? _playlistImporter.ImportNotice) is string blocked)
                {
                    SetStatusMessage($"Couldn't import that page. {blocked}", StatusMessageType.Error);
                    IsLoading = false;
                    return;
                }

                // If no videos found in playlist, treat as single video URL
                ShowImportProgress("No playlist found, treating as single video URL...");
                await ProcessUrlAsync(trimmedUrl, importToken);
            }
            catch (OperationCanceledException)
            {
                SetStatusMessage("Cancelled", StatusMessageType.Warning);
            }
            catch (Exception ex)
            {
                Logger.Error("Error in AddUrlOrPlaylistAsync", ex);
                SetStatusMessage($"Error adding URL or playlist: {ex.Message}", StatusMessageType.Error);
            }
            finally
            {
                EndImport();
            }
        }

        private async System.Threading.Tasks.Task AddFilesAsync(string[] filePaths, CancellationToken cancellationToken = default)
        {
            IsLoading = true;
            ImportProgressValue = 0;
            ImportProgressText = "Preparing import...";
            SetStatusMessage("Checking the files\u2026", StatusMessageType.Info);

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Ensure screens are up to date
                if (AvailableScreens.Count == 0) RefreshScreens();
                var defaultScreen = GetDefaultScreen();

                // Use HashSet for O(1) lookups instead of O(n) Any() checks
                var existingPaths = new HashSet<string>(AddedFiles.Select(x => x.FilePath), StringComparer.OrdinalIgnoreCase);

                // Validate files in parallel
                int processedCount = 0;
                int totalCount = filePaths.Length;
                var validationTasks = filePaths.Select(filePath => Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    (string filePath, bool isValid, string errorMessage) result;
                    if (existingPaths.Contains(filePath))
                    {
                        result = (filePath, isValid: false, errorMessage: "File already in playlist");
                    }
                    else if (!FileValidator.ValidateVideoFile(filePath, out string errorMessage))
                    {
                        result = (filePath, isValid: false, errorMessage);
                    }
                    else
                    {

                        // Check file size and warn if large (no limit enforced)
                        if (FileValidator.ValidateFileSize(filePath, out long size, out bool warning))
                        {
                            if (warning)
                            {
                                Logger.Debug($"Large file detected: {System.IO.Path.GetFileName(filePath)} ({size / (1024.0 * 1024 * 1024):F2} GB)");
                            }
                        }
                        result = (filePath, isValid: true, errorMessage: (string)null);
                    }

                    var current = Interlocked.Increment(ref processedCount);
                    Application.Current?.Dispatcher.InvokeAsync(() =>
                    {
                        ImportProgressValue = (double)current / totalCount * 100;
                        ImportProgressText = $"Validating {current} of {totalCount}...";
                    }, System.Windows.Threading.DispatcherPriority.Background);

                    return result;
                }, cancellationToken)).ToArray();

                cancellationToken.ThrowIfCancellationRequested();
                var results = await System.Threading.Tasks.Task.WhenAll(validationTasks);

                cancellationToken.ThrowIfCancellationRequested();
                var settings = App.Settings;
                int total = results.Length;
                int addedCount = 0;
                int skippedCount = 0;

                var failedFiles = new List<(string name, string error)>();

                using var bulk = BeginBulkChange();
                for (int i = 0; i < results.Length; i++)
                {
                    var (filePath, isValid, errorMessage) = results[i];

                    // Update progress
                    ImportProgressText = $"Importing {i + 1} of {total}...";
                    cancellationToken.ThrowIfCancellationRequested();
                    if (isValid)
                    {
                        var sanitizedPath = FileValidator.SanitizePath(filePath);
                        if (sanitizedPath != null)
                        {
                            var item = new VideoItem(sanitizedPath, defaultScreen);
                            item.Opacity = settings.DefaultOpacity;
                            item.Volume = settings.DefaultVolume;
                            // Validate the file to set its validation status
                            item.Validate();
                            AddedFiles.Add(item);
                            existingPaths.Add(sanitizedPath);

                            addedCount++;
                        }
                        else
                        {
                            Logger.Warning($"Failed to sanitize path: {filePath}");
                            failedFiles.Add((System.IO.Path.GetFileName(filePath), "Invalid file path"));
                            skippedCount++;
                        }
                    }
                    else
                    {
                        Logger.Warning($"Skipped file {filePath}: {errorMessage}");
                        failedFiles.Add((System.IO.Path.GetFileName(filePath), errorMessage));
                        skippedCount++;
                    }
                }

                if (skippedCount > 0)
                {
                    var message = $"Added {addedCount} file(s), skipped {skippedCount} invalid file(s)";
                    if (failedFiles.Count <= 5)
                    {
                        message += ":\n" + string.Join("\n", failedFiles.Select(f => $"• {f.name} - {f.error}"));
                    }
                    else
                    {
                        message += ":\n" + string.Join("\n", failedFiles.Take(5).Select(f => $"• {f.name} - {f.error}")) + $"\n... and {failedFiles.Count - 5} more";
                    }
                    SetStatusMessage(message, StatusMessageType.Warning);
                }
                else
                {
                    SetStatusMessage($"Added {Videos(addedCount)}", StatusMessageType.Success);
                }

                UpdateButtons();
                SaveSession();
            }
            catch (OperationCanceledException)
            {
                SetStatusMessage("Cancelled", StatusMessageType.Warning);
                Logger.Debug("Add files operation was cancelled");
            }
            catch (Exception ex)
            {
                Logger.Error("Error adding files", ex);
                SetStatusMessage($"Couldn't add the files: {ex.Message}", StatusMessageType.Error);
            }
            finally
            {
                IsLoading = false;
                ImportProgressValue = 0;
                ImportProgressText = string.Empty;
            }
        }

        private void ClearAll(object obj)
        {
            int count = AddedFiles.Count;
            if (count == 0) return;

            string videos = count == 1 ? "1 video" : $"{count} videos";
            if (!DialogService.Confirm("Clear playlist", $"Remove all {videos} from the playlist?", "Clear", "Cancel", destructive: true))
                return;

            RemoveItems(AddedFiles.ToList(), $"Cleared {videos}");
        }

        private void Exit(object obj)
        {
            if (DialogService.Confirm("Exit EdgeLoop", "Exit EdgeLoop? Playback stops on every screen.", "Exit", "Cancel"))
            {
                SaveSession(runInBackground: false);
                Application.Current.Shutdown();
            }
        }

        // Method to handle Drag & Drop from View
        public void AddDroppedFiles(string[] files)
        {
            // Capture dispatcher before starting the background task
            var dispatcher = Application.Current?.Dispatcher;

            // Safely execute async code from void method
            // This pattern ensures exceptions are properly caught and handled
            _ = AddDroppedFilesAsync(files).ContinueWith(task =>
            {
                if (task.IsFaulted)
                {
                    var ex = task.Exception?.GetBaseException() ?? task.Exception;
                    Logger.Error("Error in AddDroppedFiles operation", ex);
                    dispatcher?.InvokeAsync(() =>
                    {
                        SetStatusMessage($"Couldn't add the dropped files: {ex?.Message ?? "unknown error"}", StatusMessageType.Error);
                    });
                }
            }, TaskContinuationOptions.OnlyOnFaulted);
        }

        private async Task AddDroppedFilesAsync(string[] files)
        {
            IsLoading = true;
            ShowImportProgress("Scanning dropped items...");
            var importToken = BeginImport();

            var allVideoFiles = new List<string>();
            int folderCount = 0;

            try
            {
                await Task.Run(() =>
                {
                    foreach (var path in files)
                    {
                        importToken.ThrowIfCancellationRequested();
                        if (Directory.Exists(path))
                        {
                            folderCount++;
                            // Recursively get all video files from folder
                            try
                            {
                                var folderVideos = Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
                                    .Where(f => Constants.VideoExtensions.Contains(System.IO.Path.GetExtension(f)?.ToLowerInvariant()));

                                foreach (var video in folderVideos)
                                {
                                    allVideoFiles.Add(video);
                                }
                            }
                            catch (UnauthorizedAccessException ex)
                            {
                                Logger.Warning($"Access denied to folder: {path}", ex);
                            }
                            catch (Exception ex)
                            {
                                Logger.Error($"Error scanning folder: {path}", ex);
                            }
                        }
                        else
                        {
                            var ext = System.IO.Path.GetExtension(path)?.ToLowerInvariant();
                            if (Constants.VideoExtensions.Contains(ext))
                            {
                                allVideoFiles.Add(path);
                            }
                        }
                    }
                }, importToken);
            }
            catch (OperationCanceledException)
            {
                SetStatusMessage("Cancelled", StatusMessageType.Warning);
                EndImport();
                return;
            }

            if (allVideoFiles.Count > 0)
            {
                if (folderCount > 0)
                {
                    SetStatusMessage($"Found {Videos(allVideoFiles.Count)} in {folderCount} {(folderCount == 1 ? "folder" : "folders")}. Checking them\u2026", StatusMessageType.Info);
                }
                await AddFilesAsync(allVideoFiles.ToArray(), importToken);
            }
            else
            {
                SetStatusMessage("None of the dropped items are videos EdgeLoop can play.", StatusMessageType.Warning);
                EndImport();
            }
        }

        public void MoveVideoItem(VideoItem item, int newIndex)
        {
            if (item == null) return;
            var oldIndex = AddedFiles.IndexOf(item);
            if (oldIndex < 0 || newIndex < 0 || newIndex >= AddedFiles.Count) return;

            AddedFiles.Move(oldIndex, newIndex);
            SaveSession();
        }

        public void AddDroppedUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;

            var potentialUrl = url.Trim();
            if (!potentialUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !potentialUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                !Path.IsPathRooted(potentialUrl) && potentialUrl.Contains("."))
            {
                potentialUrl = "https://" + potentialUrl;
            }

            // Validate URL
            if (!Uri.TryCreate(potentialUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                SetStatusMessage("That dropped text isn't a valid link.", StatusMessageType.Warning);
                return;
            }

            _ = ProcessDroppedUrlAsync(potentialUrl);
        }

        private async Task ProcessDroppedUrlAsync(string url)
        {
            IsLoading = true;
            try
            {
                await ProcessUrlAsync(url, BeginImport());
            }
            finally
            {
                EndImport();
            }
        }




        public void SaveSession(bool runInBackground = true)
        {
            try
            {
                // Only save session if user wants to remember playlist
                if (App.Settings == null || !App.Settings.RememberLastPlaylist)
                {
                    return;
                }

                // Take a snapshot of the playlist items to avoid cross-thread issues
                var playlistItems = AddedFiles.Select(item => new PlaylistItem
                {
                    FilePath = item.FilePath,
                    ScreenDeviceName = item.AssignedScreen?.DeviceName,
                    Opacity = item.Opacity,
                    Volume = item.Volume,
                    Title = item.Title,
                    OriginalPageUrl = item.OriginalPageUrl, // Store for re-extraction when URLs expire
                    DurationSeconds = item.Duration?.TotalSeconds
                }).ToList();

                lock (_pendingSessionLock)
                {
                    _pendingSession = playlistItems;
                }

                if (runInBackground)
                {
                    System.Threading.Tasks.Task.Run(() => WritePendingSession(waitForOtherSave: false));
                }
                else
                {
                    WritePendingSession(waitForOtherSave: true);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to create session snapshot", ex);
            }
        }

        /// <summary>
        /// Writes the latest playlist snapshot. When another save is running, a background save
        /// leaves the snapshot for it: that save writes whatever is newest before it finishes, so
        /// no change is lost. On exit (<paramref name="waitForOtherSave"/>), waits for it instead.
        /// </summary>
        private void WritePendingSession(bool waitForOtherSave)
        {
            while (true)
            {
                bool acquired = waitForOtherSave ? _saveSemaphore.Wait(TimeSpan.FromSeconds(10)) : _saveSemaphore.Wait(0);
                if (!acquired)
                {
                    if (waitForOtherSave) Logger.Warning("SaveSession: Another save didn't finish in time; the playlist may not include the last change");
                    return;
                }

                try
                {
                    while (true)
                    {
                        List<PlaylistItem> items;
                        lock (_pendingSessionLock)
                        {
                            items = _pendingSession;
                            _pendingSession = null;
                        }
                        if (items == null) break;

                        try
                        {
                            var json = System.Text.Json.JsonSerializer.Serialize(new Playlist { Items = items });
                            AtomicFile.WriteAllText(AppPaths.SessionFile, json);
                        }
                        catch (Exception ex)
                        {
                            Logger.Error("Failed to save session", ex);
                        }
                    }
                }
                finally
                {
                    _saveSemaphore.Release();
                }

                // A snapshot that arrived after the last write, while its own save couldn't get in
                lock (_pendingSessionLock)
                {
                    if (_pendingSession == null) return;
                }
            }
        }

        private async Task InitializeAsync(CancellationToken ct)
        {
            // 1. Load the session first: the daily yt-dlp update below downloads a new copy of
            // yt-dlp, and the saved playlist shouldn't wait for that
            await LoadSessionAsync(ct);

            // 2. Run update check for yt-dlp
            try
            {
                if (ServiceContainer.TryGet<YtDlpService>(out var ytDlp))
                {
                    if (!ytDlp.IsAvailable)
                    {
                        SetStatusMessage("yt-dlp is missing, so some sites play at lower quality. Check the Dependencies folder next to EdgeLoop.exe.", StatusMessageType.Warning);
                    }
                    else
                    {
                        await ytDlp.CheckForUpdateAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning("yt-dlp update check failed", ex);
            }
        }

        private async Task InitializeAsyncWithoutSession(CancellationToken ct)
        {
            // Just run update check
            try
            {
                if (ServiceContainer.TryGet<YtDlpService>(out var ytDlp))
                {
                    if (!ytDlp.IsAvailable)
                    {
                        SetStatusMessage("yt-dlp is missing, so some sites play at lower quality. Check the Dependencies folder next to EdgeLoop.exe.", StatusMessageType.Warning);
                    }
                    else
                    {
                        await ytDlp.CheckForUpdateAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning("yt-dlp update check failed", ex);
            }
        }

        private void CancelImport(object obj)
        {
            _importCts?.Cancel();
            IsLoading = false;
        }

        /// <summary>
        /// Starts a new import that the busy overlay's Cancel button can stop, cancelling any earlier one.
        /// Linked to the view model's lifetime token so closing the app still cancels it.
        /// </summary>
        private CancellationToken BeginImport()
        {
            _importCts?.Cancel();
            _importCts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token);
            return _importCts.Token;
        }

        /// <summary>Shows progress in the busy overlay and the status bar.</summary>
        private void ShowImportProgress(string text, int current = 0, int total = 0)
        {
            ImportProgressText = text;
            ImportProgressValue = total > 0 ? Math.Min(100, current * 100.0 / total) : 0;
            SetStatusMessage(text, StatusMessageType.Info);
        }

        private void EndImport()
        {
            IsLoading = false;
            ImportProgressValue = 0;
            ImportProgressText = string.Empty;
        }

        private async Task LoadSessionAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var path = AppPaths.SessionFile;
                if (!System.IO.File.Exists(path)) return;

                // Read file asynchronously to avoid blocking UI thread
                var json = await System.IO.File.ReadAllTextAsync(path, cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();

                var playlist = System.Text.Json.JsonSerializer.Deserialize<Playlist>(json);

                if (playlist != null)
                {
                    // Dispatch UI updates back to UI thread
                    await Application.Current.Dispatcher.InvokeAsync(async () =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        AddedFiles.Clear();
                        if (AvailableScreens.Count == 0) RefreshScreens();

                        using var bulk = BeginBulkChange();
                        foreach (var item in playlist.Items)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            var screen = AvailableScreens.FirstOrDefault(s => s.DeviceName == item.ScreenDeviceName) ?? AvailableScreens.FirstOrDefault(s => s.Screen.Primary) ?? AvailableScreens.FirstOrDefault();
                            var videoItem = new VideoItem(item.FilePath, screen);
                            videoItem.Opacity = item.Opacity;
                            videoItem.Volume = item.Volume;
                            videoItem.OriginalPageUrl = item.OriginalPageUrl; // Restore for re-extraction

                            // A web video saved while it played from a cached copy that has since been
                            // cleaned up: use its page again instead of showing it as missing
                            if (HypnoViewModel.RevertDeletedCacheFile(videoItem))
                            {
                                Logger.Debug($"LoadSession: Cached copy of '{videoItem.FileName}' is gone, using its page again");
                            }

                            // Set title if available (backward compatible - Title may be null for old sessions)
                            if (!string.IsNullOrWhiteSpace(item.Title))
                            {
                                videoItem.Title = item.Title;
                            }

                            // CRITICAL: Re-extract URLs from time-sensitive sites (Rule34Video, Hypnotube)
                            // OPTIMIZATION: Use PersistentUrlCache or JIT resolution avoids startup delay.
                            if (videoItem.IsUrl && NeedsReExtraction(item.FilePath))
                            {
                                try
                                {
                                    var pageUrl = !string.IsNullOrEmpty(item.OriginalPageUrl)
                                        ? item.OriginalPageUrl
                                        : GetOriginalPageUrl(item.FilePath);

                                    if (!string.IsNullOrEmpty(pageUrl))
                                    {
                                        // Try to get from persistent cache first (Instant)
                                        var cachedUrl = PersistentUrlCache.Instance.Get(pageUrl);

                                        if (!string.IsNullOrEmpty(cachedUrl))
                                        {
                                            Logger.Debug($"LoadSession: Found valid cached URL for '{videoItem.FileName}'");
                                            videoItem = new VideoItem(cachedUrl, screen);
                                            videoItem.Opacity = item.Opacity;
                                            videoItem.Volume = item.Volume;
                                            videoItem.Title = item.Title;
                                            videoItem.OriginalPageUrl = pageUrl;
                                        }
                                        else
                                        {
                                            // JIT Strategy: Set FilePath to the Page URL.
                                            // HypnoViewModel.LoadCurrentVideo will detect it's a Page URL and resolve it when played.
                                            Logger.Debug($"LoadSession: Deferring resolution for '{videoItem.FileName}' to Playback (JIT)");
                                            videoItem = new VideoItem(pageUrl, screen);
                                            videoItem.Opacity = item.Opacity;
                                            videoItem.Volume = item.Volume;
                                            videoItem.Title = item.Title;
                                            videoItem.OriginalPageUrl = pageUrl;
                                        }
                                    }
                                    else
                                    {
                                        Logger.Warning($"LoadSession: Could not determine original page URL for '{videoItem.FileName}', keeping generic/expired URL.");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Logger.Warning($"LoadSession: JIT/Cache setup error: {ex.Message}, using saved URL");
                                }
                            }

                            // Validate the file when loading from session
                            videoItem.Validate();
                            videoItem.Duration ??= item.Duration;
                            AddedFiles.Add(videoItem);
                        }
                        UpdateButtons();
                    }, System.Windows.Threading.DispatcherPriority.Normal, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                Logger.Debug("Load session operation was cancelled");
            }
            catch (Exception ex)
            {
                Logger.Warning("Failed to load session", ex);
            }
        }

        /// <summary>
        /// Determines if a URL is from a time-sensitive site that needs re-extraction
        /// </summary>
        private bool NeedsReExtraction(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;

            // Rule34Video URLs expire quickly
            if (url.Contains("rule34video.com/get_file/") || url.Contains("rule34video.com/video/"))
            {
                return true;
            }

            // Hypnotube URLs may also be time-limited
            if (SiteRules.Is(url, Site.Hypnotube) && !url.EndsWith(".m3u8"))
            {
                return true;
            }

            if (SiteRules.Is(url, Site.Iwara))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Attempts to extract the original page URL from a direct video URL
        /// </summary>
        private string GetOriginalPageUrl(string videoUrl)
        {
            if (string.IsNullOrEmpty(videoUrl)) return null;

            try
            {
                // For Rule34Video, extract video ID from the URL
                // Example: https://rule34video.com/get_file/.../4187000/4187379/4187379_360.mp4
                // Should return: https://rule34video.com/video/4187379/
                if (SiteRules.Is(videoUrl, Site.Rule34Video))
                {
                    var match = System.Text.RegularExpressions.Regex.Match(videoUrl, @"/(\d{7})/(\d+)/");
                    if (match.Success && match.Groups.Count > 2)
                    {
                        var videoId = match.Groups[2].Value;
                        // We don't have the slug, but the video ID alone should work for re-extraction
                        return $"https://rule34video.com/video/{videoId}/";
                    }
                }

                // For Hypnotube, similar pattern
                // Example: https://cdn.hypnotube.com/videos/12345.mp4
                // This is harder without the original page URL, so we'll skip for now

            }
            catch (Exception ex)
            {
                Logger.Warning($"GetOriginalPageUrl: Error parsing URL: {ex.Message}");
            }

            return null;
        }

        private bool _disposed = false;

        /// <summary>
        /// Marks the playlist items matching a failed video (same item, file or page URL) as failed and
        /// returns them. They stay in the playlist: a failure can be temporary, and playback retries them next time.
        /// </summary>
        internal static List<VideoItem> MarkFailed(IEnumerable<VideoItem> playlist, VideoItem failed, string reason)
        {
            var path = failed.FilePath;
            var originalUrl = failed.OriginalPageUrl;
            var matches = playlist.Where(x =>
                ReferenceEquals(x, failed) ||
                string.Equals(x.FilePath, path, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(originalUrl) && string.Equals(x.OriginalPageUrl, originalUrl, StringComparison.OrdinalIgnoreCase))
            ).ToList();


            foreach (var item in matches)
                item.PlaybackError = string.IsNullOrWhiteSpace(reason) ? "Playback failed" : reason;

            return matches;
        }

        private void VideoService_MediaErrorOccurred(object sender, MediaErrorEventArgs e)
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                Logger.Debug($"[Launcher] Media error received: {e.ErrorMessage} (Item: {e.Item?.FileName ?? "null"})");
                SetStatusMessage(e.ErrorMessage, StatusMessageType.Error);

                if (e.Item != null)
                {
                    var failingItems = MarkFailed(AddedFiles, e.Item, e.ErrorMessage);
                    if (failingItems.Count > 0)
                    {
                        // The items stay in the playlist; just stop the running players from trying them again this session
                        Logger.Debug($"[Launcher] Marked {failingItems.Count} playlist item(s) as failed.");
                        App.VideoService?.RemoveItemsFromAllPlayers(failingItems);
                    }
                    else
                    {
                        Logger.Warning($"[Launcher] Media error for '{e.Item.FileName}' but no matching playlist item was found. Path: {e.Item.FilePath}");
                    }
                }
            });
        }

        private void VideoService_MediaOpened(object sender, EventArgs e)
        {
            // REQUEST: "keep showing the error till user presses the button another time."
            // We no longer automatically clear the error when a DIFFERENT video succeeds.
            // The status bar will only be cleared when the user clicks "Start Gooning" again.

            /* Old behavior:
            Application.Current?.Dispatcher.InvokeAsync(() => {
                if (!string.IsNullOrEmpty(StatusMessage)) {
                    SetStatusMessage(null);
                }
            });
            */
        }

        /// <summary>
        /// Disposes resources and unsubscribes from events
        /// </summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                _cancellationTokenSource?.Cancel();
                _cancellationTokenSource?.Dispose();
                SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
                App.VideoService.MediaErrorOccurred -= VideoService_MediaErrorOccurred;
                App.VideoService.MediaOpened -= VideoService_MediaOpened;
                _saveTimer?.Stop();
                _disposed = true;
            }
        }
        private ImportMode PromptForAmbiguityIfRequired(string url)
        {
            var ambiguity = PlaylistImporter.DetectUrlAmbiguity(url);
            if (ambiguity.IsAmbiguous)
            {
                var choice = DialogService.Choose(
                    "Video or playlist?",
                    $"This {ambiguity.SiteName} link contains both a video and a playlist. What do you want to import?",
                    "Only video",
                    "Playlist"
                );

                if (choice == 1) return ImportMode.SingleVideo;
                if (choice == 2) return ImportMode.Collection;
                return (ImportMode)(-1); // Cancelled
            }
            return ImportMode.Auto;
        }
    }
}



