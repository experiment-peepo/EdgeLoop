using System;
using System.Windows;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls;
using EdgeLoop.Classes;
using FlyleafLib;
using FlyleafLib.MediaPlayer;
using Logger = EdgeLoop.Classes.Logger;

namespace EdgeLoop.ViewModels
{
    public class HypnoViewModel : ObservableObject, IDisposable
    {
        private VideoItem[] _files;
        private int _currentPos = 0;
        private int _consecutiveFailures = 0;
        private const int MaxConsecutiveFailures = 10; // Stop retrying after 10 consecutive failures
        private ConcurrentDictionary<string, int> _fileFailureCounts = new ConcurrentDictionary<string, int>(); // Track failures per file (thread-safe)
        private const int MaxFailuresPerFile = 3; // Skip a file after 3 failures
        private bool _isLoading = false; // Prevent concurrent LoadCurrentVideo() calls

        private string _loadingProgressText = string.Empty;
        public string LoadingProgressText
        {
            get => _loadingProgressText;
            set => SetProperty(ref _loadingProgressText, value);
        }

        public bool IsLoadingStatus
        {
            get => _isLoading;
            set => SetProperty(ref _isLoading, value);
        }
        private bool _manualPauseRequested = false; // Track if user requested pause while Opening
        private readonly object _loadLock = new object(); // Lock for loading operations
        private Uri _expectedSource = null; // Track the source we're expecting MediaOpened for
        private int _recursionDepth = 0; // Track recursion depth to prevent stack overflow
        private const int MaxRecursionDepth = 50; // Maximum recursion depth before aborting
        private CancellationTokenSource _loadCts; // Added to cancel ongoing URL extraction on Skip
        private static readonly Random _shuffleRandom = new Random(); // Shared Random for better randomness
        private int[] _shuffledIndices;
        private int _shuffledIndexPointer = -1;

        // Pre-buffering for instant playback
        private readonly IVideoDownloadService _downloadService;
        private CancellationTokenSource _preBufferCts = null;
        private string _preBufferedUrl = null; // The URL that was pre-buffered
        private string _preBufferedPath = null; // The local cache path for pre-buffered video
        private static bool _hasShownCachingWarning = false; // Track if we've warned the user about disabled caching

        public Config Config { get; private set; }
        public Player Player { get; private set; }

        private (TimeSpan position, long timestamp) _lastPositionRecord;
        private DateTime _lastSaveTime = DateTime.MinValue;
        public (TimeSpan position, long timestamp) LastPositionRecord
        {
            get => _lastPositionRecord;
            set => SetProperty(ref _lastPositionRecord, value);
        }

        public string MonitorName { get; set; } = "Unknown";

        public VideoItem CurrentItem
        {
            get => _currentItem;
            protected set
            {
                if (_currentItem != value)
                {
                    if (_currentItem != null) _currentItem.PropertyChanged -= CurrentItem_PropertyChanged;
                    SetProperty(ref _currentItem, value);
                    if (_currentItem != null) _currentItem.PropertyChanged += CurrentItem_PropertyChanged;
                }
            }
        }

        private Uri _currentSource;
        public Uri CurrentSource
        {
            get => _currentSource;
            set
            {
                SetProperty(ref _currentSource, value);
            }
        }

        private double _opacity;
        public virtual double Opacity
        {
            get => _opacity;
            set
            {
                if (SetProperty(ref _opacity, value))
                {
                    if (_currentItem != null && _currentItem.Opacity != value)
                    {
                        // Only push back to the shared item if we are NOT a synced follower
                        // This prevents followers from poisoning the master's opacity in group mode
                        if (string.IsNullOrEmpty(SyncGroupId) || IsSyncMaster)
                        {
                            _currentItem.Opacity = value;
                        }
                    }
                    OnPropertyChanged(nameof(EffectiveOpacity));
                    Logger.Debug($"[HypnoViewModel] Opacity changed: {value} (Effective: {EffectiveOpacity})");
                }
            }
        }

        public double EffectiveOpacity => (App.Settings != null && App.Settings.AlwaysOpaque) ? 1.0 : _opacity;

        public void RefreshOpacity()
        {
            OnPropertyChanged(nameof(Opacity));
            OnPropertyChanged(nameof(EffectiveOpacity));
        }

        private double _volume;
        public virtual double Volume
        {
            get => _volume;
            set
            {
                if (SetProperty(ref _volume, value))
                {
                    if (Player?.Audio != null) Player.Audio.Volume = (int)(value * 100); // Flyleaf uses 0-100
                    if (_currentItem != null && _currentItem.Volume != value)
                    {
                        // Only push back to the shared item if we are NOT a synced follower
                        // This prevents followers from muting the master in group mode
                        if (string.IsNullOrEmpty(SyncGroupId) || IsSyncMaster)
                        {
                            _currentItem.Volume = value;
                        }
                    }
                    OnPropertyChanged(nameof(ActualVolume));
                }
            }
        }

        // MPV-style quadratic volume scaling
        // 100% UI volume (1.0) = 1.0^2 = 1.0 actual volume
        // Quadratic is better balanced: 50% slider = 25% power (vs 12.5% in cubic)
        public double ActualVolume => Math.Pow(_volume, 2);

        private double _speedRatio = 1.0;
        public virtual double SpeedRatio
        {
            get => _speedRatio;
            set
            {
                if (SetProperty(ref _speedRatio, value))
                {
                    if (Player != null) Player.Speed = value;
                }
            }
        }

        private MediaState _mediaState = MediaState.Manual;
        public MediaState MediaState
        {
            get => _mediaState;
            set => SetProperty(ref _mediaState, value);
        }

        private bool _isReady = false;
        public bool IsReady
        {
            get => _isReady;
            private set => SetProperty(ref _isReady, value);
        }

        public bool IsLoading
        {
            get
            {
                lock (_loadLock)
                {
                    return _isLoading;
                }
            }
        }

        public bool UseCoordinatedStart { get; set; } = false;
        public string SyncGroupId { get; set; } = null;
        public bool IsSyncMaster { get; set; } = false;

        public IClock ExternalClock
        {
            get => Player?.ExternalClock;
            set
            {
                if (Player != null)
                {
                    Player.ExternalClock = value;
                    if (value != null)
                    {
                        Player.Config.Player.MasterClock = FlyleafLib.MediaPlayer.MasterClock.External;
                    }
                }
            }
        }

        public long SyncTolerance
        {
            get => Player?.Config.Player.SyncTolerance ?? 160000;
            set
            {
                if (Player != null)
                {
                    Player.Config.Player.SyncTolerance = value;
                    OnPropertyChanged(nameof(SyncTolerance));
                }
            }
        }

        private DateTime _lastSeekTime = DateTime.MinValue;
        private VideoPlayRecord _currentPlayRecord;
        private string _playlistHash;
        private Guid _sessionId = Guid.NewGuid();

        public int ClockDriftMs => Player?.Video.ClockDriftMs ?? 0;
        public double D3DImageLatencyMs => Player?.Video.D3DImageLatencyMs ?? 0;

        private string _resolution = "Unknown";
        public string Resolution
        {
            get => _resolution;
            private set => SetProperty(ref _resolution, value);
        }

        private System.Windows.Threading.DispatcherTimer _resolutionTimer;

