# Smart Shuffle Implementation Plan
## Version 2.0 - Intelligent Video Selection Algorithm

---

## Executive Summary

This document outlines the complete implementation plan for upgrading the EdgeLoop shuffle system from a simple "play each video once randomly" approach to an intelligent, multi-factor scoring system that considers recency, user preferences, content variety, and watch behavior.

---

## Phase 1: Data Infrastructure

### 1.1 New Data Model: VideoPlayRecord

**Purpose**: Store rich metadata about each video play event for scoring calculations.

**Location**: Create new file `EdgeLoop/Classes/VideoPlayRecord.cs`

**Fields**:
| Field | Type | Description |
|-------|------|-------------|
| `FilePath` | string | Unique identifier for the video |
| `PlayedAt` | DateTime | Timestamp of when video started playing |
| `WatchDurationMs` | long | How long user watched before skip/end |
| `VideoDurationMs` | long | Total video length |
| `WasSkipped` | bool | True if user manually skipped (watched < 30%) |
| `WasCompleted` | bool | True if video played to end (watched > 90%) |
| `SessionId` | Guid | Links plays within same session |
| `PlaylistHash` | string | 8-char hash of the playlist this was played in |

**Storage Strategy**:
- Store in `Data/play_history.json` (separate from settings.json)
- Rolling window: Keep last 30 days of records
- Prune on app startup and periodically during runtime
- Maximum 50,000 records (approximately 10MB)

### 1.2 Extend UserSettings.cs

**New Properties**:
| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `ShuffleMode` | enum | `Smart` | Options: `Simple`, `Smart`, `Random` |
| `ShuffleRecencyWeight` | double | `0.3` | How much recency affects scoring (0-1) |
| `ShufflePreferenceWeight` | double | `0.3` | How much skip history affects scoring (0-1) |
| `ShuffleVarietyWeight` | double | `0.2` | How much variety affects scoring (0-1) |
| `ShuffleLengthWeight` | double | `0.2` | How much length mixing affects scoring (0-1) |
| `EnableShuffleDebugLog` | bool | `false` | Log scoring details for debugging |

### 1.3 New Service: PlayHistoryService

**Purpose**: Centralized management of play history with efficient querying.

**Location**: Create new file `EdgeLoop/Services/PlayHistoryService.cs`

**Responsibilities**:
1. Load/save play history from disk
2. Add new play records
3. Query records by file path, date range, or playlist
4. Calculate aggregate statistics (skip rate, play count, last played)
5. Prune old records
6. Thread-safe access for multi-monitor scenarios

**Public Methods**:
- `RecordPlayStart(VideoItem, playlistHash)` → returns recordId
- `RecordPlayEnd(recordId, watchedMs, wasSkipped, wasCompleted)`
- `GetLastPlayedDate(filePath)` → DateTime?
- `GetSkipRate(filePath)` → double (0-1)
- `GetTotalPlayCount(filePath)` → int
- `GetAverageWatchPercent(filePath)` → double (0-1)
- `GetRecentPlays(count)` → List of last N played file paths
- `PruneOldRecords(olderThan)` → removes records older than date

---

## Phase 2: Metric Implementation

### 2.1 Metric 1: Recency Score

**Concept**: Videos played long ago are fresher and should be prioritized.

**Calculation Logic**:
```
Input: filePath, currentPlayHistory
Output: score between 0.0 and 1.0

1. Find last play date for this file
2. If never played → return 1.0 (maximum freshness)
3. Calculate hours since last played
4. Apply logarithmic decay:
   - Played in last hour → 0.0
   - Played 1-24 hours ago → 0.1-0.4
   - Played 1-7 days ago → 0.5-0.8
   - Played 7+ days ago → 0.9-1.0
5. Return normalized score
```

**Edge Cases**:
- New videos (never played) get maximum score
- Videos played in current session get heavily penalized
- Cross-session recency is less penalizing than within-session

**Weight Configuration**: Default 30% of total score

### 2.2 Metric 2: Watch Time Score (Skip Detection)

**Concept**: Videos that users frequently skip should be played less often.

