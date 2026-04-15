# FlyleafLib Fork Changes Documentation

> **Base Version**: Flyleaf v3.9.x (forked ~Nov 2025)  
> **Last Upstream Sync**: December 2025  
> **Purpose**: Custom modifications to support GOON application's unique requirements

---

## 🎯 Summary of Custom Features

This fork adds two major capabilities not present in upstream Flyleaf:

| Feature | Purpose | Complexity |
|---------|---------|------------|
| **External Clock Synchronization** | Multi-monitor frame-exact sync | High |
| **D3DImage Transparency Support** | WPF overlay windows without airspace issues | High |

---

## 📁 Modified Files

### 1. External Clock Synchronization

#### `FlyleafLib/MediaPlayer/IClock.cs` (NEW FILE)
```csharp
namespace FlyleafLib.MediaPlayer;

public interface IClock
{
    long Ticks { get; }
    double Speed { get; set; }
}
```
**Purpose**: Interface for external clock sources to enable synchronized playback across multiple players.

---

#### `FlyleafLib/MediaPlayer/Player.cs`
**Changes**:
- Added `IClock ExternalClock { get; set; }` property (line ~25)
- Added speed propagation to external clock when player speed changes (line ~333)

```csharp
public IClock ExternalClock { get; set; }

// In speed setter:
if (ExternalClock != null) ExternalClock.Speed = newValue;
```

**Conflict Risk**: 🟡 Medium - Core player file, but changes are minimal and additive.

---

#### `FlyleafLib/Engine/Config.cs`
**Changes**:
- Added `External` option to `MasterClock` enum

```csharp
public enum MasterClock
{
    Audio,
    Video,
    External  // <-- Added
}
```

**Conflict Risk**: 🟢 Low - Additive enum change only.

---

#### `FlyleafLib/MediaPlayer/Player.Screamers.VASD.cs`
**Changes**: Heavy modifications (~25 insertion points)

Key modifications:
1. **Line ~289**: Capture `startClockTicks` from `ExternalClock` when starting playback
2. **Line ~320-365**: Use `ExternalClock.Ticks` for stream time calculation when `MasterClock == External`
3. **Line ~351-363**: Adaptive speed correction to stay in sync with external clock
4. **Line ~396-397**: Video frame distance calculation using external clock
5. **Line ~504-507**: Subtitle frame timing using external clock
6. **Line ~564-565**: Data frame timing using external clock
7. **Line ~718-795**: Audio frame timing and sync using external clock

**Conflict Risk**: 🔴 High - This is the heart of the A/V sync engine. Any upstream refactoring will cause conflicts.

---

#### `FlyleafLib/MediaPlayer/Player.Screamers.cs`
**Changes**:
- Line ~229: Capture `startClockTicks` from external clock
- Line ~235: Use external clock for elapsed time calculation

**Conflict Risk**: 🔴 High - Core screamer logic.

---

### 2. D3DImage Transparency Support

#### `FlyleafLib/MediaFramework/MediaRenderer/Renderer.D3DImage.cs` (NEW FILE)
**Lines**: 240  
**Purpose**: Manages D3D11-to-D3D9 texture sharing for WPF D3DImage interop

Key components:
- `D3DImageManager` class - handles shared texture creation and synchronization
- D3D9 device initialization for texture sharing
- Keyed mutex synchronization between D3D11 and D3D9
- Automatic texture resizing based on control dimensions

**Classes Added**:
- `D3DImageManager` (public, disposable)

**Conflict Risk**: 🟢 Low - New file, unlikely to conflict unless upstream adds similar functionality.

---

#### `FlyleafLib/MediaFramework/MediaRenderer/Renderer.Device.cs`
**Changes**:
- Line ~183: Call `D3DImageInit()` during device initialization
- Line ~259: Call `D3DImageDispose()` during disposal

**Conflict Risk**: 🟡 Medium - Device lifecycle hooks.

---

#### `FlyleafLib/MediaFramework/MediaRenderer/Renderer.Present.cs`
**Changes**: Multiple insertion points for D3DImage rendering path
- Line ~92: Acquire D3DImage mutex before rendering
- Line ~148: Release D3DImage mutex after rendering
- Line ~193-240: Alternative Present path for D3DImage mode
- Line ~265: Additional D3DImage checks
- Line ~303-320: D3DImage cleanup in alternative path

**Conflict Risk**: 🔴 High - Core rendering pipeline.

---

#### `FlyleafLib/MediaFramework/MediaRenderer/Renderer.VP.cs`
**Changes**:
- Line ~142: Skip viewport processing if D3DImage is not enabled

**Conflict Risk**: 🟡 Medium - Viewport processing logic.

---

#### `FlyleafLib/MediaFramework/MediaRenderer/SwapChain.cs`
**Changes**:
- Line ~24: Modified `CanPresent` property to consider D3DImage state

**Conflict Risk**: 🟡 Medium - Presentation logic.

---

#### `FlyleafLib.Controls.WPF/FlyleafD3DImage.cs` (NEW FILE)
**Lines**: 153  
**Purpose**: WPF control that uses D3DImage instead of HwndHost for transparency support

Key features:
- Inherits from `Image` (not ContentControl with HwndHost)
- Uses WPF's `D3DImage` as backing store
- Subscribes to `CompositionTarget.Rendering` for frame updates
- Handles front buffer availability changes
- Proper lifecycle management and disposal