        private void UpdateResolution()
        {
            if (_disposed || Player == null) return;
            try
            {
                if (Player.Video != null)
                {
                    var w = Player.Video.Width;
                    var h = Player.Video.Height;

                    if (w > 0 && h > 0)
                    {
                        var newRes = $"{w}x{h}";
                        if (Resolution != newRes)
                        {
                            Resolution = newRes;
                            Logger.Debug($"[HypnoViewModel] Resolution updated on {MonitorName}: {newRes}");

                            // Once found, we can slow down or stop the timer if not needed
                            if (_resolutionTimer != null && _resolutionTimer.IsEnabled)
                            {
                                _resolutionTimer.Stop();
                            }
                        }
                    }
                    else
                    {
                        // Log why it's failing if we're in a polling state
                        if (Resolution == "Unknown")
                        {
                            // Logger.Debug($"[HypnoViewModel] Resolution poll: Video object exists but dimensions are {w}x{h}");
                        }
                    }
                }
                else
                {
                    if (Resolution == "Unknown")
                    {
                        // Logger.Debug($"[HypnoViewModel] Resolution poll: Video object is null");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[HypnoViewModel] Error in UpdateResolution: {ex.Message}");
            }
        }

        private void StartResolutionPolling()
        {
            if (_resolutionTimer == null)
            {
                _resolutionTimer = new System.Windows.Threading.DispatcherTimer();
                _resolutionTimer.Interval = TimeSpan.FromMilliseconds(500);
                _resolutionTimer.Tick += (s, e) => UpdateResolution();
            }

            if (!_resolutionTimer.IsEnabled)
            {
                Logger.Debug($"[HypnoViewModel] Starting resolution polling on {MonitorName}");
                _resolutionTimer.Start();
            }
        }

        public bool IsShuffle
        {
            get => App.Settings?.VideoShuffle ?? true;
            set
            {
                if (App.Settings != null)
                {
                    App.Settings.VideoShuffle = value;
                    OnPropertyChanged(nameof(IsShuffle));
                    App.Settings.Save(); // Save immediately when toggled
                }
            }
        }

        public event EventHandler RequestPlay;
        public event EventHandler RequestPause;
        public event EventHandler RequestStop;
        public event EventHandler RequestStopBeforeSourceChange;
        public event EventHandler<MediaErrorEventArgs> MediaErrorOccurred;
        public event EventHandler MediaOpened;
        public event EventHandler<TimeSpan> RequestSyncPosition;
        public event EventHandler RequestReady;

        public ICommand SkipCommand { get; }
        public ICommand PreviousCommand { get; }
        public ICommand TogglePlayPauseCommand { get; }

        /// <summary>
        /// Event raised when the entire queue has failed and playback must stop
        /// </summary>
        public event EventHandler TerminalFailure;
        public event EventHandler<MediaErrorEventArgs> MediaFailed;

        public void RefreshSuperResolution()
        {
            if (_disposed || Player == null) return;
            Player.Config.Video.SuperResolution = App.Settings?.EnableSuperResolution ?? false;
        }

        private readonly IVideoUrlExtractor _urlExtractor;

        public HypnoViewModel(IVideoUrlExtractor urlExtractor = null, IVideoDownloadService downloadService = null)
        {
            _urlExtractor = urlExtractor ?? (ServiceContainer.TryGet<IVideoUrlExtractor>(out var extractor) ? extractor : null) ?? App.UrlExtractor;
            _downloadService = downloadService ?? (ServiceContainer.TryGet<IVideoDownloadService>(out var ds) ? ds : null) ?? new VideoDownloadService();
            _opacity = 0.9; // Safe default to ensure window is visible during initial load
            Config = new Config();

            Config.Player.AutoPlay = false;
            Config.Player.MasterClock = MasterClock.Video;
            Config.Player.Stats = true; // Enable telemetry stats
            Config.Player.SeekAccurate = true; // Ensure precise resumption to the exact tick

            // Increase buffering to prevent stutters on high-resolution/high-bitrate videos
            // 1 second = 10,000,000 ticks (100ns units)
            // Tuned for responsiveness: 5s total buffer to handle 4K 100Mbps peaks.
            Config.Demuxer.BufferDuration = 50000000;

            // Inject AI Super Resolution setting
            Config.Video.SuperResolution = App.Settings?.EnableSuperResolution ?? false;
            // Config.Video.ClearImage = true; // Property validation failed, using default ClearScreen=true

            // HLS / Streaming Optimizations for Speed & Stability
            // HLS / Streaming Optimizations for Speed & Stability
            // Set FFmpeg options via dictionary (properties do not exist on class)
            if (Config.Demuxer.FormatOpt == null) Config.Demuxer.FormatOpt = new Dictionary<string, string>();

            Config.Demuxer.FormatOpt["analyzeduration"] = "5000000"; // 5s for more reliable 4K/HLS probing
            Config.Demuxer.FormatOpt["probesize"] = "10000000";      // 10MB for 4K headers
            Config.Demuxer.FormatOpt["discardcorrupt"] = "1";       // Prevent stalling on minor stream errors
            Config.Demuxer.FormatOpt["reconnect"] = "1";
            Config.Demuxer.FormatOpt["reconnect_streamed"] = "1";
            Config.Demuxer.FormatOpt["reconnect_delay_max"] = "5";
            Config.Demuxer.FormatOpt["seg_max_retry"] = "10";
            Config.Demuxer.FormatOpt["http_persistent"] = "0"; // Disabled for HLS stability (PMVHaven CDN rotates endpoints between segments)

            // Re-enabling Video Acceleration as requested
            Config.Video.VideoAcceleration = true;

            // Fix for "Audio Only" / Black Screen with HW Acceleration ON:
            // 1. Allow decoding even if profile doesn't perfectly match capabilities (bypasses some strict checks)
            Config.Decoder.AllowProfileMismatch = true;
            // 2. Use D3D11 Video Processor directly (often more stable for HLS/Web streams than internal VP)
            Config.Video.VideoProcessor = VideoProcessors.D3D11;

            Player = new Player(Config);

            // Map Flyleaf events (Named handlers for safe unsubscription)
            Player.OpenCompleted += Player_OpenCompleted;
            Player.PropertyChanged += Player_PropertyChanged;

            SkipCommand = new RelayCommand(_ => PlayNext(true));
            PreviousCommand = new RelayCommand(_ => PlayPrevious(true));
            TogglePlayPauseCommand = new RelayCommand(_ => TogglePlayPause());
        }

        private int _toggleClickCount = 0;

        public virtual void TogglePlayPause()
        {
            _toggleClickCount++;
            int clickId = _toggleClickCount;

            if (_disposed || Player == null)
            {
                Logger.Warning($"[TogglePlayPause #{clickId}] Ignored - disposed or no player");
                return;
            }

            var status = Player.Status;
            bool canPlay = Player.CanPlay;

            Logger.Debug($"[TogglePlayPause #{clickId}] Status: {status}, CanPlay: {canPlay}, Loading: {_isLoading}, ManualPause: {_manualPauseRequested}, UI: {MediaState}");

            // CASE 1: Engine is not ready yet (still initializing streams)
            if (!canPlay)
            {
                Logger.Debug($"[TogglePlayPause #{clickId}] CanPlay=false. Deferring pause request.");
                _manualPauseRequested = true;
                _mediaState = System.Windows.Controls.MediaState.Pause;
                OnPropertyChanged(nameof(MediaState));
                return;
            }

            // CASE 2: Video has ended. Pressing play should restart.
            if (status == FlyleafLib.MediaPlayer.Status.Ended)
            {
                Logger.Debug($"[TogglePlayPause #{clickId}] Status=Ended. Seeking to start.");
                Player.Seek(0);
                Play();
                _manualPauseRequested = false;
                return;
            }

            // CASE 3: Use Flyleaf's native toggle for most reliable behavior
            // This bypasses our complex state tracking and lets the engine decide
            var statusBefore = Player.Status;
            Player.TogglePlayPause();
            var statusAfter = Player.Status;

            Logger.Debug($"[TogglePlayPause #{clickId}] Native toggle: {statusBefore} -> {statusAfter}");

            // Update our UI state to match what the engine did
            if (statusAfter == FlyleafLib.MediaPlayer.Status.Playing)
            {
                _mediaState = System.Windows.Controls.MediaState.Play;
                _manualPauseRequested = false;
            }
            else
            {
                _mediaState = System.Windows.Controls.MediaState.Pause;
                // Set manual pause if we're still loading to prevent auto-play in OnMediaOpened
                if (_isLoading || status == FlyleafLib.MediaPlayer.Status.Opening)
                {
                    _manualPauseRequested = true;
                }
            }
            OnPropertyChanged(nameof(MediaState));
        }

        public int CurrentIndex => _currentPos;

        public void SetQueue(IEnumerable<VideoItem> files, int startIndex = -1)
        {
            if (_disposed) return;
            // Unsubscribe from current item's PropertyChanged event to prevent memory leaks
            if (CurrentItem != null)
            {
                CurrentItem.PropertyChanged -= CurrentItem_PropertyChanged;
            }

            lock (_loadLock)
            {
                // Reset current item (event handler unsubscription handled by property setter)
                CurrentItem = null;
                // Reset loading state when queue changes
                IsLoadingStatus = false;
                // Reset recursion depth when queue changes
                _recursionDepth = 0;
            }

            _files = files?.ToArray() ?? Array.Empty<VideoItem>();
            _currentPos = -1;
            _manualPauseRequested = false; // Reset pause intent for new queue

            UpdatePlaylistHash();

            Logger.Debug($"Queue updated with {_files.Length} videos");
            foreach (var f in _files)
            {
                Logger.Debug($"  - {f.FileName} ({(f.IsUrl ? "URL" : "Local")})");
            }

            _fileFailureCounts.Clear();
            _consecutiveFailures = 0;

            // Reset shuffle when queue changes
            _shuffledIndices = null;
            _shuffledIndexPointer = -1;

            // Cancel any pending pre-buffer when queue changes
            _preBufferCts?.Cancel();
            _preBufferedUrl = null;
            _preBufferedPath = null;

            // Start playing the new queue
            if (startIndex >= 0 && startIndex < _files.Length)
            {
                if (IsShuffle)
                {
                    InitializeShuffle(startIndex);
                    _shuffledIndexPointer = 0; // The first index is already being played by JumpToIndex
                }
                JumpToIndex(startIndex);
            }
            else
            {
                PlayNext();
            }
        }

        public virtual void RemoveItems(IEnumerable<VideoItem> itemsToRemove)
        {
            if (_disposed || _files == null || _files.Length == 0) return;

            // Build a set of tracking paths for fast lookup
            var pathsToRemove = new HashSet<string>(
                itemsToRemove.Select(x => x.TrackingPath),
                StringComparer.OrdinalIgnoreCase
            );

            if (pathsToRemove.Count == 0) return;

            var newFilesList = new List<VideoItem>();
            int newCurrentPos = -1;
            bool currentItemRemoved = false;
            VideoItem currentItemRef = CurrentItem;

            for (int i = 0; i < _files.Length; i++)
            {
                var item = _files[i];
                if (pathsToRemove.Contains(item.TrackingPath))
                {
                    if (i == _currentPos || item == currentItemRef) currentItemRemoved = true;
                    continue;
                }

                if (i == _currentPos || (currentItemRef != null && item == currentItemRef))
                {
                    newCurrentPos = newFilesList.Count;
                }
                newFilesList.Add(item);
            }

            lock (_loadLock)
            {
                _files = newFilesList.ToArray();
                _currentPos = newCurrentPos;
                UpdatePlaylistHash();

                // Invalidate shuffle bag as indices have changed
                _shuffledIndices = null;
                _shuffledIndexPointer = -1;
            }

            if (_files.Length == 0)
            {
                Logger.Warning($"No videos remaining in queue for {MonitorName} after removal. Triggering terminal failure.");
                Stop();
                TerminalFailure?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (currentItemRemoved)
            {
                Logger.Debug($"Current video removed from queue on {MonitorName}. Skipping to next.");
                CurrentItem = null; // Clear highlight/current item immediately
                PlayNext(true);
            }
        }

        private void UpdatePlaylistHash()
        {
            if (_files == null || _files.Length == 0)
            {
                _playlistHash = "EMPTY";
                return;
            }

            try
            {
                // Simple hash of file paths
                var paths = string.Join("|", _files.Select(f => f.FilePath).OrderBy(p => p));
                using (var md5 = System.Security.Cryptography.MD5.Create())
                {
                    var hashBytes = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(paths));
                    _playlistHash = BitConverter.ToString(hashBytes).Replace("-", "").Substring(0, 8);
                }
            }
            catch
            {
                _playlistHash = "ERROR";
            }
        }

        private void InitializeShuffle(int firstIndex = -1)
        {
            if (_files == null || _files.Length == 0) return;

            Logger.Debug($"[Shuffle] Initializing new shuffle bag for {_files.Length} videos on {MonitorName} (FirstIndex={firstIndex})");

            int lastIndex = -1;
            if (firstIndex != -1)
            {
                lastIndex = firstIndex;
            }
            else if (_shuffledIndices != null && _shuffledIndexPointer >= 0 && _shuffledIndexPointer < _shuffledIndices.Length)
            {
                lastIndex = _shuffledIndices[_shuffledIndexPointer];
            }

            _shuffledIndices = Enumerable.Range(0, _files.Length).ToArray();

            // Fisher-Yates shuffle
            for (int i = _shuffledIndices.Length - 1; i > 0; i--)
            {
                int j = _shuffleRandom.Next(i + 1);
                int temp = _shuffledIndices[i];
                _shuffledIndices[i] = _shuffledIndices[j];
                _shuffledIndices[j] = temp;
            }

            // Ensure the requested firstIndex (or lastIndex from previous bag) is handled
            if (lastIndex != -1 && _shuffledIndices.Length > 0)
            {
                // Find where lastIndex ended up
                int idx = Array.IndexOf(_shuffledIndices, lastIndex);
                if (idx != -1)
                {
                    if (firstIndex != -1)
                    {
                        // Move firstIndex to the front
                        int temp = _shuffledIndices[0];
                        _shuffledIndices[0] = _shuffledIndices[idx];
                        _shuffledIndices[idx] = temp;
                    }
                    else
                    {
                        // We are starting a new bag, ensure the first item isn't the same as the last bag's last item
                        if (_shuffledIndices[0] == lastIndex && _shuffledIndices.Length > 1)
                        {
                            int swapIdx = _shuffleRandom.Next(1, _shuffledIndices.Length);
                            int temp = _shuffledIndices[0];
                            _shuffledIndices[0] = _shuffledIndices[swapIdx];
                            _shuffledIndices[swapIdx] = temp;
                        }
                    }
                }
            }

            _shuffledIndexPointer = -1;
        }



        public void JumpToIndex(int index, bool force = false)
        {
            if (_files == null || index < 0 || index >= _files.Length) return;

            // Proceed if:
            // 1. It's a different index
            // 2. It's a forced jump (manual skip)
            // 3. The current player has reached the end and needs to restart (looping)
            bool shouldJump = force || _currentPos != index || (Player != null && Player.Status == FlyleafLib.MediaPlayer.Status.Ended);
            if (!shouldJump) return;

            Logger.Debug($"Jumping to index {index} (requested for sync, force={force}, sameIndex={_currentPos == index})");

            lock (_loadLock)
            {
                // Cancel any pending load and clear the CTS so LoadCurrentVideo creates a fresh one
                if (_loadCts != null)
                {
                    _loadCts.Cancel();
                    _loadCts.Dispose();
                    _loadCts = null;
                }
                IsLoadingStatus = true; // Mark as loading immediately to prevent sync timer from starting the clock
                _recursionDepth = 0;
            }

            _manualPauseRequested = false; // Explicit user jump resets pause intent
            _currentPos = index;

            // Align shuffle pointer if enabled
            if (IsShuffle && _shuffledIndices != null)
            {
                int idx = Array.IndexOf(_shuffledIndices, index);
                if (idx != -1)
                {
                    _shuffledIndexPointer = idx;
                    Logger.Debug($"[Shuffle] Manually jumped to index {index}, aligning shuffle pointer to position {idx + 1}/{_shuffledIndices.Length}");
                }
                else
                {
                    // This shouldn't happen unless the queue changed without re-initializing shuffle
                    InitializeShuffle(index);
                    _shuffledIndexPointer = 0;
                }
            }

            _ = LoadCurrentVideo();
        }

        private VideoItem _currentItem;

        public virtual async void PlayNext(bool force = false)
        {
            try
            {
                if (_disposed) return;

                if (force) _manualPauseRequested = false;

                if (_files == null || _files.Length == 0)
                {
                    Logger.Warning($"No videos in queue for {MonitorName}. Triggering terminal failure.");
                    TerminalFailure?.Invoke(this, EventArgs.Empty);
                    return;
                }

                // Record end of current video before moving to next
                // If forced skip, count as skip
                RecordCurrentPlayEnd(force);

                // Prevent rapid/concurrent calls to PlayNext() while loading
                // This protects against race conditions when PlayNext() is called multiple times quickly
                lock (_loadLock)
                {
                    if (_isLoading && !force)
                    {
                        Logger.Warning("PlayNext() called while already loading, skipping to prevent race condition");
                        return;
                    }

                    if (force && _isLoading)
                    {
                        Logger.Debug("PlayNext forced: Interrupting current load to skip.");
                        _loadCts?.Cancel();
                        IsLoadingStatus = false;
                        _recursionDepth = 0; // Reset recursion depth on force skip
                        Stop(); // Interrupt current player if it was trying to open
                    }

                    _loadCts?.Cancel();
                    _loadCts = new CancellationTokenSource();
                    IsLoadingStatus = true; // Set loading flag early to prevent other PlayNext calls
                }

                // Find the next valid video that hasn't failed too many times
                // --- 0. FOLLOWER SYNC CHECK ---
                // If we're a follower in a coordinated group, we don't make our own decisions.
                // We forward the request to the master or wait for the broadcast.
                if (!string.IsNullOrEmpty(SyncGroupId) && !IsSyncMaster)
                {
                    if (force)
                    {
                        Logger.Debug($"[PlayNext] {this.MonitorName} (Follower) received manual skip. Requesting group skip via master.");
                        if (ServiceContainer.TryGet<VideoPlayerService>(out var vps))
                        {
                            // Find the master for this group and trigger skip
                            var master = vps.ActiveViewModels.FirstOrDefault(vm => vm.SyncGroupId == this.SyncGroupId && vm.IsSyncMaster);
                            master?.PlayNext(true);
                        }
                    }
                    else
                    {
                        Logger.Debug($"[PlayNext] {this.MonitorName} (Follower) ignoring auto-skip, waiting for master sync.");
                    }
                    IsLoadingStatus = false;
                    return;
                }

                int attempts = 0;
                do
                {
                    if (IsShuffle && _files.Length > 1)
                    {
                        if (_shuffledIndices == null || _shuffledIndices.Length != _files.Length || _shuffledIndexPointer >= _shuffledIndices.Length - 1)
                        {
                            InitializeShuffle();
                        }

                        _shuffledIndexPointer++;
                        _currentPos = _shuffledIndices[_shuffledIndexPointer];
                        Logger.Debug($"[Shuffle] Picked index {_currentPos} (Bag Position: {_shuffledIndexPointer + 1}/{_shuffledIndices.Length})");
                    }
                    else
                    {
                        // Sequential Logic
                        _currentPos = (_currentPos + 1) % _files.Length;
                    }

                    // --- 1. MASTER BROADCAST ---
                    // After master decides the next index, tell all followers
                    if (!string.IsNullOrEmpty(SyncGroupId) && IsSyncMaster)
                    {
                        if (ServiceContainer.TryGet<VideoPlayerService>(out var vps))
                        {
                            vps.BroadcastIndexToGroup(SyncGroupId, _currentPos, force);
                        }
                    }

                    attempts++;
                    if (attempts > _files.Length)
                    {
                        Logger.Error("No valid files found in playlist after full cycle. Stopping.");
                        Application.Current?.Dispatcher?.Invoke(() =>
                        {
                            Stop();
                            TerminalFailure?.Invoke(this, EventArgs.Empty);
                        });
                        IsLoadingStatus = false;
                        return;
                    }
                } while (_files[_currentPos] == null || (_fileFailureCounts.TryGetValue(_files[_currentPos].FilePath, out int fails) && fails >= 3));

                Logger.Debug($"[PlayNext] {this.MonitorName} selected next video: #{_currentPos} - {_files[_currentPos].FileName}");

                StartNewPlayRecord(_files[_currentPos].FilePath);
                _ = LoadCurrentVideo();
            }
            catch (Exception ex)
            {
                Logger.Error($"[PlayNext] Unhandled exception on {MonitorName}", ex);
                IsLoadingStatus = false;
            }
        }

        public virtual async void PlayPrevious(bool force = false)
        {
            try
            {
                if (_disposed) return;

                if (force) _manualPauseRequested = false;

                if (_files == null || _files.Length == 0)
                {
                    Logger.Warning($"No videos in queue for {MonitorName}. Triggering terminal failure.");
                    TerminalFailure?.Invoke(this, EventArgs.Empty);
                    return;
                }

                RecordCurrentPlayEnd(force);

                lock (_loadLock)
                {
                    if (_isLoading && !force) return;

                    if (force && _isLoading)
                    {
                        _loadCts?.Cancel();
                        IsLoadingStatus = false;
                        _recursionDepth = 0;
                        Stop();
                    }

                    _loadCts?.Cancel();
                    _loadCts = new CancellationTokenSource();
                    IsLoadingStatus = true;
                }

                if (!string.IsNullOrEmpty(SyncGroupId) && !IsSyncMaster)
                {
                    if (force)
                    {
                        Logger.Debug($"[PlayPrevious] {this.MonitorName} (Follower) received manual skip. Requesting group skip via master.");
                        if (ServiceContainer.TryGet<VideoPlayerService>(out var vps))
                        {
                            var master = vps.ActiveViewModels.FirstOrDefault(vm => vm.SyncGroupId == this.SyncGroupId && vm.IsSyncMaster);
                            master?.PlayPrevious(true);
                        }
                    }
                    IsLoadingStatus = false;
                    return;
                }

                int attempts = 0;
                do
                {
                    // Previous always goes sequentially backwards even if shuffle is on
                    // This is more intuitive for users (going back to what they just saw)
                    _currentPos = (_currentPos - 1 + _files.Length) % _files.Length;

                    if (!string.IsNullOrEmpty(SyncGroupId) && IsSyncMaster)
                    {
                        if (ServiceContainer.TryGet<VideoPlayerService>(out var vps))
                        {
                            vps.BroadcastIndexToGroup(SyncGroupId, _currentPos, force);
                        }
                    }

                    attempts++;
                    if (attempts > _files.Length)
                    {
                        Logger.Error("No valid files found in playlist after full cycle. Stopping.");
                        Application.Current?.Dispatcher?.Invoke(() =>
                        {
                            Stop();
                            TerminalFailure?.Invoke(this, EventArgs.Empty);
                        });
                        IsLoadingStatus = false;
                        return;
                    }
                } while (_files[_currentPos] == null || (_fileFailureCounts.TryGetValue(_files[_currentPos].FilePath, out int fails) && fails >= 3));

                Logger.Debug($"[PlayPrevious] {this.MonitorName} selected previous video: #{_currentPos} - {_files[_currentPos].FileName}");

                lock (_loadLock)
                {
                    // Cancel any pending load
                    if (_loadCts != null)
                    {
                        _loadCts.Cancel();
                        _loadCts.Dispose();
                        _loadCts = null;
                    }
                    IsLoadingStatus = true;
                }

                StartNewPlayRecord(_files[_currentPos].FilePath);
                _ = LoadCurrentVideo();
            }
            catch (Exception ex)
            {
                Logger.Error($"[PlayPrevious] Unhandled exception on {MonitorName}", ex);
                IsLoadingStatus = false;
            }
        }

        private void RecordCurrentPlayEnd(bool wasSkipped = false)
        {
            if (_currentPlayRecord == null || Player == null) return;

            if (ServiceContainer.TryGet<PlayHistoryService>(out var historyService))
            {
                // CurTime is in 100ns units (ticks), convert to ms
                _currentPlayRecord.WatchDurationMs = Player.CurTime / 10000;
                _currentPlayRecord.VideoDurationMs = Math.Max(0, Player.Duration) / 10000;

                // If duration is 0, we can't calculate percentage, but assume not a complete watch
                double percent = _currentPlayRecord.VideoDurationMs > 0
                    ? (double)_currentPlayRecord.WatchDurationMs / _currentPlayRecord.VideoDurationMs
                    : 0;

                _currentPlayRecord.WasSkipped = wasSkipped || (percent < 0.3 && _currentPlayRecord.VideoDurationMs > 10000); // Only small percent count if not a very short video
                _currentPlayRecord.WasCompleted = !wasSkipped && (percent > 0.9);

                historyService.AddRecord(_currentPlayRecord);
                _currentPlayRecord = null;
            }
        }

        private void StartNewPlayRecord(string filePath)
        {
            _currentPlayRecord = new VideoPlayRecord
            {
                FilePath = filePath,
                PlayedAt = DateTime.Now,
                SessionId = _sessionId,
                PlaylistHash = _playlistHash
            };
        }

        private async Task LoadCurrentVideo()
        {
            if (_disposed) return;

            // Ensure we yield immediately to the caller (e.g., SetQueue or sync timer)
            // to avoid blocking the UI thread during the synchronous initial parts of video opening.
            await Task.Yield();

            CancellationToken token;
            lock (_loadLock)
            {
                if (_loadCts == null) _loadCts = new CancellationTokenSource();
                token = _loadCts.Token;

                IsLoadingStatus = true; // Set flag inside lock to prevent race condition
                                        // DO NOT RESET _manualPauseRequested HERE. It must persist through internal retries and async ops.
                                        // It is only reset by explicit user actions (Play, Skip, Jump, SetQueue).

                IsReady = false; // Reset ready flag as we are starting a new load

                // Force UI to show "Pause" icon immediately since we are starting to load/play
                // This prevents the UI from showing "Play" icon briefly if Player.Status flickers to Stopped
                Application.Current?.Dispatcher?.InvokeAsync(() =>
                {
                    MediaState = System.Windows.Controls.MediaState.Play;
                });

                // Check recursion depth to prevent stack overflow
                _recursionDepth++;
                if (_recursionDepth > MaxRecursionDepth)
                {
                    Logger.Error($"Maximum recursion depth ({MaxRecursionDepth}) exceeded in LoadCurrentVideo. Stopping playback.");
                    IsLoadingStatus = false;
                    _recursionDepth = 0;
                    MediaErrorOccurred?.Invoke(this, new MediaErrorEventArgs("Playback stopped due to excessive errors. Please check your video files.", _currentItem));
                    return;
                }
            }

            try
            {
                if (token.IsCancellationRequested) return;

                if (_files == null || _files.Length == 0 || _currentPos < 0 || _currentPos >= _files.Length)
                {
                    lock (_loadLock)
                    {
                        IsLoadingStatus = false;
                    }
                    return;
                }

                CurrentItem = _files[_currentPos];
                // IsPlaying highlight is now handled centrally by VideoPlayerService
                // (Event subscription handled by property setter)
                // (Event subscription handled by property setter)

                var path = _currentItem.FilePath;
                Logger.Debug($"LoadCurrentVideo: Processing item '{_currentItem.FileName}' with path: {path}");

                // NEW: If we have an original page URL that requires download (like YouTube), 
                // and we don't have a local cache file yet, prioritize the page URL for JIT resolution.
                // This ensures we trigger the download+mux path instead of trying to play a low-quality streaming URL.
                if (!string.IsNullOrEmpty(_currentItem.OriginalPageUrl) && _currentItem.IsUrl)
                {
                    try
                    {
                        var pageUri = new Uri(_currentItem.OriginalPageUrl);
                        if (YtDlpService.IsDownloadRequiredSite(pageUri.Host))
                        {
                            Logger.Debug($"LoadCurrentVideo: Prioritizing OriginalPageUrl for resolution: {_currentItem.OriginalPageUrl}");
                            path = _currentItem.OriginalPageUrl;
                        }
                    }
                    catch { }
                }

                // CRITICAL FIX: Detect and clean malformed Rule34Video URLs
                if (path.Contains("rule34video.com/video/") && path.Contains("/function/0/https://"))
                {
                    Logger.Warning($"LoadCurrentVideo: Detected malformed Rule34Video URL with page prefix. Attempting to clean...");
                    int httpIndex = path.IndexOf("https://", path.IndexOf("/function/0/"));
                    if (httpIndex > 0)
                    {
                        var cleanedUrl = path.Substring(httpIndex);
                        Logger.Debug($"LoadCurrentVideo: Cleaned URL from '{path}' to '{cleanedUrl}'");
                        path = cleanedUrl;
                    }
                }

                // Validate based on whether it's a URL or local file
                if (_currentItem.IsUrl)
                {
                    Logger.Debug($"LoadCurrentVideo: Item is a URL, validating...");
                    // For URLs, validate URL format
                    if (!FileValidator.ValidateVideoUrl(path, out string urlValidationError))
                    {
                        Logger.Warning($"URL validation failed for '{_currentItem.FileName}': {urlValidationError}. Reporting failure.");

                        // Increment failure count for this item
                        _fileFailureCounts.AddOrUpdate(path, 1, (k, v) => v + 1);
                        _consecutiveFailures++;

                        OnMediaFailed(new ArgumentException($"URL validation failed: {urlValidationError}"), token);
                        return;
                    }
                    Logger.Debug($"LoadCurrentVideo: URL validation passed for: {path}");

                    // RESOLVE PAGE URLS: If it's a page URL and not already cached, resolve it now
                    if (FileValidator.IsPageUrl(path))
                    {
                        var cached = _downloadService.GetCachedFilePath(path);
                        if (string.IsNullOrEmpty(cached) || cached.EndsWith(".partial"))
                        {
                            if (token.IsCancellationRequested) return;
                            Logger.Debug($"LoadCurrentVideo: Page URL detected, resolving: {path}");

                            // Use longer timeout for sites that require download+mux (30 minutes vs 30 seconds)
                            var host = new Uri(path).Host.ToLowerInvariant();
                            bool isDownloadRequired = YtDlpService.IsDownloadRequiredSite(host);
                            int resolutionTimeoutMs = isDownloadRequired ? 1800000 : 30000;

                            // Warn the user once per session if they are streaming a site that relies on caching for 4K
                            if (isDownloadRequired && App.Settings?.EnableLocalCaching != true && !_hasShownCachingWarning)
                            {
                                _hasShownCachingWarning = true;
                                Logger.Warning($"LoadCurrentVideo: Local caching is disabled. Displaying warning to user for {host}.");
                                Application.Current?.Dispatcher?.InvokeAsync(() =>
                                {
                                    LoadingProgressText = "⚠️ Performance Warning: Local Caching is disabled. Quality will be limited.";
                                });
                            }

                            // Reset progress text before starting extraction
                            Application.Current?.Dispatcher?.InvokeAsync(() =>
                            {
                                LoadingProgressText = "";
                            });

                            var progress = new Progress<string>(status =>
                            {
                                Application.Current?.Dispatcher?.InvokeAsync(() =>
                                {
                                    LoadingProgressText = status;
                                });
                            });

                            var resolutionTask = _urlExtractor.ExtractVideoUrlAsync(path, token, progress);
                            var timeoutTask = Task.Delay(resolutionTimeoutMs, token);
                            var completedTask = await Task.WhenAny(resolutionTask, timeoutTask);

                            string resolved = null;
                            if (completedTask == resolutionTask)
                            {
                                resolved = await resolutionTask;
                            }
                            else
                            {
                                Logger.Warning($"LoadCurrentVideo: Resolution timed out after {(resolutionTimeoutMs / 1000)}s for: {path}");
                            }

                            if (!string.IsNullOrEmpty(resolved))
                            {
                                Logger.Debug($"LoadCurrentVideo: Successfully resolved page URL to: {resolved}");
                                path = resolved;
                            }
                            else
                            {
                                if (token.IsCancellationRequested)
                                {
                                    Logger.Debug("LoadCurrentVideo: Resolution cancelled.");
                                    return;
                                }

                                string errorDetail = completedTask == timeoutTask ? $"Resolution timed out ({(resolutionTimeoutMs / 1000)}s)" : "Extraction failed";
                                Logger.Warning($"LoadCurrentVideo: {errorDetail} for page URL: {path}. Reporting failure.");

                                // Increment failure count for this URL
                                _fileFailureCounts.AddOrUpdate(path, 1, (k, v) => v + 1);
                                _consecutiveFailures++;

                                OnMediaFailed(new Exception($"Failed to resolve video URL: {errorDetail}"), token);
                                return;
                            }
                        }
                    }

                    // Check if this URL is cached locally for instant playback
                    var cachedPath = _downloadService.GetCachedFilePath(path);
                    if (!string.IsNullOrEmpty(cachedPath) && !cachedPath.EndsWith(".partial"))
                    {
                        // Concurrent Playback Safetey Check:
                        // If we are about to use a .downloading file (partial) AND we have a saved playback position,
                        // we must FORCE streaming instead. Seeking into a non-downloaded area of a local file fails/hangs,
                        // whereas streaming allows random access to non-buffered areas.
                        bool forceStream = false;
                        if (cachedPath.EndsWith(".downloading") && App.Settings?.RememberFilePosition == true)
                        {
                            // Peeking at the tracking path for the CURRENT item (which is still the URL/PageURL)
                            var savedPos = PlaybackPositionTracker.Instance.GetPosition(_currentItem.TrackingPath);
                            if (savedPos.HasValue && savedPos.Value.TotalSeconds > 10)
                            { // arbitrary buffer
                                Logger.Debug($"[ConcurrentPlayback] Active download detected but found saved position ({savedPos.Value:mm\\:ss}). Forcing stream for safe seeking.");
                                forceStream = true;
                            }
                        }

                        if (!forceStream)
                        {
                            // Validate the cached file before using it
                            if (FileValidator.IsCorruptedCacheFile(cachedPath))
                            {
                                Logger.Warning($"[PreBuffer] Cached file is corrupted or a manifest: {cachedPath}. Deleting and falling back to streaming.");
                                try { File.Delete(cachedPath); } catch { }
                            }
                            else
                            {
                                Logger.Debug($"[PreBuffer] Using cached file: {Path.GetFileName(cachedPath)}");
                                path = cachedPath;

                                // Update existing item instead of replacing it to maintain UI highlighting
                                if (string.IsNullOrEmpty(CurrentItem.OriginalPageUrl))
                                {
                                    CurrentItem.OriginalPageUrl = FileValidator.IsPageUrl(_currentItem.FilePath) ? _currentItem.FilePath : null;
                                }
                                CurrentItem.FilePath = cachedPath;
                            }
                        }
                    }
                }
                else
                {
                    // For local files, check if path is rooted
                    if (!Path.IsPathRooted(path))
                    {
                        Logger.Warning($"Non-rooted path detected for '{_currentItem.FileName}': {path}. Skipping to next video.");
                        PlayNext(true);
                        return;
                    }

                    // Re-validate file existence before attempting to load
                    if (!FileValidator.ValidateVideoFile(path, out string validationError) && !path.EndsWith(".downloading"))
                    {
                        Logger.Warning($"File validation failed for '{_currentItem.FileName}': {validationError}. Skipping to next video.");

                        // Cleanup corrupted cache files immediately (e.g. HLS manifests saved as MP4)
                        if (path.Contains("VideoCache") && File.Exists(path))
                        {
                            try { File.Delete(path); Logger.Debug($"Deleted corrupted cache file: {path}"); } catch { }
                        }

                        // Increment failure count for this path
                        _fileFailureCounts.AddOrUpdate(path, 1, (k, v) => v + 1);
                        _consecutiveFailures++;

                        lock (_loadLock)
                        {
                            IsLoadingStatus = false;
                        }
                        PlayNext(true);
                        return;
                    }
                }

                // Apply per-monitor/per-item settings
                Opacity = _currentItem.Opacity;
                Volume = _currentItem.Volume;

                // --- SITE-SPECIFIC SETTINGS (Moved down to be closer to Open) ---

                RequestStopBeforeSourceChange?.Invoke(this, EventArgs.Empty);

                Uri newSource;
                if (_currentItem.IsUrl || path.StartsWith("http"))
                {
                    newSource = new Uri(path, UriKind.Absolute);
                }
                else
                {
                    if (Path.IsPathRooted(path) && !path.StartsWith("http"))
                    {
                        if (!File.Exists(path))
                        {
                            Logger.Error($"LoadCurrentVideo: File not found at path: {path}");
                            OnMediaFailed(new FileNotFoundException("File not found", path), token);
                            return;
                        }
                        else
                        {
                            Logger.Debug($"LoadCurrentVideo: File verified to exist: {path}");

                            if (path.Contains('[') || path.Contains(']') || path.Contains('#') || path.Contains('%') || path.Contains(' ') || path.Any(c => c > 127))
                            {
                                string shortPath = GetShortPath(path);
                                if (!string.Equals(shortPath, path, StringComparison.OrdinalIgnoreCase))
                                {
                                    Logger.Debug($"LoadCurrentVideo: Converted problematic path '{path}' to short path '{shortPath}'");
                                    path = shortPath;
                                }
                            }
                        }
                    }

                    if (path.Contains('#') || path.Contains('%'))
                    {
                        try
                        {
                            var uriBuilder = new UriBuilder
                            {
                                Scheme = Uri.UriSchemeFile,
                                Host = string.Empty,
                                Path = Path.GetFullPath(path)
                            };
                            newSource = uriBuilder.Uri;
                        }
                        catch (Exception ex)
                        {
                            Logger.Warning($"Failed to create URI using UriBuilder for path: {path}. Falling back to standard constructor.", ex);
                            newSource = new Uri(Path.GetFullPath(path));
                        }
                    }
                    else
                    {
                        newSource = new Uri(Path.GetFullPath(path));
                    }

                    Logger.Debug($"LoadCurrentVideo: Generated URI: {newSource.AbsoluteUri}");
                } // End if(!IsUrl)

                lock (_loadLock)
                {
                    _expectedSource = newSource;

                    if (App.Settings?.RememberFilePosition == true)
                    {
                        var savedPos = PlaybackPositionTracker.Instance.GetPosition(_currentItem.TrackingPath);
                        if (savedPos.HasValue)
                        {
                            Logger.Debug($"[Resume] Found saved position for '{_currentItem.FileName}': {savedPos.Value:mm\\:ss}. Setting pending seek.");
                            _pendingSeekPosition = savedPos.Value;
                        }
                    }
                }

                if (CurrentSource != null && CurrentSource.Equals(newSource))
                {
                    CurrentSource = null; // Clear source to force reload
                }

                try
                {
                    Config.Demuxer.UserAgent = App.Settings?.UserAgent;
                    Config.Demuxer.Cookies = App.Settings?.Cookies;

                    // Default network stability options
                    Config.Demuxer.FormatOpt["reconnect"] = "1";
                    Config.Demuxer.FormatOpt["reconnect_streamed"] = "1";
                    Config.Demuxer.FormatOpt["reconnect_delay_max"] = "5";
                    Config.Demuxer.FormatOpt["reconnect_on_network_error"] = "1";
                    Config.Demuxer.FormatOpt["reconnect_on_http_error"] = "1";
                    Config.Demuxer.FormatOpt["http_persistent"] = "1";
                    Config.Demuxer.FormatOpt["tls_verify"] = "0"; // Helps with handshake failures on some CDNs
                    Config.Demuxer.FormatOpt["rw_timeout"] = "15000000"; // 15s socket timeout (in microseconds)

                    // --- SITE-SPECIFIC HEADER INJECTION ---
                    string referer = _currentItem.OriginalPageUrl;

                    // Common site detection
                    if (path.Contains("rule34video.com"))
                    {
                        referer = "https://rule34video.com/";
                    }
                    else if (path.Contains("pmvhaven.com"))
                    {
                        referer = "https://pmvhaven.com/";
                    }
                    else if (path.Contains("iwara.tv") || (referer != null && referer.Contains("iwara.tv")))
                    {
                        referer = "https://iwara.tv/";
                    }
                    else if (path.Contains("hypnotube.com"))
                    {
                        if (App.Settings != null && !string.IsNullOrEmpty(App.Settings.HypnotubeCookies))
                        {
                            Config.Demuxer.FormatOpt["headers"] = $"Cookie: {App.Settings.HypnotubeCookies.Trim()}\r\n";
                        }
                    }

                    // If we resolved a Page URL locally, that page URL is a good Referer backup
                    if (string.IsNullOrEmpty(referer) && FileValidator.IsPageUrl(_currentItem.FilePath))
                    {
                        referer = _currentItem.FilePath;
                    }

                    if (!string.IsNullOrEmpty(referer))
                    {
                        Logger.Debug($"LoadCurrentVideo: Injecting Referer header: {referer}");
                        Config.Demuxer.FormatOpt["referer"] = referer;
                        if (referer == "https://iwara.tv/")
                        {
                            Config.Demuxer.FormatOpt["headers"] = $"Referer: {referer}\r\nOrigin: https://iwara.tv\r\n";
                        }
                        else
                        {
                            Config.Demuxer.FormatOpt["headers"] = $"Referer: {referer}\r\n";
                        }
                    }
                    else
                    {
                        Config.Demuxer.FormatOpt.Remove("referer");
                        Config.Demuxer.FormatOpt.Remove("headers");
                    }

                    // Monitor for "Opening" stall
                    // Increase timeout for remote URLs significantly (60s) vs local files (30s)
                    // PMVHaven and other HLS streams can take 20-30s to probe and buffer variants.
                    int timeoutMs = _currentItem.IsUrl ? 60000 : 30000;
                    _ = Task.Delay(timeoutMs, token).ContinueWith(t =>
                    {
                        if (t.IsCanceled || _disposed) return;

                        var status = Player.Status;
                        var isLoading = _isLoading;
                        if (isLoading && status == FlyleafLib.MediaPlayer.Status.Opening)
                        {
                            Logger.Warning($"LoadCurrentVideo: Player seems stuck in 'Opening' status for {timeoutMs / 1000}s. Reporting failure.");
                            var ex = new TimeoutException($"Video source timed out during opening ({timeoutMs / 1000}s stall).");
                            if (Application.Current?.Dispatcher != null)
                            {
                                Application.Current.Dispatcher.InvokeAsync(() => OnMediaFailed(ex, token));
                            }
                            else
                            {
                                OnMediaFailed(ex, token);
                            }
                        }
                    }, TaskContinuationOptions.ExecuteSynchronously);

                    if (token.IsCancellationRequested)
                    {
                        Logger.Debug("LoadCurrentVideo: Aborting Player.Open because task was cancelled.");
                        return;
                    }

                    Logger.Debug($"LoadCurrentVideo: Opening path in Flyleaf: {path}");
                    CurrentSource = newSource;

                    // Call Open in a separate task to ensure zero risk of blocking the UI thread
                    // during heavy probing or network initialization.
                    await Task.Run(() => Player.Open(path), token);
                    Logger.Debug("LoadCurrentVideo: Player.Open call returned.");
                }
                catch (Exception ex)
                {
                    Logger.Error("Error in LoadCurrentVideo()", ex);
                    OnMediaFailed(ex, token);
                }
            }
            finally
            {
                lock (_loadLock)
                {
                    _recursionDepth = Math.Max(0, _recursionDepth - 1);
                }
            }
        }

        private void CurrentItem_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (_currentItem == null) return;
            if (e.PropertyName == nameof(VideoItem.Opacity))
            {
                Opacity = _currentItem.Opacity;
            }
            else if (e.PropertyName == nameof(VideoItem.Volume))
            {
                Volume = _currentItem.Volume;
            }
        }

        private void Player_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (_disposed) return;

            if (e.PropertyName == nameof(Player.Status))
            {
                // Sync internal MediaState with actual engine status to prevent stale state (fixes double-click issues)
                // Treat Opening as Playing (Active) so UI shows Pause icon
                if (Player.Status == FlyleafLib.MediaPlayer.Status.Playing || Player.Status == FlyleafLib.MediaPlayer.Status.Opening)
                {
                    // Critical: If user manually requested pause during Opening, DO NOT revert UI to Play (Pause Icon).
                    // This prevents the UI from flickering "Pause ||" when the user just clicked "Pause" (showing >)
                    if (!_manualPauseRequested)
                    {
                        _mediaState = System.Windows.Controls.MediaState.Play;
                        OnPropertyChanged(nameof(MediaState));
                    }

                    StartResolutionPolling();
                }
                else if (Player.Status == FlyleafLib.MediaPlayer.Status.Paused || Player.Status == FlyleafLib.MediaPlayer.Status.Stopped)
                {
                    // Ignore Stopped/Paused status updates if we are in the middle of loading a video
                    // This prevents the UI from flipping to "Play" icon momentarily while the player initializes
                    if (!_isLoading)
                    {
                        _mediaState = System.Windows.Controls.MediaState.Pause;
                        OnPropertyChanged(nameof(MediaState));
                    }
                }

                if (Player.Status == FlyleafLib.MediaPlayer.Status.Ended)
                {
                    // Critical: Ignore 'Ended' status if we are in the middle of loading a video.
                    // Flyleaf often fires 'Ended' for the PREVIOUS video when a new Open() is called.
                    // Processing this would trigger a recursive Skip loop.
                    if (_isLoading)
                    {
                        Logger.Warning($"Ignoring Status.Ended for '{MonitorName}' because a new video is already loading.");
                        return;
                    }
                    OnMediaEnded();
                }
            }
            else if (e.PropertyName == nameof(Player.Video))
            {
                OnPropertyChanged(nameof(ClockDriftMs));
                OnPropertyChanged(nameof(D3DImageLatencyMs));
                UpdateResolution();
            }
            else if (e.PropertyName == nameof(Player.CurTime))
            {
                // Update internal state record
                var position = TimeSpan.FromTicks(Player.CurTime);
                LastPositionRecord = (position, DateTime.Now.Ticks);

                // Save to persistent tracker every 3 seconds
                if ((DateTime.Now - _lastSaveTime).TotalSeconds >= 3 && _currentItem != null)
                {
                    SaveCurrentPosition();
                }
            }
        }

