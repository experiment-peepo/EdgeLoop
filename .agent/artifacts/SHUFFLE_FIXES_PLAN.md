# Shuffle System Remaining Fixes Plan

---

## Issue 1: First Video Has No History Record

### Problem Description

When a new playlist starts:
1. `SetQueue()` is called with the video list
2. `_currentPos` is set to `-1`
3. `PlayNext()` is called
4. `RecordCurrentPlayEnd()` runs but does nothing (no `_currentPlayRecord` exists yet)
5. Smart shuffle selects the first video
6. `StartNewPlayRecord()` creates a record for the selected video
7. Video plays...

**The Issue:** The first video IS correctly tracked. BUT if the user closes the app before the first video ends, `RecordCurrentPlayEnd()` is never called, and the play record is lost (never saved to history).

### Root Cause Analysis

The `_currentPlayRecord` is only saved to `PlayHistoryService` when:
- `PlayNext()` is called (before selecting next video)
- `Stop()` is called

If the app is closed via:
- Window close button
- Panic hotkey
- Task manager

...without calling `Stop()`, the current play record is lost.

### Detailed Fix Plan

#### Step 1: Add Dispose Save in HypnoViewModel

**File:** `GOON/ViewModels/HypnoViewModel.cs`

**Location:** `Dispose()` method

**Change:** Before disposing, save the current play record:

```csharp
public void Dispose() {
    if (_disposed) return;
    _disposed = true;
    
    // CRITICAL: Save current play record before disposing
    RecordCurrentPlayEnd(false);
    
    // ... rest of dispose logic
}
```

#### Step 2: Add Application Exit Handler in App.xaml.cs

**File:** `GOON/App.xaml.cs`

**Location:** `OnStartup()` method

**Change:** Register for application exit to ensure history is saved:

```csharp
protected override void OnStartup(StartupEventArgs e) {
    // ... existing code ...
    
    // Ensure play history is saved on exit
    this.Exit += (s, args) => {
        if (ServiceContainer.TryGet<PlayHistoryService>(out var historyService)) {
            historyService.SaveHistory();
        }
    };
    
    base.OnStartup(e);
}
```

#### Step 3: Add Window Closing Handler in HypnoWindow

**File:** `GOON/Windows/HypnoWindow.xaml.cs`

**Location:** Window constructor or Loaded event

**Change:** Ensure ViewModel dispose is called on window close:

```csharp
this.Closing += (s, e) => {
    ViewModel?.Dispose();
};
```

### Testing Verification

