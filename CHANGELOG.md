## [Unreleased]

### Changed
- **Playback engine**: Updated the bundled FlyleafLib to v3.12 with FFmpeg 9.
- **Upscaling**: Videos smaller than the screen are upscaled by EdgeLoop's own FSRCNNX shader, on every screen at once and on any graphics card. The NVIDIA and Intel driver upscalers are no longer used: they upscale only one video at a time and need the driver's own setting. Settings can limit upscaling to videos up to 720p or 1080p.
- **One look across the app**: Every window shares the same title bar, with the Windows shadow, snapping and (on Windows 11) rounded corners. Colours, fonts and controls come from one theme; buttons, check boxes and sliders can be reached with Tab and show a focus ring.
- **Launcher**: The playlist only builds the cards on screen, so large playlists stay fast. Cards show each video's length and PLAYING / FAILED / MISSING / CACHED badges. Removing videos, Clear and the new "Remove failed" offer Undo. Delete removes the selected card and Alt+Up/Down moves it. Start explains why it's disabled, and Now Playing has a Previous button.
- **Settings**: Sections in a sidebar, a "click and press keys" hotkey recorder that flags duplicates, skip keys for each screen with an "Identify screens" button, hidden cookie fields, Reset to defaults per section, and a prompt before discarding unsaved changes.
- **Dialogs**: One confirmation dialog, consistent button order, and an input dialog with a hint that keeps OK disabled until something is typed.
- **Display scaling**: EdgeLoop is now per-monitor DPI aware, so windows, overlays and notices line up on setups that mix scaling levels.

### Fixed
- Skip hotkeys (the arrow keys) and the triple-Esc stop are only captured while videos play, so they work normally in other apps otherwise.
- A video that fails to play is marked as failed instead of being removed from the playlist and saved session.
- Overlay windows no longer appear in the taskbar or Alt+Tab, and never take focus.
- Loading a video no longer darkens the whole screen: a small card in the corner shows progress, and each video's own opacity applies straight away.
- A short notice in the corner of a screen says which video couldn't play and why; it can be turned off in Settings.
- Cookie fields no longer delete spaces while you type; they're cleaned up when saved.
- Enter and Esc work in every dialog.

## [1.0.0-prerelease] - 2026-04-26

### Added
- **Deep Audit Fixes**: Major reliability hardening across core services.
- **Atomic Saving**: Settings, playback history, and URL caches now use "atomic" save operations to prevent data corruption.
- **Download Concurrency Limit**: Added a limit of 4 simultaneous downloads to prevent network/system lag.
- **Startup Path Repair**: Application now automatically fixes its Windows startup registration if the folder is moved.
- **Power & Sleep Management**: Improved PC sleep prevention during slow video buffering and handled "ghost" windows after terminal failures.
- **yt-dlp Persistence**: Update checks for yt-dlp now persist across restarts (24-hour throttle).
- **Resource Cleanup**: Hardened memory management in LRU caches and Flyleaf engine disposal.

## [1.0.0-beta] - 2026-04-26

### Added
- **YouTube Ambiguity Handler**: Added a new choice dialog that appears when importing YouTube links containing both a video and a playlist ID. You can now choose to import just the video or the entire collection.
- **Source Labeling**: The watchlist now displays the source of each video (e.g., "YouTube", "PMVHaven", "Local File") as a subtle sub-label under the title.
- **Smart Dependency Updates**: The `update_dependencies` tools now intelligently check for the latest versions on GitHub before downloading, saving bandwidth.
- **Single Instance Guard**: Implemented a global application Mutex to prevent multiple instances from running simultaneously.
- **Diagnostics**: Added a dedicated `diagnostics.log` for troubleshooting engine-level issues without cluttering the main log.

### Fixed
- **Startup Engine Initialization**: Resolved a critical startup crash where the app failed to find FFmpeg shared libraries. It now intelligently validates paths and looks for required DLLs before initializing.
- **YouTube Cookie Lock**: Added a retry mechanism for `yt-dlp` to handle "database locked" errors (common when Chrome is open). It now falls back to public extraction if cookies are inaccessible.
- **PMVHaven Playlist Import**: Fixed a critical issue where only the first video was imported from PMVHaven collections. It now uses a specialized LD+JSON extractor to find all videos reliably.
- **Title Sanitization**: Improved title cleaning for PMVHaven to remove redundant site names and uploader attributions, keeping your watchlist clean.
- **URL Pattern Support**: Added support for singular `/video/` URL patterns alongside the plural `/videos/` patterns.
- **Multi-Monitor Synchronization**: Hardened the frame-sync logic for better reliability when monitors have different refresh rates or buffering speeds.
- **UI & Layout**: Fixed a `XamlParseException` in the choice dialog and improved capitalization and alignment in the settings window.

### Changed
- **Default Browser**: Set **Firefox** as the default browser for cookie extraction (more reliable profile handling).
- **Dependency Pipeline**: Increased FFmpeg download timeouts to 5 minutes to accommodate slower internet connections.
- **Hardware Acceleration**: Optimized the engine to use the D3D11 Video Processor by default for superior performance on modern Windows systems.

## [1.0.0] - 2026-04-21

### Added (since Rebranding)
- **Drag & Drop Enhancements**
  - Recursive folder drop support (scans subdirectories for video files)
  - URL drop support (drag links directly into playlist)
  - Visual insertion indicator during drag (shows exactly where video will land)
  - Drag ghost preview image (follows cursor during drag)
  - Auto-scroll during drag near playlist edges
  - Keyboard shortcuts: `Delete` to remove, `Alt+Up/Down` for reordering

- **Playlist Improvements**
  - URL vs Local file visual badges (Globe icon for URLs, File icon for local)
  - Instant highlight updates when removing videos
  - Removed videos now stop playing immediately

- **Performance**
  - Transitioned the entire engine to **.NET 10** for improved hardware acceleration support.
  - Removed unused ffmpeg.exe dependency (~100MB smaller package)
  - Improved highlight synchronization logic
  - Centralized playlist highlight management

### Fixed
- Videos removed from playlist no longer continue playing in the background
- Highlight state now stays consistent during rapid editing or monitor changes
- Better exception handling in core playback loop

### Changed
- **Rebranding**: Project renamed to **EdgeLoop**.
- FFmpeg now uses internal shared libraries only (no standalone ffmpeg.exe needed).
- Updated documentation and technical guides.

## [1.0.0] - Initial Release (as GOON)

### Features
- Multi-monitor overlay video playback
- Transparent window support via custom D3DImage integration
- Frame-exact synchronization across monitors
- yt-dlp integration for streaming sites
- Session persistence and position tracking
- AI Super Resolution support (Nvidia VSR / Intel VPE)
- Panic hotkey for instant clearing