        private void SaveCurrentPosition()
        {
            // Prevent overwriting saved position during initial load or while seeking
            // Added 5s cooldown after seek to allow HLS streams to stabilize and reach the target timestamp
            if (_currentItem != null && Player != null && !_isLoading && !_pendingSeekPosition.HasValue && (DateTime.Now - _lastSeekTime).TotalSeconds > 5)
            {
                var position = TimeSpan.FromTicks(Player.CurTime);
                PlaybackPositionTracker.Instance.UpdatePosition(_currentItem.TrackingPath, position);
                _lastSaveTime = DateTime.Now;
            }
        }

        private void Player_OpenCompleted(object sender, FlyleafLib.MediaPlayer.OpenCompletedArgs e) => OnMediaOpened(sender, e);

        public void OnMediaEnded()
        {
            if (_disposed) return;
            IsReady = false;
            Logger.Debug($"[HypnoViewModel] Media ended: {CurrentSource} (Pos: #{_currentPos})");

            _consecutiveFailures = 0; // Reset failure counter on successful playback

            // SESSION RESUME: Clear position so we don't resume at the very end next time
            if (App.Settings?.RememberFilePosition == true && _currentItem != null)
            {
                PlaybackPositionTracker.Instance.ClearPosition(_currentItem.TrackingPath);
            }

            // Clear failure count for this file since it played successfully (thread-safe)
            if (_currentItem != null)
            {
                if (_currentItem.FilePath != null)
                {
                    _fileFailureCounts.TryRemove(_currentItem.FilePath, out _);
                }

                // SESSION RESUME: Clear position so we don't resume at the very end next time
                if (App.Settings?.RememberFilePosition == true)
                {
                    PlaybackPositionTracker.Instance.ClearPosition(_currentItem.TrackingPath);
                }
            }

            // Do NOT reset recursion depth here; let it be handled by success/fail
            // to correctly catch rapid recursive failures.

            PlayNext();
        }