**Calculation Logic**:
```
Input: filePath, playHistoryService
Output: score between 0.0 and 1.0

1. Get all play records for this file
2. If no records → return 0.5 (neutral, unknown)
3. Calculate average watch percentage:
   averageWatchPercent = sum(watchedMs / videoDurationMs) / playCount
4. Calculate skip rate:
   skipRate = skippedCount / totalPlayCount
5. Combine into score:
   - High watch % + low skip rate → 0.8-1.0
   - Medium watch % → 0.4-0.7
   - Low watch % + high skip rate → 0.0-0.3
6. Return score
```

**Skip Detection Trigger**:
- User clicks "Skip" button → wasSkipped = true
- User closes window during playback → check watch percentage
- Video ends naturally → wasCompleted = true
- Watch < 30% of video duration → considered a skip

**Weight Configuration**: Default 30% of total score

### 2.3 Metric 3: Play Count Balance Score

**Concept**: Balance play frequency so all videos get fair exposure.

**Calculation Logic**:
```
Input: filePath, allFilesInPlaylist, playHistoryService
Output: score between 0.0 and 1.0

1. Get play count for this file
2. Get play counts for all files in playlist
3. Calculate average play count across playlist
4. Calculate this file's deviation from average:
   deviation = (averageCount - thisCount) / maxDeviation
5. Normalize to 0-1:
   - Played much less than average → 0.8-1.0 (should play more)
   - Played average amount → 0.5
   - Played much more than average → 0.0-0.2 (should play less)
6. Return score
```

**Considerations**:
- New files added to playlist should get boosted
- Files with 0 plays get maximum score
- Cap the penalty so popular videos aren't completely blocked

**Weight Configuration**: Included in Recency weight by default

### 2.4 Metric 4: Video Length Variety Score

**Concept**: Avoid playing many short or long videos consecutively.

**Calculation Logic**:
```
Input: filePath, videoDuration, recentPlayHistory (last 5 videos)
Output: score between 0.0 and 1.0

1. Categorize this video by length:
   - Short: < 60 seconds
   - Medium: 60 seconds - 5 minutes
   - Long: > 5 minutes

2. Analyze recent plays:
   - Count how many of last 5 were in each category
   
3. Boost score if this video's category is underrepresented:
   - If 4 of last 5 were Short and this is Long → score = 1.0
   - If last 5 were mixed and this matches pattern → score = 0.5
   - If 4 of last 5 were same category as this → score = 0.2

4. Return variety score
```

**Categories Definition**:
| Category | Duration Range | Typical Content |
|----------|---------------|-----------------|
| Short | 0-60s | Clips, teasers |
| Medium | 1-5min | Standard videos |
| Long | 5min+ | Full-length content |

**Weight Configuration**: Default 20% of total score

### 2.5 Metric 5: Creator/Source Variety Score

**Concept**: Avoid playing too many videos from the same source/folder consecutively.

**Calculation Logic**:
```
Input: filePath, recentPlayHistory (last 3 videos)
Output: score between 0.0 and 1.0

1. Extract source identifier:
   - Use parent folder name as "creator"
   - Example: "D:\Videos\CreatorA\video.mp4" → creator = "CreatorA"

2. Get creators of last 3 played videos

3. Count consecutive same-creator plays:
   - If this video's creator matches last 3 → score = 0.0
   - If this video's creator matches last 2 → score = 0.3
   - If this video's creator matches last 1 → score = 0.6
   - If this video's creator is different from all 3 → score = 1.0

4. Return variety score
```

**Source Identification Strategy**:
- Primary: Parent folder name
- Fallback: First word of filename after removing dates/numbers
- Override: Allow user to define creator patterns in settings

**Weight Configuration**: Included in Variety weight by default

### 2.6 Metric 6: Time-of-Day Preference Score

**Concept**: Learn what content user prefers at different times.

**Calculation Logic**:
```
Input: filePath, currentHour, playHistoryService
Output: score between 0.0 and 1.0

1. Get current time bucket:
   - Morning: 6 AM - 12 PM
   - Afternoon: 12 PM - 6 PM
   - Evening: 6 PM - 10 PM
   - Night: 10 PM - 6 AM

2. Query play history for this file during current time bucket

3. Calculate time-bucket affinity:
   - If video is frequently watched at this time → score = 0.8-1.0
   - If video is rarely watched at this time → score = 0.3-0.5
   - If no data for this time → score = 0.5 (neutral)

4. Return time preference score
```

**Learning Period**:
- Need at least 20 play records to generate meaningful preferences
- Until then, return neutral score (0.5)
- Weight this metric lower until data is available