**Conflict Risk**: 🟢 Low - New file in Controls project.

---

#### `FlyleafLib/MediaPlayer/Video.cs`
**Changes**:
- Lines ~80-83: Added `D3DImageLatencyMs` property for diagnostics

**Conflict Risk**: 🟢 Low - Diagnostic property only.

---

#### `FlyleafLib/Engine/Engine.cs`
**Changes**:
- Line ~369: Propagate D3DImageLatencyMs in engine loop

**Conflict Risk**: 🟡 Medium - Engine main loop.

---

### 3. Stability and Testing Enhancements

#### `FlyleafLib/MediaPlayer/Player.cs`
**Changes**:
- Modified `Status` property from `private set` to `internal set` (line ~165).
- Allows external components (like GOON's test suite) to simulate or force player status transitions for stall detection testing.

#### `FlyleafLib/AssemblyInfo.cs`
**Changes**:
- Added `[assembly: InternalsVisibleTo("GOON")]`
- Added `[assembly: InternalsVisibleTo("GOON.Tests")]`
**Purpose**: Allows the main application and its test suite to access internal members of FlyleafLib (like the volatile `status` field) for advanced synchronization and stability monitoring.

---

### 4. yt-dlp Future Compatibility (March 2026)

#### `Plugins/YoutubeDL/YoutubeDLJson.cs`
**Changes**:
- Added `[JsonExtensionData]` on `Format` class to capture unknown fields from future yt-dlp versions
- Added `RobustJsonOptions` static property with `AllowReadingFromString`, `PropertyNameCaseInsensitive`, `AllowTrailingCommas`

**Conflict Risk**: 🟢 Low - Additive properties only.

#### `Plugins/YoutubeDL/YoutubeDL.cs`
**Changes**:
- JSON deserialization now uses `YoutubeDLJson.RobustJsonOptions` instead of basic options
- Null-safe `HasVideo()`/`HasAudio()` — checks `!= null` before `"none"` sentinel comparison
- Null-safe protocol regex in `GetBestMatch()` LINQ query
- Default codec values changed from `""` to `"none"` for consistency
- Added null check for `fmt.url`

**Conflict Risk**: 🟡 Medium - Format handling logic, but changes are defensive additions.

---

### 5. Test Files

#### `Tests/FlyleafLib.Tests/ClockSyncTests.cs` (NEW FILE)
**Purpose**: Unit tests for external clock synchronization

Tests included:
- `PlayerUsesExternalClockWhenSet` - Verifies ExternalClock property assignment
- `SpeedUpdatePropagatesToExternalClock` - Verifies speed sync between player and clock

**Conflict Risk**: 🟢 Low - New test file.

---

## 🔧 Maintenance Guidelines

### When Merging Upstream Changes

1. **Always backup first**: `git stash` or create a branch before merging
2. **Check these files for conflicts**:
   - `Player.Screamers.VASD.cs` (most likely to conflict)
   - `Renderer.Present.cs` (second most likely)
   - `Plugins/YoutubeDL/YoutubeDLJson.cs` (if upstream changes JSON model)
3. **Run tests after merge**: Execute `ClockSyncTests` to verify sync still works
4. **Manual verification**: Test D3DImage rendering with transparent window
5. **yt-dlp JSON check**: If upstream updates `YoutubeDLJson.cs`, ensure `[JsonExtensionData]` and `RobustJsonOptions` are preserved

### Upstream Changes to Watch For

| Upstream Change Type | Impact | Action Required |
|---------------------|--------|-----------------|
| FFmpeg version bump | 🟢 Low | Usually compatible |
| Vortice.Windows update | 🟡 Medium | Check D3DImageManager |
| Screamer refactoring | 🔴 High | Manual conflict resolution |
| New renderer architecture | 🔴 High | May require D3DImage rewrite |
| YoutubeDLJson model update | 🟡 Medium | Re-apply `[JsonExtensionData]` and `RobustJsonOptions` |
| yt-dlp JSON schema change | 🟢 Low | `[JsonExtensionData]` absorbs unknown fields automatically |

---

## 📊 Change Statistics

| Metric | Count |
|--------|-------|
| New files created | 4 |
| Existing files modified | 12 |
| Lines added (estimated) | ~700 |
| Lines modified (estimated) | ~65 |

---

## 🧪 Testing Checklist

After any merge from upstream, verify:

- [ ] Single-monitor playback works normally
- [ ] Multi-monitor sync (ExternalClock) stays within 10ms drift
- [ ] Transparent overlay windows render correctly (D3DImage)
- [ ] Speed changes propagate to all synchronized players
- [ ] No memory leaks in D3DImageManager (use diagnostic tools)
- [ ] `ClockSyncTests` pass
- [ ] yt-dlp extraction works with latest yt-dlp binary (`YtDlpCompatibilityTests`)
- [ ] `YtDlpService.DetectedVersion` reports correct version at startup

---

## 📝 Notes

- The fork maintains API compatibility with upstream where possible
- Custom changes are isolated to specific subsystems to minimize merge conflicts
- Consider contributing `IClock` interface upstream if stable
- `[JsonExtensionData]` on `Format` is critical — never remove it when merging upstream

---

*Last updated: March 2026*