        public void OnMediaOpened(object sender, FlyleafLib.MediaPlayer.OpenCompletedArgs e)
        {
            if (_disposed) return;
            if (!string.IsNullOrEmpty(e.Error))
            {
                Logger.Warning($"[HypnoViewModel] Player_OpenCompleted failed with error: {e.Error}. Throwing OnMediaFailed.");
                OnMediaFailed(new Exception(e.Error));
                return;
            }
            // Verify that the opened media matches what we're expecting
            // This prevents stale MediaOpened events from previous sources after SetQueue() changes
            lock (_loadLock)
            {
                // If CurrentSource doesn't match expected source, this is a stale event - ignore it
                if (_expectedSource == null || CurrentSource != _expectedSource)
                {
                    Logger.Warning("OnMediaOpened called for stale source, ignoring");
                    return;
                }

                // Reset loading flag when media successfully opens
                // This must be done in a lock to ensure thread safety
                IsLoadingStatus = false;
                // Reset recursion depth on successful load
                _recursionDepth = 0;
            }

            // Reset failure counter when video successfully opens
            _consecutiveFailures = 0;

            // Clear failure count for this file since it opened successfully (thread-safe)
            if (_currentItem?.FilePath != null)
            {
                _fileFailureCounts.TryRemove(_currentItem.FilePath, out _);
            }

            // Set initial parameters
            if (Player.Audio != null)
            {
                Player.Audio.Volume = (int)(_volume * 100);
                Logger.Debug($"[HypnoViewModel] Applied volume {Player.Audio.Volume} to new media.");
            }
            Player.Speed = _speedRatio;

            if (_manualPauseRequested)
            {
                Logger.Debug("[HypnoViewModel] Manual pause requested during load. Preventing auto-play.");
                Pause();
            }
            else if (UseCoordinatedStart)
            {
                // Coordinated start: Request PLAY to allow buffering to complete, 
                // but rely on the stopped SharedClock to keep it frozen at frame 0.
                // NOTE: We MUST call Play() here, not just set MediaState, to trigger the engine.
                Play();
                IsReady = true;
                RequestReady?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                // Request play now that media is confirmed loaded
                // This ensures Play() is only called after MediaElement has processed the source
                Play();
            }

            // Handle pending seek (e.g., from RestoreState) - SEEK AFTER PLAY to ensure it applies
            if (_pendingSeekPosition.HasValue)
            {
                var pos = _pendingSeekPosition.Value;
                Logger.Debug($"[Resume] Seeking to saved position: {pos:mm\\:ss} (Ticks: {pos.Ticks})");

                // For coordinated groups, the master's resume position drives the shared clock
                if (UseCoordinatedStart && IsSyncMaster && ExternalClock is Classes.SharedClock sharedClock)
                {
                    Logger.Debug($"[Resume] Sync master updating SharedClock to resumed position: {pos}");
                    sharedClock.Seek(pos.Ticks);
                }

                Player.SeekAccurate((int)(pos.Ticks / 10000));
                _pendingSeekPosition = null;
                _lastSeekTime = DateTime.Now;
            }

            // Start pre-buffering the next video for instant playback
            StartPreBuffer();

            // Notify listeners that media has opened successfully
            MediaOpened?.Invoke(this, EventArgs.Empty);

            // Capture initial resolution
            StartResolutionPolling();
        }

