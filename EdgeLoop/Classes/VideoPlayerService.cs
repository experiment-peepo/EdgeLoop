using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Controls;
using EdgeLoop.Windows;
using EdgeLoop.ViewModels;
using System.Diagnostics;

namespace EdgeLoop.Classes
{
    /// <summary>
    /// Service for managing video playback across multiple screens
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public class VideoPlayerService : IVideoPlayerService
    {
        readonly List<HypnoWindow> players = new List<HypnoWindow>();
        private readonly object _playersLock = new object();
        public System.Collections.ObjectModel.ObservableCollection<ActivePlayerViewModel> ActivePlayers { get; } = new System.Collections.ObjectModel.ObservableCollection<ActivePlayerViewModel>();
        private readonly LruCache<string, bool> _fileExistenceCache;
        private System.Windows.Threading.DispatcherTimer _masterSyncTimer;
        private readonly SharedClock _sharedClock = new SharedClock();
        private bool _userPaused = false; // Tracks if user manually paused (prevents auto-resume)
        private readonly HashSet<VideoItem> _lastHighlightedItems = new HashSet<VideoItem>();

        /// <summary>
        /// Event raised when a media error occurs during playback
        /// </summary>
        public event EventHandler<MediaErrorEventArgs> MediaErrorOccurred;

        /// <summary>
        /// Event raised when a video opens successfully
        /// </summary>
        public event EventHandler MediaOpened;

        public bool HasActivePlayers => players.Count > 0;
        public IReadOnlyList<HypnoViewModel> ActiveViewModels
        {
            get
            {
                lock (_playersLock)
                {
                    return players
                        .Where(p => p.ViewModel != null)
                        .SelectMany(p => p.ViewModel is GroupHypnoViewModel ghvm ? ghvm.Children : new[] { p.ViewModel })
                        .ToList().AsReadOnly();
                }
            }
        }

        public VideoPlayerService()
        {
            var ttl = TimeSpan.FromMinutes(Constants.CacheTtlMinutes);
            _fileExistenceCache = new LruCache<string, bool>(Constants.MaxFileCacheSize, ttl);

            _masterSyncTimer = new System.Windows.Threading.DispatcherTimer();
            _masterSyncTimer.Interval = TimeSpan.FromMilliseconds(100);
            _masterSyncTimer.Tick += MasterSyncTimer_Tick;
        }

        public void BroadcastIndexToGroup(string syncGroupId, int index, bool force = false)
        {
            lock (_playersLock)
            {
                foreach (var player in players)
                {
                    if (player.ViewModel != null && player.ViewModel.SyncGroupId == syncGroupId && !player.ViewModel.IsSyncMaster)
                    {
                        // FIX: Force jump if it's a manual skip OR if the follower is at the end of its current video, even if index is the same
                        if (force || player.ViewModel.CurrentIndex != index || (player.ViewModel.Player.Status == FlyleafLib.MediaPlayer.Status.Ended && !player.ViewModel.IsLoading))
                        {
                            Logger.Debug($"[Sync] Broadcasting index {index} to group '{syncGroupId}' follower at {player.ScreenDeviceName} (Force: {force}, EndReached: {player.ViewModel.Player.Status == FlyleafLib.MediaPlayer.Status.Ended})");
                            player.ViewModel.JumpToIndex(index, force);
                        }
                    }
                }
            }
        }

        private DateTime _lastSessionSave = DateTime.MinValue;
        private DateTime _lastSyncStallLog = DateTime.MinValue;

        private void MasterSyncTimer_Tick(object sender, EventArgs e)
        {
            // ... (Existing sync logic) ...

            // Auto-save session state every 30 seconds
            if ((DateTime.Now - _lastSessionSave).TotalSeconds > 30)
            {
                SaveSessionState();
                _lastSessionSave = DateTime.Now;
            }

            UpdateHighlights();

            List<HypnoWindow> playersSnapshot;
            lock (_playersLock)
            {
                if (players.Count == 0) return;
                playersSnapshot = players.ToList();
            }

            // Group players to sync them. 
            // 1. Prefer SyncGroupId for coordinated groups (All Monitors mode)
            // 2. Fall back to CurrentSource for independent players playing the same file
            // 3. Skip players with no source AND no SyncGroupId
            var groups = playersSnapshot
                .Where(p => p.ViewModel != null)
                .GroupBy(p => !string.IsNullOrEmpty(p.ViewModel.SyncGroupId)
                    ? "SyncGroup_" + p.ViewModel.SyncGroupId
                    : (p.ViewModel.CurrentSource?.ToString() ?? "Independent_" + Guid.NewGuid()));

            // --- 0. Coordinated Group Index Sync ---
            // Ensure all players with the same SyncGroupId are playing the same video index.
            // This fixes divergence when Shuffle is on for "All Monitors" mode.
            var coordinatedGroups = playersSnapshot
                .Where(p => !string.IsNullOrEmpty(p.ViewModel.SyncGroupId))
                .GroupBy(p => p.ViewModel.SyncGroupId);

            foreach (var coordinatedGroup in coordinatedGroups)
            {
                var playerList = coordinatedGroup.ToList();
                if (playerList.Count <= 1) continue;

                var master = playerList[0];
                int masterIndex = master.ViewModel.CurrentIndex;

                // Only sync if the master has actually picked a valid video index
                if (masterIndex >= 0)
                {
                    foreach (var follower in playerList.Skip(1))
                    {
                        if (follower.ViewModel.CurrentIndex != masterIndex || (follower.ViewModel.Player.Status == FlyleafLib.MediaPlayer.Status.Ended && !follower.ViewModel.IsLoading))
                        {
                            Logger.Debug($"[Sync] Coordinated player at {follower.ScreenDeviceName} diverged in group '{coordinatedGroup.Key}' (Index {follower.ViewModel.CurrentIndex} vs Master {masterIndex}, Status {follower.ViewModel.Player.Status}). Correcting.");
                            follower.ViewModel.JumpToIndex(masterIndex, true);
                        }
                    }
                }
            }

            foreach (var group in groups)
            {
                var playerList = group.ToList();
                if (playerList.Count == 0) continue;

                // --- 1. Ready Check Phase ---
                // Flyleaf handles its own ready state, but we can check Status
                var allReady = playerList.All(p => p.ViewModel.Player.Status == FlyleafLib.MediaPlayer.Status.Playing || p.ViewModel.Player.Status == FlyleafLib.MediaPlayer.Status.Opening);

                if (allReady)
                {
                    // Triggers playback for any players that are waiting (Flyleaf usually plays on open if configured)
                    var waitingPlayers = playerList.Where(p => p.ViewModel.Player.Status == FlyleafLib.MediaPlayer.Status.Opening).ToList();
                    foreach (var p in waitingPlayers)
                    {
                        // No explicit action needed for Coordinated Start yet, will be handled by Clock in Phase 4
                    }
                }

                // --- 2. Synchronization Phase ---
                // We sync groups of 2+ players, OR single players using ExternalClock (to handle AutoPlay=false start)
                if (playerList.Count < 2 && !playerList.Any(p => p.ViewModel.ExternalClock != null)) continue;

                var master = playerList.FirstOrDefault(p => p.ViewModel.IsSyncMaster) ?? playerList.FirstOrDefault();
                int masterIndex = master?.ViewModel.CurrentIndex ?? -1;
                bool masterReady = master?.ViewModel.IsReady ?? false;

                // --- 2a. Flyleaf External Clock Sync (Coordinated Hold/Release) ---
                if (playerList.Any(p => p.ViewModel.ExternalClock != null))
                {
                    var clock = _sharedClock;

                    // A group is "loading" if ANY player is loading, opening, OR if they haven't all reached the same video index yet
                    bool anyLoading = playerList.Any(p =>
                        p.ViewModel.IsLoading ||
                        p.ViewModel.Player.Status == FlyleafLib.MediaPlayer.Status.Opening ||
                        (masterIndex >= 0 && p.ViewModel.CurrentIndex != masterIndex));

                    // A player is "buffered enough" if it's Playing/Ready, NOT loading/buffering/opening, AND at the correct index
                    bool allBuffered = playerList.All(p =>
                        (p.ViewModel.IsReady || p.ViewModel.Player.Status == FlyleafLib.MediaPlayer.Status.Playing) &&
                        !p.ViewModel.IsLoading &&
                        !p.ViewModel.Player.IsBuffering &&
                        p.ViewModel.Player.Status != FlyleafLib.MediaPlayer.Status.Opening &&
                        (masterIndex < 0 || p.ViewModel.CurrentIndex == masterIndex));

                    bool anyBuffering = playerList.Any(p => p.ViewModel.IsLoading || p.ViewModel.Player.IsBuffering || p.ViewModel.Player.Status == FlyleafLib.MediaPlayer.Status.Opening);

                    // HOLD MECHANISM: Reset or Hold the clock if anyone is loading or transitioning.
                    // We must do this to ensure all monitors start at the exact same point for the NEW video.
                    if (anyLoading && !_userPaused)
                    {
                        // We reset only if we aren't already at 0/ResumePos (to avoid log spam)
                        // OR if the clock is currently running (transitioning from a previous video).
                        bool needsReset = clock.IsRunning || (clock.Ticks > 0 && !masterReady);

                        if (needsReset)
                        {
                            Logger.Debug($"[Sync] Group '{group.Key}' is transitioning/loading (Master Index: {masterIndex}). Resetting SharedClock to 0/Hold.");
                            clock.Reset();
                        }
                        else if (clock.Ticks == 0 && !clock.IsRunning)
                        {
                            // Already held at 0, no action needed
                        }
                    }

                    if (clock.IsRunning)
                    {
                        if (anyBuffering)
                        {
                            var stalledPlayers = playerList
                                .Where(p => p.ViewModel.IsLoading || p.ViewModel.Player.IsBuffering || p.ViewModel.Player.Status == FlyleafLib.MediaPlayer.Status.Opening)
                                .Select(p => $"{p.ScreenDeviceName} ({(p.ViewModel.IsLoading ? "Loading" : (p.ViewModel.Player.Status == FlyleafLib.MediaPlayer.Status.Opening ? "Opening" : "Buffering"))} @ {p.ViewModel.Player.CurTime / 10000}ms)");

                            Logger.Debug($"[Sync] Stalled! Pausing SharedClock at {clock.Ticks / 10000}ms. Waiting for: [{string.Join(", ", stalledPlayers)}]");
                            clock.Pause();
                        }
                        else
                        {
                            // Note: Auto-resume was removed here to allow individual monitors to remain paused 
                            // while others continue playing. Synchronization is maintained via the ExternalClock.
                        }
                    }
                    else if (allBuffered && playerList.Any() && !_userPaused)
                    {
                        // If everyone is ready/buffered and the clock is stopped AND user didn't pause, START IT.
                        // This handles the "Auto-play after skip" requirement.
                        // CRITICAL: Skip this if user manually paused - respect their intent!
                        Logger.Debug($"[Sync] All players ready/buffered. Starting/Resuming SharedClock. (Ticks: {clock.Ticks})");
                        clock.Start();
                        foreach (var p in playerList)
                        {
                            if (p.ViewModel.MediaState == MediaState.Play &&
                                p.ViewModel.Player.Status != FlyleafLib.MediaPlayer.Status.Playing &&
                                p.ViewModel.Player.Status != FlyleafLib.MediaPlayer.Status.Opening)
                            {
                                Logger.Debug($"[Sync] Requesting Play for stalled monitor: {p.ScreenDeviceName}");
                                p.ViewModel.Play();
                            }
                        }
                    }
                    else if (!clock.IsRunning)
                    {
                        // Throttled logging to avoid flooding the log file
                        if ((DateTime.Now - _lastSyncStallLog).TotalSeconds > 5)
                        {
                            var states = string.Join(", ", playerList.Select(p =>
                                $"{p.ScreenDeviceName}: Ready={p.ViewModel.IsReady}, Sts={p.ViewModel.Player.Status}, Buf={p.ViewModel.Player.IsBuffering}"));
                            Logger.Debug($"[Sync-Stall] Clock Stopped. Waiting for: {states}");
                            _lastSyncStallLog = DateTime.Now;
                        }
                    }

                    // Sync speed across all players in the group
                    foreach (var p in playerList)
                    {
                        if (Math.Abs(p.ViewModel.SpeedRatio - clock.Speed) > 0.01)
                        {
                            Logger.Debug($"[Sync] Syncing speed for {p.ScreenDeviceName} to {clock.Speed}");
                            p.ViewModel.SpeedRatio = clock.Speed;
                        }
                    }

                    var primary = playerList.FirstOrDefault();
                    foreach (var p in playerList)
                    {
                        if (p != primary && p.ViewModel.Volume > 0)
                        {
                            Logger.Debug($"[Sync] Muting follower on {p.ScreenDeviceName}");
                            p.ViewModel.Volume = 0;
                        }
                    }
                    continue; // Skip legacy drift correction
                }
            }
        }

        /// <summary>
        /// Called by HypnoWindow when a media error occurs
        /// </summary>
        internal void OnMediaError(MediaErrorEventArgs e)
        {
            MediaErrorOccurred?.Invoke(this, e);
        }

        /// <summary>
        /// Called by HypnoWindow when a media opens successfully
        /// </summary>
        internal void OnMediaOpened()
        {
            UpdateHighlights();
            MediaOpened?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Gets whether any videos are currently playing
        /// </summary>
        public bool IsPlaying
        {
            get
            {
                lock (_playersLock)
                {
                    return players.Count > 0;
                }
            }
        }

        /// <summary>
        /// Plays videos on the specified screens
        /// </summary>
        /// <param name="files">Video files to play</param>
        /// <param name="screens">Screens to play on</param>
        public async System.Threading.Tasks.Task PlayOnScreensAsync(IEnumerable<VideoItem> files, IEnumerable<ScreenViewer> screens)
        {
            StopAll();
            var queue = await NormalizeItemsAsync(files, CancellationToken.None);
            var allScreens = Screen.AllScreens;

            foreach (var sv in (screens ?? Enumerable.Empty<ScreenViewer>()).OrderBy(s => s.ScreenIndex))
            {
                // Validate screen still exists
                if (sv?.Screen == null) continue;
                bool screenExists = allScreens.Any(s => s.DeviceName == sv.Screen.DeviceName);

                if (!screenExists)
                {
                    Logger.Warning($"Screen {sv.DeviceName} is no longer available, skipping");
                    continue;
                }

                var w = new HypnoWindow(sv.Screen);
                w.Show();

                w.ViewModel.SetQueue(queue);

                lock (_playersLock)
                {
                    players.Add(w);
                }
                ActivePlayers.Add(new ActivePlayerViewModel(sv.ToString(), w.ViewModel));
            }

            if (this.IsPlaying)
            {
                PowerManagement.SuppressSleep();
                _masterSyncTimer.Start();
            }
        }

        /// <summary>
        /// Pauses the shared sync clock. Call this when a user manually pauses a player.
        /// This prevents the sync logic from auto-resuming the paused player.
        /// </summary>
        public void PauseSyncClock()
        {
            _userPaused = true;  // Mark that user intentionally paused
            if (_sharedClock.IsRunning)
            {
                Logger.Debug("[Sync] User paused. Pausing SharedClock.");
                _sharedClock.Pause();
            }
        }

        /// <summary>
        /// Resumes the shared sync clock. Call this when a user manually plays a player.
        /// </summary>
        public void ResumeSyncClock()
        {
            _userPaused = false;  // User wants to play, clear the pause flag
            if (!_sharedClock.IsRunning)
            {
                Logger.Debug("[Sync] User played. Resuming SharedClock.");
                _sharedClock.Start();
            }
        }

        /// <summary>
        /// Pauses all currently playing videos
        /// </summary>
        public void PauseAll()
        {
            _sharedClock.Pause();
            List<HypnoWindow> playersSnapshot;
            lock (_playersLock)
            {
                playersSnapshot = players.ToList();
            }
            foreach (var w in playersSnapshot) w.ViewModel.Pause();
        }

        /// <summary>
        /// Resumes all paused videos
        /// </summary>
        public void ContinueAll()
        {
            _sharedClock.Start();
            List<HypnoWindow> playersSnapshot;
            lock (_playersLock)
            {
                playersSnapshot = players.ToList();
            }
            foreach (var w in playersSnapshot) w.ViewModel.Play();
        }

        /// <summary>
        /// Stops and disposes all video players
        /// </summary>
        public void StopAll()
        {
            _masterSyncTimer?.Stop();
            _sharedClock.Pause();
            _sharedClock.Reset();
            PowerManagement.AllowSleep();

            // SESSION RESUME: Immediate save of positions when session ends
            PlaybackPositionTracker.Instance.SaveSync();

            // Unregister all screen hotkeys
            ActivePlayers.Clear();

            List<HypnoWindow> playersCopy;
            lock (_playersLock)
            {
                playersCopy = players.ToList();
                players.Clear();
            }

            UpdateHighlights();

            foreach (var w in playersCopy)
            {
                try
                {
                    // Critical: Close should be on UI thread or at least handle it safely
                    if (System.Windows.Application.Current?.Dispatcher.CheckAccess() == true)
                    {
                        w.Close();
                    }
                    else
                    {
                        System.Windows.Application.Current?.Dispatcher.Invoke(() => w.Close());
                    }

                    if (w is IDisposable disposable)
                    {
                        disposable.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning("Error disposing window", ex);
                }
            }
            _masterSyncTimer.Stop();
        }

        /// <summary>
        /// Unregisters a single player when it's closed or failed
        /// </summary>
        public void UnregisterPlayer(HypnoWindow player)
        {
            if (player == null) return;

            lock (_playersLock)
            {
                if (players.Remove(player))
                {
                    Logger.Debug($"[VideoPlayerService] Unregistered player for screen: {player.ScreenDeviceName}");
                }

                // Find and remove from ActivePlayers collection
                var vm = ActivePlayers.FirstOrDefault(ap => ap.Player == player.ViewModel);
                if (vm != null)
                {
                    ActivePlayers.Remove(vm);
                }

                UpdateHighlights();

                if (players.Count == 0)
                {
                    _masterSyncTimer.Stop();
                    PowerManagement.AllowSleep();
                    Logger.Debug("[VideoPlayerService] Last player removed, stopped sync timer.");
                }
            }
        }



        /// <summary>
        /// Sets the volume for all video players
        /// </summary>
        /// <param name="volume">Volume level (0.0 to 1.0)</param>
        public void SetVolumeAll(double volume)
        {
            List<HypnoWindow> playersSnapshot;
            lock (_playersLock)
            {
                playersSnapshot = players.ToList();
            }
            foreach (var w in playersSnapshot) w.ViewModel.Volume = volume;
        }

        /// <summary>
        /// Sets the opacity for all video players
        /// </summary>
        /// <param name="opacity">Opacity level (0.0 to 1.0)</param>
        public void SetOpacityAll(double opacity)
        {
            List<HypnoWindow> playersSnapshot;
            lock (_playersLock)
            {
                playersSnapshot = players.ToList();
            }
            foreach (var w in playersSnapshot) w.ViewModel.Opacity = opacity;
        }

        /// <summary>
        /// Refreshes the opacity of all players (useful when AlwaysOpaque setting changes)
        /// </summary>
        public void RefreshAllOpacities()
        {
            List<HypnoWindow> playersSnapshot;
            lock (_playersLock)
            {
                playersSnapshot = players.ToList();
            }
            foreach (var w in playersSnapshot)
            {
                w.ViewModel.RefreshOpacity();
            }
        }

        /// <summary>
        /// Refreshes the Super Resolution setting of all players
        /// </summary>
        public void RefreshAllSuperResolution()
        {
            List<HypnoWindow> playersSnapshot;
            lock (_playersLock)
            {
                playersSnapshot = players.ToList();
            }
            foreach (var w in playersSnapshot)
            {
                w.ViewModel.RefreshSuperResolution();
            }
        }

        /// <summary>
        /// Applies the prevent minimize setting to all windows
        /// </summary>
        public void ApplyPreventMinimizeSetting()
        {
            // Settings are applied via StateChanged event handler in HypnoWindow
            // No action needed here as the setting is checked in real-time
        }

        /// <summary>
        /// Plays videos on specific monitors with per-monitor assignments
        /// </summary>
        /// <param name="assignments">Dictionary mapping screens to their video playlists</param>
        public async Task PlayPerMonitorAsync(IDictionary<ScreenViewer, IEnumerable<VideoItem>> assignments, bool showGroupControl = true, PlaybackState resumeState = null, CancellationToken cancellationToken = default)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                StopAll();

                if (assignments == null) return;
                var allScreens = Screen.AllScreens;

                int sharedCoordinatedIndex = -1;

                foreach (var kvp in assignments.OrderBy(x => x.Key.ScreenIndex))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var sv = kvp.Key;

                    // Validate screen still exists
                    if (sv?.Screen == null)
                    {
                        Logger.Warning("Screen viewer has null screen, skipping");
                        continue;
                    }

                    bool screenExists = allScreens.Any(s => s.DeviceName == sv.Screen.DeviceName);
                    if (!screenExists)
                    {
                        Logger.Warning($"Screen {sv.DeviceName} is no longer available, skipping");
                        continue;
                    }

                    var queue = await NormalizeItemsAsync(kvp.Value, cancellationToken);
                    if (!queue.Any()) continue;

                    // 0. Screen and Visibility
                    var w = new HypnoWindow(sv.Screen);
                    w.Show();

                    // 1. Group Identity Phase (BEFORE SetQueue)
                    w.ViewModel.UseCoordinatedStart = true;
                    if (showGroupControl)
                    {
                        w.ViewModel.SyncGroupId = "AllMonitors";
                    }

                    if (!string.IsNullOrEmpty(w.ViewModel.SyncGroupId))
                    {
                        if (sharedCoordinatedIndex == -1)
                        {
                            // First player in the group picks the random starting point
                            w.ViewModel.IsSyncMaster = true;
                            sharedCoordinatedIndex = -2; // Mark as "Master Assigned" to prevent others from claiming mastery
                        }
                        else if (sharedCoordinatedIndex >= 0)
                        {
                            // Subsequent players follow the master immediately if master already selected an index
                            w.ViewModel.JumpToIndex(sharedCoordinatedIndex, true);
                            w.ViewModel.Volume = 0; // Mute followers
                        }
                        else if (sharedCoordinatedIndex == -2)
                        {
                            // Master is assigned but hasn't picked an index yet. 
                            // It will pick one during SetQueue() and we will update sharedCoordinatedIndex after.
                            w.ViewModel.Volume = 0; // Mute followers
                        }
                    }

                    // 2. Queue Assignment Phase
                    // Pass the shared coordinated index if we are in a group and it's already known
                    int startIndex = (!string.IsNullOrEmpty(w.ViewModel.SyncGroupId) && sharedCoordinatedIndex >= 0)
                                     ? sharedCoordinatedIndex
                                     : -1;

                    w.ViewModel.SetQueue(queue, startIndex);

                    if (!string.IsNullOrEmpty(w.ViewModel.SyncGroupId))
                    {
                        w.ViewModel.ExternalClock = _sharedClock;
                    }
                    else
                    {
                        w.ViewModel.ExternalClock = null;
                    }

                    // 3. Coordination Capture
                    if (!string.IsNullOrEmpty(w.ViewModel.SyncGroupId) && sharedCoordinatedIndex == -2)
                    {
                        sharedCoordinatedIndex = w.ViewModel.CurrentIndex;
                        Logger.Debug($"[Sync] Captured master index {sharedCoordinatedIndex} for group coordination.");

                        // Force broadcast now that we have followers potentially waiting (though players list is still empty)
                        // The loop below handles actual synchronization for players already in the list
                    }

                    // Apply Restore State if available
                    if (resumeState != null)
                    {
                        w.ViewModel.RestoreState(resumeState.CurrentIndex, resumeState.PositionTicks);
                        // Also restore speed if needed
                        if (resumeState.SpeedRatio != 1.0) w.ViewModel.SpeedRatio = resumeState.SpeedRatio;
                    }

                    lock (_playersLock)
                    {
                        players.Add(w);
                    }

                    // If put into group control, don't add individual controls
                    if (!sv.IsAllScreens && !showGroupControl)
                    {
                        ActivePlayers.Add(new ActivePlayerViewModel(sv.ToString(), w.ViewModel));
                    }

                    // Stagger window creation slightly (reduced for Flyleaf)
                    await System.Threading.Tasks.Task.Delay(100, cancellationToken);
                }

                // Consolidate "All Screens" players into a single control
                if (showGroupControl)
                {
                    List<HypnoWindow> playersSnapshot;
                    lock (_playersLock)
                    {
                        playersSnapshot = players.ToList();
                    }
                    var allScreensPlayers = playersSnapshot.Where(p => p.ViewModel.UseCoordinatedStart).ToList();
                    if (allScreensPlayers.Any())
                    {
                        var groupVm = new GroupHypnoViewModel(allScreensPlayers.Select(p => p.ViewModel));
                        ActivePlayers.Add(new ActivePlayerViewModel("All Monitors", groupVm));
                    }
                }

                if (this.IsPlaying)
                {
                    PowerManagement.SuppressSleep();
                    _masterSyncTimer.Start();
                }
            }
            catch (OperationCanceledException)
            {
                Logger.Debug("PlayPerMonitorAsync was cancelled.");
                StopAll();
            }
            catch (Exception ex)
            {
                Logger.Error("Error in PlayPerMonitorAsync", ex);
                StopAll();
            }
        }

