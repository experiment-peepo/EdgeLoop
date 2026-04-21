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