        /// <summary>
        /// Starts pre-buffering the next video in the queue.
        /// Prioritizes 1080p+ videos and uses partial downloading for faster startup.
        /// </summary>
        private void StartPreBuffer()
        {
            if (_disposed) return;
            // Cancel any existing pre-buffer operation
            _preBufferCts?.Cancel();
            _preBufferCts = new CancellationTokenSource();
            var cancellationToken = _preBufferCts.Token;

            if (_files == null || _files.Length == 0) return;

            // Look ahead up to 3 videos and collect candidates for pre-buffering
            var candidates = new System.Collections.Generic.List<(VideoItem item, int quality, int position)>();
            for (int i = 1; i <= Math.Min(3, _files.Length); i++)
            {
                int pos = (_currentPos + i) % _files.Length;
                if (pos == _currentPos) continue; // Don't buffer current video

                var item = _files[pos];
                if (item?.IsUrl == true)
                {
                    // CRITICAL: Unknown/Live streams (.m3u8, .mpd) cannot be pre-buffered via simple download.
                    // Downloading them just gets the playlist text file, which breaks the player if treated as a video cache.
                    // We typically exclude them unless we implement a complex stream downloader.
                    if (item.FilePath.Contains(".m3u8") || item.FilePath.Contains(".mpd"))
                    {
                        continue;
                    }

                    var quality = QualitySelector.DetectQualityFromUrl(item.FilePath);
                    candidates.Add((item, quality, pos));
                }
            }

            if (candidates.Count == 0) return;

            // Prioritize 1080p+ videos, otherwise take the next one
            var highRes = candidates
                .Where(c => c.quality >= 1080)
                .OrderByDescending(c => c.quality)
                .FirstOrDefault();

            VideoItem nextItem;
            int detectedQuality;

            if (highRes.item != null)
            {
                nextItem = highRes.item;
                detectedQuality = highRes.quality;
                Logger.Debug($"[PreBuffer] Prioritizing high-res video: {detectedQuality}p - {nextItem.FileName}");
            }
            else
            {
                var first = candidates.First();
                nextItem = first.item;
                detectedQuality = first.quality;
                Logger.Debug($"[PreBuffer] No high-res found, buffering next: {(detectedQuality > 0 ? $"{detectedQuality}p" : "unknown")} - {nextItem.FileName}");
            }

            var videoUrl = nextItem.FilePath;

            // Already cached (full)?
            // We ignore .partial files here to ensure we resolve the URL correctly
            var cachedPath = _downloadService.GetCachedFilePath(videoUrl);
            if (!string.IsNullOrEmpty(cachedPath) && !cachedPath.EndsWith(".partial"))
            {
                _preBufferedUrl = videoUrl;
                _preBufferedPath = cachedPath;
                Logger.Debug($"[PreBuffer] Already cached: {Path.GetFileName(cachedPath)}");
                return;
            }

            // Start background partial download for faster startup
            Logger.Debug($"[PreBuffer] Starting partial download for: {nextItem.FileName}");
            _ = Task.Run(async () =>
            {
                try
                {
                    if (_disposed) return;
                    var finalUrl = videoUrl;

                    // If it's a page URL, resolve it first to avoid "Opening" stalls later
                    if (FileValidator.IsPageUrl(videoUrl))
                    {
                        Logger.Debug($"[PreBuffer] Resolving site URL: {nextItem.FileName}");
                        var resolved = await _urlExtractor.ExtractVideoUrlAsync(videoUrl, cancellationToken);
                        if (!string.IsNullOrEmpty(resolved) && !cancellationToken.IsCancellationRequested)
                        {
                            if (resolved.Contains(".m3u8") || resolved.Contains(".mpd"))
                            {
                                Logger.Debug($"[PreBuffer] Resolved to stream ({Path.GetExtension(resolved)}), skipping caching: {nextItem.FileName}");
                                nextItem.FilePath = resolved; // Still update path so it loads immediately later
                                return;
                            }

                            finalUrl = resolved;
                            // Update the item so LoadCurrentVideo picks it up immediately
                            nextItem.FilePath = resolved;
                            Logger.Debug($"[PreBuffer] Successfully resolved {nextItem.FileName}");
                        }
                        else if (!cancellationToken.IsCancellationRequested)
                        {
                            Logger.Warning($"[PreBuffer] Failed to resolve site URL: {nextItem.FileName}. Aborting pre-buffer for this item.");
                            return; // DO NOT proceed with downloading the page URL!
                        }
                    }

                    // Header preparation
                    var headers = new System.Collections.Generic.Dictionary<string, string>();
                    if (!string.IsNullOrEmpty(nextItem.OriginalPageUrl))
                    {
                        headers["Referer"] = nextItem.OriginalPageUrl;
                    }

                    // DOWNLOAD TO CACHE: For sites like Rule34Video with short-lived URLs, 
                    // we MUST download to disk immediately or extraction is wasted.
                    // We use DownloadVideoAsync (full) to avoid Flyleaf hitches with partial files.
                    // If the file is massive, it might take a while, but it's the most reliable way.
                    var localPath = await _downloadService.DownloadVideoAsync(finalUrl, headers, cancellationToken);

                    if (_disposed) return;
                    if (!string.IsNullOrEmpty(localPath) && !cancellationToken.IsCancellationRequested)
                    {
                        _preBufferedUrl = finalUrl;
                        _preBufferedPath = localPath;
                        Logger.Debug($"[PreBuffer] Completed disk cache: {Path.GetFileName(localPath)}");
                    }
                }
                catch (OperationCanceledException)
                {
                    Logger.Debug("[PreBuffer] Cancelled");
                }
                catch (Exception ex)
                {
                    Logger.Warning($"[PreBuffer] Failed: {ex.Message}");
                }
            }, cancellationToken);
        }