        private async Task<IEnumerable<VideoItem>> NormalizeItemsAsync(IEnumerable<VideoItem> files, CancellationToken cancellationToken)
        {
            var list = new List<VideoItem>();
            foreach (var f in files ?? Enumerable.Empty<VideoItem>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (f.IsUrl)
                {
                    // For URLs, just validate and add (no file existence check)
                    if (FileValidator.ValidateVideoUrl(f.FilePath, out _))
                    {
                        list.Add(f);
                    }
                }
                else if (Path.IsPathRooted(f.FilePath))
                {
                    // For local files, check existence
                    if (await CheckFileExists(f.FilePath).ConfigureAwait(false))
                    {
                        list.Add(f);
                    }
                }
            }
            return list;
        }

        private async Task<bool> CheckFileExists(string filePath)
        {
            // Use cache to avoid repeated disk I/O
            if (_fileExistenceCache.TryGetValue(filePath, out bool exists))
            {
                return exists;
            }

            // Retry logic with exponential backoff
            exists = await CheckFileExistsWithRetry(filePath);
            _fileExistenceCache.Set(filePath, exists);
            return exists;
        }

        private async Task<bool> CheckFileExistsWithRetry(string filePath)
        {
            int attempt = 0;
            while (attempt < Constants.MaxRetryAttempts)
            {
                try
                {
                    return File.Exists(filePath);
                }
                catch (Exception ex)
                {
                    attempt++;
                    if (attempt >= Constants.MaxRetryAttempts)
                    {
                        Logger.Warning($"Failed to check file existence after {Constants.MaxRetryAttempts} attempts: {filePath}", ex);
                        return false;
                    }

                    // Exponential backoff with async delay
                    int delay = Constants.RetryBaseDelayMs * (int)Math.Pow(2, attempt - 1);
                    await Task.Delay(delay);
                }
            }
            return false;
        }

