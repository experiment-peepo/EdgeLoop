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