**Weight Configuration**: Default 10% of total score (increases over time)

---

## Phase 3: Scoring Engine

### 3.1 New Service: ShuffleScoringEngine

**Purpose**: Combine all metrics into a final score for each candidate video.

**Location**: Create new file `EdgeLoop/Services/ShuffleScoringEngine.cs`

**Core Algorithm**:
```
For each candidate video in playlist:
  1. Calculate individual metric scores:
     - recencyScore = RecencyMetric.Calculate(video)
     - watchTimeScore = WatchTimeMetric.Calculate(video)
     - playCountScore = PlayCountMetric.Calculate(video)
     - lengthVarietyScore = LengthVarietyMetric.Calculate(video)
     - creatorVarietyScore = CreatorVarietyMetric.Calculate(video)
     - timeOfDayScore = TimeOfDayMetric.Calculate(video)
  
  2. Apply weights from settings:
     weightedRecency = recencyScore * settings.RecencyWeight
     weightedPreference = (watchTimeScore + playCountScore) / 2 * settings.PreferenceWeight
     weightedVariety = (lengthVarietyScore + creatorVarietyScore) / 2 * settings.VarietyWeight
     weightedLength = lengthVarietyScore * settings.LengthWeight
  
  3. Calculate final score:
     finalScore = weightedRecency + weightedPreference + weightedVariety + weightedLength
  
  4. Add small random factor (±5%) to prevent deterministic behavior:
     finalScore += (random.NextDouble() - 0.5) * 0.1 * finalScore
  
  5. Store (video, finalScore) pair

Select video with highest final score
```

### 3.2 Scoring Modes

**Mode 1: Simple (Current Behavior)**
- Only uses binary "played/not played" logic
- No metrics, no learning
- Fastest, no disk I/O during shuffle

**Mode 2: Smart (Recommended Default)**
- Uses all 6 metrics
- Learns from user behavior
- Balances variety and preference

**Mode 3: True Random**
- Pure random selection (Fisher-Yates on each pick)
- No history, no learning
- Each video equally likely every time

### 3.3 Performance Considerations

**Caching Strategy**:
- Cache computed scores for 10 seconds (or until playlist changes)
- Only recalculate scores when:
  - Video plays/skips (affects recency)
  - Playlist changes
  - Cache expires

**Lazy Loading**:
- Don't load full play history until shuffle is needed
- Load only relevant records (this playlist, last 30 days)

**Batch Scoring**:
- Score all candidates once, then pick best
- Don't score on demand (too slow)

---

## Phase 4: Integration Points

### 4.1 HypnoViewModel.cs Changes

**PlayNext() Method Modification**:

Current flow:
```
PlayNext() → Pick random from unplayed → Load video
```

New flow:
```
PlayNext() 
  → Get all candidates from playlist
  → Call ShuffleScoringEngine.ScoreAll(candidates)
  → Select highest scoring video
  → Record play start with PlayHistoryService
  → Load video
```

**OnMediaEnded() / Skip Button Modification**:

Add tracking:
```
When video ends or is skipped:
  → Calculate watch percentage
  → Call PlayHistoryService.RecordPlayEnd(recordId, watchedMs, wasSkipped, wasCompleted)
```

### 4.2 LauncherViewModel.cs Changes

**Remove Pre-shuffle**:
- Currently shuffles playlist before sending to HypnoWindow
- With smart shuffle, this is no longer needed
- Instead, pass playlist in original order; let HypnoViewModel pick

**Settings UI**:
- Add shuffle mode dropdown (Simple/Smart/Random)
- Add advanced settings expandable panel:
  - Recency weight slider
  - Preference weight slider
  - Variety weight slider
  - Length weight slider

### 4.3 UserSettings.cs Changes

**New Properties** (as detailed in Phase 1.2)

**Migration**:
- Handle case where old settings exist without new properties
- Set sensible defaults for new users

### 4.4 App.xaml.cs Changes

**Service Registration**:
- Register PlayHistoryService as singleton
- Register ShuffleScoringEngine (depends on PlayHistoryService)

**Startup Sequence**:
1. Load settings
2. Initialize PlayHistoryService (load history from disk)
3. Prune old records (async, non-blocking)
4. Continue with app initialization

---

## Phase 5: UI Enhancements

### 5.1 Settings Window Updates

**New Section: "Smart Shuffle"**