        public void OnMediaFailed(Exception ex, CancellationToken token = default)
        {
            Logger.Error($"[HypnoViewModel] OnMediaFailed triggered for {_currentItem?.FileName ?? "Unknown"}: {ex.Message}");
            if (ex.InnerException != null)
            {
                Logger.Error($"[HypnoViewModel] Inner Exception: {ex.InnerException.Message}");
            }

            MediaFailed?.Invoke(this, new MediaErrorEventArgs(ex.Message, _currentItem, ex));
            if (_disposed) return;
            IsReady = false;
            // Reset loading flag on failure
            // This must be done in a lock to ensure thread safety
            lock (_loadLock)
            {
                IsLoadingStatus = false;
                // Capture current load token if none provided
                if (token == default && _loadCts != null) token = _loadCts.Token;
            }

            // CRITICAL: If the player is stuck in 'Opening', force it to stop
            // to break any underlying stalls before we attempt to skip.
            if (Player.Status == FlyleafLib.MediaPlayer.Status.Opening)
            {
                Logger.Warning("OnMediaFailed: Player reported failure while in 'Opening' status. Forcing stop.");
                Player.Stop();
            }

            var fileName = _currentItem?.FileName ?? "Unknown";
            var filePath = _currentItem?.FilePath;

            // Check for specific codec/media foundation errors
            bool isCodecError = false;
            bool isFileNotFoundError = false;
            bool isUrlOpenError = false;
            string specificAdvice = "";

            if (ex is COMException comEx)
            {
                // 0x8898050C = MILAVERR_UNEXPECTEDWMPFAILURE (Common with resource exhaustion or codec issues)
                // 0xC00D5212 = MF_E_TOPO_CODEC_NOT_FOUND (Explicit missing codec)
                // 0xC00D11B1 = NS_E_WMP_FILE_OPEN_FAILED (File/URL cannot be opened)
                uint errorCode = (uint)comEx.ErrorCode;
                if (errorCode == 0x8898050C)
                {
                    isCodecError = true;
                    specificAdvice = " This error (0x8898050C) typically indicates: 1) GPU/VRAM exhaustion when playing multiple videos, 2) Missing codecs, or 3) Corrupted video file. Try reducing the number of active screens or check if the file plays in other media players.";
                }
                else if (errorCode == 0xC00D5212)
                {
                    isCodecError = true;
                    specificAdvice = " Missing codec for this video format. Install required codecs or convert the video to a supported format.";
                }
                else if (errorCode == 0xC00D11B1)
                {
                    // File open failed - different handling for URLs vs local files
                    if (_currentItem?.IsUrl == true)
                    {
                        isUrlOpenError = true;
                        specificAdvice = " URL cannot be opened. This typically means: 1) The URL has expired or is no longer valid, 2) Network connectivity issues, 3) The server is unavailable, or 4) DRM-protected content. Try refreshing the URL or checking your network connection.";

                        // Clear cache for this page URL so it can be re-extracted on next attempt
                        if (!string.IsNullOrEmpty(_currentItem?.OriginalPageUrl))
                        {
                            Logger.Debug($"[HypnoViewModel] Clearing cached URL for '{_currentItem.OriginalPageUrl}' due to playback failure.");
                            PersistentUrlCache.Instance.Remove(_currentItem.OriginalPageUrl);
                        }
                    }
                    else
                    {
                        isFileNotFoundError = true;
                        specificAdvice = " File cannot be opened. The file may be locked by another application, corrupted, or you may lack read permissions.";
                    }
                }
            }
            else if (ex is System.IO.FileNotFoundException)
            {
                isFileNotFoundError = true;
                // Check if file actually exists - this could be a URI encoding issue
                if (filePath != null && System.IO.File.Exists(filePath))
                {
                    specificAdvice = " File exists on disk but MediaElement cannot load it. This may be due to special characters in the filename or path. Try renaming the file to remove special characters like '&', '#', etc.";
                }
                else
                {
                    specificAdvice = " File does not exist or has been moved/deleted.";
                }
            }

            // --- Flyleaf / Web Stream Specific Error Handling ---
            bool isForbiddenError = ex.Message.Contains("403 Forbidden") || (ex.InnerException?.Message.Contains("403 Forbidden") ?? false);
            bool isNotFoundError = ex.Message.Contains("404 Not Found") || (ex.InnerException?.Message.Contains("404 Not Found") ?? false);
            bool isTimeoutError = ex is TimeoutException;

            if ((isForbiddenError || isNotFoundError || isTimeoutError) && _currentItem?.IsUrl == true)
            {
                // If we have an OriginalPageUrl and it's different from the current FilePath, 
                // it means the temporary streaming URL we resolved earlier has likely expired.
                if (!string.IsNullOrEmpty(_currentItem.OriginalPageUrl) && _currentItem.FilePath != _currentItem.OriginalPageUrl)
                {
                    Logger.Warning($"[HypnoViewModel] Stream URL expired or timed out ({(isForbiddenError ? "403" : (isNotFoundError ? "404" : "Timeout"))}) for {fileName}. Clearing cache for '{_currentItem.OriginalPageUrl}'.");
                    PersistentUrlCache.Instance.Remove(_currentItem.OriginalPageUrl);

                    // Reset FilePath to OriginalPageUrl so the next attempt triggers fresh extraction
                    _currentItem.FilePath = _currentItem.OriginalPageUrl;

                    specificAdvice = " The temporary streaming link has expired or timed out. The cache has been cleared and it will be re-acquired on the next attempt.";
                }
                else
                {
                    // It's a 403/404 on the page itself, or we don't have a fallback.
                    isUrlOpenError = true;
                    specificAdvice = $" Server returned {(isForbiddenError ? "403 Forbidden" : (isNotFoundError ? "404 Not Found" : "Timeout"))}. The video may have been removed or is currently unavailable.";
                }
            }
            else if (ex.Message.Contains("FFmpeg Error") && _currentItem?.IsUrl == true)
            {
                isUrlOpenError = true;
                specificAdvice = $" FFmpeg reported an error: {ex.Message}. The stream may be incompatible or unstable.";
            }

            var errorMessage = $"Failed to play video: {fileName}";

            Logger.Error(errorMessage, ex);

            // If the failure happened with a cached file, delete it so it can be re-downloaded or streamed next time
            if (!string.IsNullOrEmpty(filePath) && filePath.Contains("VideoCache") && File.Exists(filePath))
            {
                Logger.Warning($"[HypnoViewModel] Deleting likely corrupted cache file: {filePath}");
                try { File.Delete(filePath); } catch { }

                // Revert to original URL so we can try streaming/re-extraction
                if (!string.IsNullOrEmpty(_currentItem?.OriginalPageUrl))
                {
                    Logger.Debug($"[HypnoViewModel] Reverting path to OriginalPageUrl: {_currentItem.OriginalPageUrl}");
                    _currentItem.FilePath = _currentItem.OriginalPageUrl;
                }
            }

            // Increment failure counters
            _consecutiveFailures++;

            // Track failures per file (thread-safe with ConcurrentDictionary)
            if (filePath != null)
            {
                // If it's a known unrecoverable error, force max failures to skip immediately
                // Codec errors, file not found errors (if file REALLLY missing), and URL open errors should skip immediately
                bool isGenuinelyMissing = isFileNotFoundError && !System.IO.File.Exists(filePath);
                bool shouldMarkUnrecoverable = isCodecError || isGenuinelyMissing || isUrlOpenError;

                int increment = shouldMarkUnrecoverable ? MaxFailuresPerFile : 1;
                int failureCount = _fileFailureCounts.AddOrUpdate(filePath, increment, (key, oldValue) => oldValue + increment);

                if (shouldMarkUnrecoverable)
                {
                    Logger.Warning($"Unrecoverable error for '{fileName}'. Marking as failed immediately to avoid retries.");
                }
                else
                {
                    Logger.Warning($"File '{fileName}' has failed {failureCount} time(s). Will skip after {MaxFailuresPerFile} failures.");
                }
            }

            // Notify listeners (e.g., UI) about the error
            MediaErrorOccurred?.Invoke(this, new MediaErrorEventArgs($"{errorMessage}.{specificAdvice} Error: {ex?.Message ?? "Unknown error"}", _currentItem));

            // Stop retrying if we've exceeded the failure threshold
            // This prevents infinite retry loops when all videos fail
            if (_consecutiveFailures >= MaxConsecutiveFailures)
            {
                Logger.Warning($"Stopped retrying after {MaxConsecutiveFailures} consecutive failures. All videos in queue may be invalid.");
                MediaErrorOccurred?.Invoke(this, new MediaErrorEventArgs($"Playback stopped after {MaxConsecutiveFailures} consecutive failures. Please check your video files.", _currentItem));
                TerminalFailure?.Invoke(this, EventArgs.Empty);
                return;
            }

            // Add a delay to allow GPU resources to free up (especially for 0x8898050C errors)
            int delayMs = isCodecError ? 500 : 300;
            _ = Task.Delay(delayMs, token).ContinueWith(t =>
            {
                if (t.IsCanceled || _disposed) return;
                Application.Current?.Dispatcher.InvokeAsync(() => PlayNext(true));
            }, TaskContinuationOptions.ExecuteSynchronously);
        }

