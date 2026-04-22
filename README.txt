EdgeLoop v1.0
==============

A specialized video player designed for multi-monitor playback with high-performance overlay capabilities.

*******************************************************************************
** IMPORTANT: YOU MUST INSTALL THE .NET 10 DESKTOP RUNTIME FIRST!            **
** Download here: https://dotnet.microsoft.com/download/dotnet/10.0          **
*******************************************************************************

KEY CAPABILITIES
----------------
* AI Super Resolution: Uses hardware-accelerated Nvidia VSR / Intel VPE to upscale videos in real-time.
* Multi-Monitor Support: Assign different videos to specific screens or play across "All Monitors".
* Overlay Mode: High-performance transparent overlays that play directly on your desktop.
* Truly Portable: Stores all settings, logs, and sessions in a local 'Data' folder if write access is available.
* Web Integration: Stream directly from supported sites (Rule34Video, Hypnotube, etc.) using integrated yt-dlp support.
* Smart Buffering: Automatically downloads high-res videos to disk for instant, stutter-free playback.
* Privacy Focused: "Boss Key" features to instantly hide or minimize players.

QUICK START
-----------
1. Install the .NET 10 Desktop Runtime (linked above).
2. Purchase and download the software from: https://psi-conaut.itch.io/edgeloop
3. Extract the contents of this zip to a folder.
3. Run EdgeLoop.exe.
4. Drag and drop videos or paste URLs to get started.

KNOWN NOTES
-----------
* Lean Distribution: This application is "Framework-Dependent" to keep the file size small. It relies on your system's .NET 10 installation.
* Data Storage: If the app cannot create a local 'Data' folder, it will fall back to %AppData%\EdgeLoop.
* Dependencies: To keep web streaming working, you should occasionally update yt-dlp via 'update_dependencies.ps1'.


---
Support the development:
- Patreon: https://www.patreon.com/cw/vexfromdestiny
- Ko-fi: https://ko-fi.com/vexfromdestiny