Layout:
```
┌─────────────────────────────────────────────────┐
│ Smart Shuffle Settings                          │
├─────────────────────────────────────────────────┤
│ Mode: [Simple ▼] [Smart] [Random]               │
│                                                 │
│ ▶ Advanced Weights (click to expand)            │
│   ├─ Recency:    [====------] 40%               │
│   ├─ Preference: [====------] 40%               │
│   ├─ Variety:    [==--------] 20%               │
│   └─ Reset to Defaults                          │
│                                                 │
│ ☐ Enable debug logging (shows scores in log)   │
│                                                 │
│ Play History: 1,234 records (2.3 MB)            │
│ [Clear History] [Export CSV]                    │
└─────────────────────────────────────────────────┘
```

### 5.2 Debug Overlay (Optional)

When debug logging is enabled, show a small overlay during playback:
```
┌─────────────────────────┐
│ Score: 0.87             │
│ Recency: 0.92           │
│ WatchTime: 0.85         │
│ Variety: 0.78           │
└─────────────────────────┘
```

---

## Phase 6: Testing Plan

### 6.1 Unit Tests

**PlayHistoryService Tests**:
- Record creation and retrieval
- Prune logic (correct records removed)
- Skip rate calculation accuracy
- Thread safety under concurrent access

**ShuffleScoringEngine Tests**:
- Individual metric calculations
- Combined scoring with various weights
- Edge cases (empty playlist, all new videos, all skipped)

### 6.2 Integration Tests

**End-to-End Shuffle Tests**:
- Verify no two consecutive videos with skip detection
- Verify variety across 100 shuffled picks
- Verify recency actually prevents repeats

### 6.3 Performance Tests

**Benchmark**:
- Score 1000 videos in < 50ms
- Load history file (10MB) in < 200ms
- Memory usage stays under 100MB for history service

---

## Phase 7: Rollout Plan

### 7.1 Stage 1: Data Collection (Week 1)

- Implement PlayHistoryService
- Start recording play events
- No changes to shuffle behavior yet
- Collect data to validate metrics

### 7.2 Stage 2: Metric Development (Week 2)

- Implement each metric in isolation
- Log scores to file for analysis
- Tune weights based on real data

### 7.3 Stage 3: Integration (Week 3)

- Integrate scoring engine into HypnoViewModel
- Add settings UI
- Behind feature flag initially

### 7.4 Stage 4: Polish (Week 4)

- A/B test results analysis
- Final weight tuning
- Documentation and release

---

## File Summary

### New Files to Create:
1. `EdgeLoop/Classes/VideoPlayRecord.cs` - Data model
2. `EdgeLoop/Services/PlayHistoryService.cs` - History management
3. `EdgeLoop/Services/ShuffleScoringEngine.cs` - Scoring logic
4. `EdgeLoop/Services/Metrics/RecencyMetric.cs` - Metric 1
5. `EdgeLoop/Services/Metrics/WatchTimeMetric.cs` - Metric 2
6. `EdgeLoop/Services/Metrics/PlayCountMetric.cs` - Metric 3
7. `EdgeLoop/Services/Metrics/LengthVarietyMetric.cs` - Metric 4
8. `EdgeLoop/Services/Metrics/CreatorVarietyMetric.cs` - Metric 5
9. `EdgeLoop/Services/Metrics/TimeOfDayMetric.cs` - Metric 6

### Files to Modify:
1. `EdgeLoop/Classes/UserSettings.cs` - Add new properties
2. `EdgeLoop/ViewModels/HypnoViewModel.cs` - Integrate scoring
3. `EdgeLoop/ViewModels/LauncherViewModel.cs` - Remove pre-shuffle
4. `EdgeLoop/Windows/SettingsWindow.xaml` - Add UI controls
5. `EdgeLoop/App.xaml.cs` - Register services

---

## Success Metrics

| Metric | Target | How to Measure |
|--------|--------|----------------|
| No immediate repeats | 0% repeat within 5 videos | Log analysis |
| Reduced skip rate | -20% vs simple shuffle | Compare skip records |
| Better variety | < 3 same-creator consecutive | Log analysis |
| User satisfaction | Qualitative feedback | User testing |
| Performance | < 50ms score time | Benchmark |

---

*Document Version: 1.0*
*Created: 2026-01-20*
*Author: AI Assistant*

