# 🌌 EdgeLoop v1.0

![Build Status](https://img.shields.io/github/actions/workflow/status/experiment-peepo/EdgeLoop/release.yml?style=flat-square&label=Build)
![License](https://img.shields.io/github/license/experiment-peepo/EdgeLoop?style=flat-square)
![Prerelease](https://img.shields.io/github/v/release/experiment-peepo/EdgeLoop?include_prereleases&style=flat-square&label=Prerelease)
![Attestation](https://img.shields.io/badge/Provenance-Verified%20🛡️-00f2ff?style=flat-square)
![Hardening](https://img.shields.io/badge/Hardening-Deep%20Audit%20✅-ff00ff?style=flat-square)

**EdgeLoop** is a high-performance video engine designed for multi-monitor setups. Built on a custom fork of `Flyleaf`, it provides seamless, transparent overlays directly on your desktop.

---

## 🚀 Key Features

### 🖥️ Advanced Multi-Monitor Management
*   **Targeted Playback**: Pin videos to specific displays or span them across your entire workspace.
*   **External Synchronization**: Multiple player instances follow a single master clock for frame-exact synchronization across monitors.

### 🔲 Transparent Graphics Pipeline
*   **DirectX 11 Rendering**: Custom pipeline that allows video windows to be truly transparent.
*   **Windowless Feel**: Run content as if it's part of your wallpaper.

### 🌐 Deep Web Integration
*   **Powered by yt-dlp**: Stream from 1000+ sites.
*   **Session Shield**: Safely store and inject cookies to unlock high-res streams.

### ⚡ Performance & Portability
*   **Smart Buffering**: Intelligent disk-caching for high-resolution content.
*   **Zero-Install**: Fully portable architecture. All data is kept in the `Data/` folder.
*   **Panic Mode**: Instant shortcut to wipe all active players from the screen.

### 💾 Local Caching Architecture
EdgeLoop features a robust background pre-buffering system designed to eliminate stuttering and provide instant seeking capabilities.
*   **MP4/Static Video Sites** (e.g., Iwara, Rule34Video, Hypnotube): EdgeLoop automatically downloads these videos to the local cache (`%LOCALAPPDATA%\EdgeLoop\VideoCache` by default) in the background. When playback starts, it seamlessly switches to the local file for zero-stutter performance.
*   **HLS Streaming Sites** (e.g., PMVHaven): These videos are delivered in live segments and cannot be easily cached as a single file. EdgeLoop streams these directly and bypasses the disk cache.
*   **Auto-Cleanup**: Cached files are automatically cleaned up 10 days after their last access to prevent disk space exhaustion.
*   **Toggle**: You can disable local caching in the Settings if you prefer to stream everything (Note: This may cause slower start times and stuttering when seeking for MP4 sites).

## 🛠️ Technical Stack
*   **Engine**: Custom Flyleaf Fork (C# / WPF / DirectX 11)
*   **Media**: Internal FFmpeg shared libraries
*   **Extraction**: Integrated `yt-dlp`
*   **Platform**: .NET 10 (Windows x64)

## 📦 Installation & Setup

1.  **Requirements**: Ensure the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0-windows-desktop-runtime) is installed.
2.  **Download**: Purchase and download the latest bundle from [itch.io](https://psi-conaut.itch.io/edgeloop). This is the only official way to purchase the software.
3.  **Run**: Extract and launch `EdgeLoop.exe`.

### Site Authentication
For sites that require sessions:
1.  Open your browser and log in to the site.
2.  Open DevTools (`F12`), go to the Console, and run: `copy(document.cookie)`.
3.  In EdgeLoop, go to **Settings > Session & History** and click **Paste**.

## 🤝 Contributing
*   See [CONTRIBUTING.md](.github/CONTRIBUTING.md) for guidelines.
*   Check the [Roadmap](docs/Flyleaf_Fork_Roadmap.md) for planned features.

## 📜 License
Distributed under the **GPL-3.0 License**. See `LICENSE` for more information.

---
*Support the development on [Patreon](https://www.patreon.com/cw/vexfromdestiny) or [Ko-fi](https://ko-fi.com/vexfromdestiny)*