        public virtual void Play()
        {
            MediaState = MediaState.Play;
            IsReady = false; // No longer just "Ready", actually playing
            Player.Play();
            RequestPlay?.Invoke(this, EventArgs.Empty);

            // Resume the shared sync clock so sync logic works correctly
            // CRITICAL FIX: Only resume the clock if we are NOT in a coordinated group OR if this is a manual user play.
            // For coordinated groups, we let the VideoPlayerService timer handle clock resumption when all are ready.
            if (string.IsNullOrEmpty(SyncGroupId) && ServiceContainer.TryGet<VideoPlayerService>(out var playerService))
            {
                playerService.ResumeSyncClock();
            }
        }

        public virtual void ForcePlay()
        {
            Play();
        }

        public virtual void Pause()
        {
            MediaState = MediaState.Pause;
            Player.Pause();
            RequestPause?.Invoke(this, EventArgs.Empty);

            // CRITICAL: Pause the shared sync clock to prevent auto-resume!
            // This is the fix for the "extra click" issue where sync was overriding user's pause.
            if (ServiceContainer.TryGet<VideoPlayerService>(out var playerService))
            {
                playerService.PauseSyncClock();
            }
        }

        public void Stop()
        {
            // Force save position before stopping
            SaveCurrentPosition();
            RecordCurrentPlayEnd(false); // Normal stop, not necessarily a skip unless watch time is low
            Player.Stop();
            RequestStop?.Invoke(this, EventArgs.Empty);
        }