1. Start app with a playlist
2. Let first video play for 10 seconds
3. Close the app (don't skip)
4. Check `Data/play_history.json` - should contain a record with:
   - FilePath matching first video
   - WatchDurationMs > 0
   - WasSkipped = false (or true if < 30%)

---

## Issue 5: Multi-Monitor Sync Group Issues

### Problem Description

When "All Monitors" mode is used:
1. Same playlist is assigned to multiple HypnoViewModels
2. Each ViewModel has its own `_currentPos`
3. Each ViewModel independently calls `SelectBestVideo()`
4. Because of random noise in scoring, each might pick DIFFERENT videos
5. `VideoPlayerService.MasterSyncTimer_Tick` has logic to sync indices (lines 67-87)
6. But this happens AFTER the wrong video has already started loading

**Result:** Momentary desync where monitors briefly show different videos before being corrected.

### Root Cause Analysis

The `SyncGroupId` property in HypnoViewModel identifies which ViewModels should play the same content. The existing sync logic in `VideoPlayerService` handles this:

```csharp
// VideoPlayerService.cs lines 67-87
var coordinatedGroups = playersSnapshot
    .Where(p => !string.IsNullOrEmpty(p.ViewModel.SyncGroupId))
    .GroupBy(p => p.ViewModel.SyncGroupId);

foreach (var coordinatedGroup in coordinatedGroups) {
    var playerList = coordinatedGroup.ToList();
    if (playerList.Count <= 1) continue;

    var master = playerList[0];
    int masterIndex = master.ViewModel.CurrentIndex;
    
    if (masterIndex >= 0) {
        foreach (var follower in playerList.Skip(1)) {
            if (follower.ViewModel.CurrentIndex != masterIndex) {
                Logger.Info($"[Sync] Coordinated player diverged. Correcting.");
                follower.ViewModel.JumpToIndex(masterIndex);
            }
        }
    }
}
```

**Problem:** This correction happens after `PlayNext()` has already been called on all ViewModels.

### Detailed Fix Plan

#### Option A: Master-Only Shuffle (Recommended)

**Concept:** Only the "master" ViewModel in a sync group should call `SelectBestVideo()`. Followers should wait and copy the master's selection.

**Implementation:**

##### Step A1: Add IsMaster Property to HypnoViewModel

**File:** `GOON/ViewModels/HypnoViewModel.cs`

```csharp
/// <summary>
/// If true, this ViewModel is the master for its sync group and decides which video to play.
/// Followers copy the master's selection instead of independently shuffling.
/// </summary>
public bool IsSyncMaster { get; set; } = false;
```

##### Step A2: Modify PlayNext to Check Master Status

**File:** `GOON/ViewModels/HypnoViewModel.cs`

**Location:** `PlayNext()` method, at the start of the shuffle logic

```csharp
if (IsShuffle && _files.Length > 1) {
    // If we're a follower in a sync group, don't shuffle - wait for master
    if (!string.IsNullOrEmpty(SyncGroupId) && !IsSyncMaster) {
        Logger.Debug($"[Shuffle] Sync follower waiting for master to decide index.");
        return; // Master will call JumpToIndex on us
    }
    
    // Normal shuffle logic for master or non-synced players
    if (App.Settings.CurrentShuffleMode == ShuffleMode.Smart ...) {
        // ...
    }
}
```

##### Step A3: Modify Master to Broadcast Selection

**File:** `GOON/ViewModels/HypnoViewModel.cs`

**Location:** After selecting the next video in `PlayNext()`

```csharp
// After: _currentPos = engine.SelectBestVideo(...);
// Add notification for sync groups:
if (!string.IsNullOrEmpty(SyncGroupId) && IsSyncMaster) {
    // Notify VideoPlayerService to update followers
    if (ServiceContainer.TryGet<VideoPlayerService>(out var vps)) {
        vps.BroadcastIndexToGroup(SyncGroupId, _currentPos);
    }
}
```

##### Step A4: Add Broadcast Method to VideoPlayerService

**File:** `GOON/Classes/VideoPlayerService.cs`

```csharp
public void BroadcastIndexToGroup(string syncGroupId, int index) {
    var players = _players.Values.ToList();
    var groupMembers = players
        .Where(p => p.ViewModel.SyncGroupId == syncGroupId)
        .ToList();
    
    foreach (var member in groupMembers) {
        if (!member.ViewModel.IsSyncMaster && member.ViewModel.CurrentIndex != index) {
            Logger.Info($"[Sync] Broadcasting index {index} to follower at {member.ScreenDeviceName}");
            member.ViewModel.JumpToIndex(index);
        }
    }
}
```

##### Step A5: Set IsSyncMaster When Creating ViewModels

**File:** Where HypnoViewModels are created for sync groups (likely `LauncherViewModel.cs` or window creation code)

Find where multiple HypnoViewModels are created for "All Monitors" mode and set the first one as master:

```csharp
bool isFirst = true;
foreach (var screen in screens) {
    var vm = new HypnoViewModel(...);
    vm.SyncGroupId = "all-monitors-group";
    vm.IsSyncMaster = isFirst;
    isFirst = false;
    // ...
}
```

#### Option B: Pre-Sync Before PlayNext (Alternative)

**Concept:** Before any ViewModel calls `SelectBestVideo()`, coordinate to ensure only one call is made.

This is more complex and requires a central coordinator. Option A is simpler.

### Testing Verification

1. Set up 2+ monitors
2. Create playlist with 5+ videos
3. Enable "All Monitors" mode
4. Start playback
5. Watch 2-3 video transitions
6. Verify: All monitors transition to the SAME video at the SAME time
7. Check logs: Should see "Broadcasting index X to follower" messages

---

## Implementation Order

| Step | Effort | Risk | Priority |
|------|--------|------|----------|
| Issue 1 Step 1: Dispose save | 5 min | Low | P0 |
| Issue 1 Step 2: App exit handler | 5 min | Low | P0 |
| Issue 1 Step 3: Window closing | 5 min | Low | P0 |
| Issue 5 Step A1: IsMaster property | 2 min | Low | P1 |
| Issue 5 Step A2: PlayNext check | 5 min | Medium | P1 |
| Issue 5 Step A3: Master broadcast | 5 min | Medium | P1 |
| Issue 5 Step A4: Service method | 10 min | Medium | P1 |
| Issue 5 Step A5: Set IsMaster | 10 min | Medium | P1 |

**Total Estimated Time:** ~45 minutes

---

## Files to Modify

1. `GOON/ViewModels/HypnoViewModel.cs`
   - Add `IsSyncMaster` property
   - Modify `PlayNext()` for follower check
   - Add broadcast after selection
   - Ensure `Dispose()` saves record

2. `GOON/App.xaml.cs`
   - Add Exit event handler

3. `GOON/Windows/HypnoWindow.xaml.cs`
   - Add Closing event handler

4. `GOON/Classes/VideoPlayerService.cs`
   - Add `BroadcastIndexToGroup()` method

5. `GOON/ViewModels/LauncherViewModel.cs` (or equivalent)
   - Set `IsSyncMaster` on first ViewModel in sync groups

---

*Document Version: 1.0*
*Created: 2026-01-20*
