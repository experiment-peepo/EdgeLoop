# GOØN v1.1.0

A specialized video player designed for multi-monitor playback with high-performance overlay capabilities for desktop.

![GOØN UI](assets/screenshot.png)

## Features
- **Multi-Monitor Support**: Assign different videos to specific screens or span across "All Monitors".
- **Transparent Overlays**: High-performance players that run directly on your desktop background.
- **Truly Portable**: Automatic local data storage in `Data/` folder; falls back to `%AppData%` only if restricted.
- **Deep Web Integration**: Integrated `yt-dlp` support for seamless streaming from major media sites.
- **Panic Hotkey**: Instantly clear all active players (Default: `Ctrl+Shift+End`).
- **Smart Pre-buffering**: Automatically buffers high-resolution videos (4K+) to disk for instant playback, with auto-cleanup after 10 days.
- **Session Support**: Ability to inject cookies for sites like Hypnotube to unlock premium/high-res content.

## Session & Cookies
Some sites (e.g., Hypnotube) require authentication to access high-quality streams.
1.  **Extract Cookies**: Log in to the site in your browser, open developer tools (F12), type `copy(document.cookie)` in the console.
2.  **Import**: In GOON, go to **Settings > Session & History**, find the site, and click **Paste**.
3.  **Enjoy**: High-resolution streams will now work automatically.

## Dependencies
- **.NET 10 Desktop Runtime**: This app requires the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) to be installed.
- **Bundled Tools**: `yt-dlp` is already included for web video extraction.
- **Media Engine**: Powered by high-efficiency internal FFmpeg shared libraries (no external installation required).

## Quick Start
1. Extract the contents of this zip to a folder.
2. Run `GOON.exe`.
3. Drag and drop videos or paste URLs to get started.

## Notes
- **Distribution**: Distributed as a folder-based bundle for maximum UI stability on .NET 10.
- **Data Storage**: If the app cannot create a local `Data/` folder, it will fall back to using `%AppData%\GOON`.

---
*Support the development on [Ko-fi](https://ko-fi.com/vexfromdestiny)*