        public virtual void SyncPosition(TimeSpan position)
        {
            Player.SeekAccurate((int)(position.Ticks / 10000));
            RequestSyncPosition?.Invoke(this, position);
        }

        public (int index, long positionTicks, double speed, string[] paths) GetPlaybackState()
        {
            // Return current state for persistence
            // Note: _files might be large, but we only need paths
            var paths = _files?.Select(f => f.FilePath).ToArray() ?? Array.Empty<string>();
            var pos = LastPositionRecord.timestamp > 0 ? LastPositionRecord.timestamp : 0;
            // Actually LastPositionRecord.position is the TimeSpan position.
            return (_currentPos, LastPositionRecord.position.Ticks, _speedRatio, paths);
        }

        public void RestoreState(int index, long positionTicks)
        {
            if (_files == null || _files.Length == 0) return;

            // Validate index
            if (index >= 0 && index < _files.Length)
            {
                _currentPos = index;
                // We need to signal that we want to start at this position
                // Typically Play(index) would be called.
                // But we want to seek too.
                // We can set a "PendingSeek" or just rely on the fact that LoadCurrentVideo hasn't happened yet?
                // If the window is just shown, LoadCurrentVideo might be called soon.

                // Let's set the index and let LoadCurrentVideo handle the loading.
                // But we need to SEEK after loading.

                // We'll use a specific method or modify LoadCurrentVideo
                // Ideally, we can set a property that OnMediaOpened uses to Seek.
                _pendingSeekPosition = TimeSpan.FromTicks(positionTicks);
                _ = LoadCurrentVideo();
            }
        }

        private TimeSpan? _pendingSeekPosition;
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern uint GetShortPathName(string lpszLongPath, System.Text.StringBuilder lpszShortPath, uint cchBuffer);

        private string GetShortPath(string path)
        {
            try
            {
                // Return original if path is invalid or too short to need conversion
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return path;

                var sb = new System.Text.StringBuilder(255);
                uint result = GetShortPathName(path, sb, (uint)sb.Capacity);

                // If buffer is too small, resize and retry
                if (result > sb.Capacity)
                {
                    sb = new System.Text.StringBuilder((int)result);
                    result = GetShortPathName(path, sb, (uint)sb.Capacity);
                }

                if (result > 0) return sb.ToString();
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to get short path for {path}", ex);
            }
            return path;
        }

        private bool _disposed = false;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Logger.Debug($"[HypnoViewModel] Disposing (Current: {CurrentSource})");

            // Force save position and record play record before disposing
            SaveCurrentPosition();
            RecordCurrentPlayEnd(false);

            if (CurrentItem != null)
            {
                CurrentItem.IsPlaying = false;
                CurrentItem.PropertyChanged -= CurrentItem_PropertyChanged;
            }

            try
            {
                if (Player != null)
                {
                    Player.OpenCompleted -= Player_OpenCompleted;
                    Player.PropertyChanged -= Player_PropertyChanged;

                    Player.Dispose();
                    Logger.Debug("[HypnoViewModel] Player disposed successfully");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Error disposing Player in HypnoViewModel", ex);
            }

            _preBufferCts?.Cancel();
            _preBufferCts?.Dispose();
            _loadCts?.Cancel();
            _loadCts?.Dispose();

            if (_resolutionTimer != null)
            {
                _resolutionTimer.Stop();
                _resolutionTimer = null;
            }
        }
    }

    public class MediaErrorEventArgs : EventArgs
    {
        public string ErrorMessage { get; }
        public VideoItem Item { get; }
        public Exception Exception { get; set; }

        public MediaErrorEventArgs(string errorMessage, VideoItem item = null, Exception ex = null)
        {
            ErrorMessage = errorMessage;
            Item = item;
            Exception = ex;
        }
    }
}