        private void SaveSessionState()
        {
            // Updated to use async save to prevent UI stutter (fire-and-forget)
            // No await here because this is called from a timer tick (void)
            try
            {
                if (App.Settings == null) return;

                List<HypnoWindow> playersSnapshot;
                lock (_playersLock)
                {
                    playersSnapshot = players.ToList();
                }

                if (playersSnapshot.Count == 0) return;

                // Grab the first active player to save as "Master" persistence
                var master = playersSnapshot.FirstOrDefault();
                if (master?.ViewModel == null) return;

                var (index, ticks, speed, paths) = master.ViewModel.GetPlaybackState();

                // Save only the lightweight state to settings.json
                var state = App.Settings.LastPlaybackState ?? new PlaybackState();
                state.CurrentIndex = index;
                state.PositionTicks = ticks;
                state.SpeedRatio = speed;
                state.LastPlayed = DateTime.Now;

                App.Settings.LastPlaybackState = state;

                // ASYNC SAVE: Fire and forget task to avoid blocking the UI thread
                // Catch exceptions inside the task to avoid unobserved task exceptions
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    try
                    {
                        await App.Settings.SaveAsync();
                    }
                    catch (Exception innerEx)
                    {
                        Logger.Warning("Failed to auto-save session (async)", innerEx);
                    }
                });

            }
            catch (Exception ex)
            {
                Logger.Warning("Failed to initiate auto-save session", ex);
            }
        }

        public void ClearFileExistenceCache()
        {
            _fileExistenceCache.Clear();
        }

        /// <summary>
        /// Removes specific items from all active player queues.
        /// This ensures that if a video is removed from the playlist, it stops playing or is skipped.
        /// </summary>
        public void RemoveItemsFromAllPlayers(IEnumerable<VideoItem> items)
        {
            if (items == null || !items.Any()) return;

            // Immediately clear highlights for these items on UI thread
            System.Windows.Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
            {
                foreach (var item in items)
                {
                    if (item == null) continue;
                    item.IsPlaying = false;
                    _lastHighlightedItems.Remove(item);
                }
            }));

            List<HypnoWindow> playersSnapshot;
            lock (_playersLock)
            {
                playersSnapshot = players.ToList();
            }

            foreach (var w in playersSnapshot)
            {
                w.ViewModel?.RemoveItems(items);
            }
        }

        /// <summary>
        /// Updates the IsPlaying highlight on VideoItems based on all active players.
        /// This handles cases where multiple monitors are playing the same or different videos.
        /// </summary>
        public void UpdateHighlights()
        {
            var currentPlaying = new HashSet<VideoItem>();
            lock (_playersLock)
            {
                foreach (var p in players)
                {
                    if (p.ViewModel?.CurrentItem != null)
                    {
                        currentPlaying.Add(p.ViewModel.CurrentItem);
                    }
                }
            }

            // Always update on UI thread
            System.Windows.Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
            {
                // Remove highlight from items no longer playing
                foreach (var item in _lastHighlightedItems.ToList())
                {
                    if (item == null)
                    {
                        _lastHighlightedItems.Remove(item);
                        continue;
                    }

                    if (!currentPlaying.Contains(item))
                    {
                        item.IsPlaying = false;
                        _lastHighlightedItems.Remove(item);
                    }
                }

                // Add highlight to items now playing
                foreach (var item in currentPlaying)
                {
                    if (item == null) continue;

                    if (!item.IsPlaying)
                    {
                        item.IsPlaying = true;
                    }
                    _lastHighlightedItems.Add(item);
                }
            }));
        }
    }
}

