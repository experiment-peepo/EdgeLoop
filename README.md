# 🌌 EdgeLoop

![GitHub release (latest by date)](https://img.shields.io/github/v/release/vexfromdestiny/EdgeLoop?style=for-the-badge&color=blueviolet)
![GitHub Workflow Status](https://img.shields.io/github/actions/workflow/status/vexfromdestiny/EdgeLoop/release.yml?style=for-the-badge&label=Build)
![License](https://img.shields.io/github/license/vexfromdestiny/EdgeLoop?style=for-the-badge&color=blue)
![Target](https://img.shields.io/badge/.NET-10.0-512bd4?style=for-the-badge&logo=dotnet)

**EdgeLoop** is a state-of-the-art, high-performance video engine designed for the specialized needs of multi-monitor setups and hypnotic entertainment. Built on a custom fork of `Flyleaf`, it eliminates the "Airspace Problem" to provide seamless, transparent overlays directly on your desktop.

---

## 🎨 Cyber Luxe Aesthetics
Designed with a "Cyber Luxe" philosophy, EdgeLoop combines raw performance with a sleek, minimalist interface that feels alive.

![EDGELOOP UI](assets/screenshot.png)

## 🚀 Key Features

### 🖥️ Advanced Multi-Monitor Management
*   **Targeted Playback**: Pin videos to specific displays or span them across your entire workspace.
*   **External Synchronization**: (Shared Clock) Multiple player instances follow a single master clock for frame-exact synchronization across monitors.

### 🔲 Transparent Graphics Pipeline
*   **DirectX 11 Rendering**: Custom `D3DImage` pipeline that allows video windows to be truly transparent (Alpha Channel support).
*   **Windowless Feel**: Run your content as if it's part of your wallpaper.

### 🌐 Deep Web Integration
*   **Powered by yt-dlp**: Seamlessly stream from 1000+ sites including high-quality niche platforms.
*   **Session Shield**: Safely store and inject cookies (via DPAPI encryption) to unlock premium/high-res streams without compromising security.

### ⚡ Performance & Portability
*   **Smart Buffering**: Intelligent disk-caching for 4K+ content to prevent stuttering on volatile network connections.
*   **Zero-Install**: Fully portable architecture. All data is kept in the `Data/` folder by default.
*   **Panic Mode**: `Ctrl+Shift+End` instantly wipes all active players from the screen.

## 🛠️ Technical Stack
*   **Engine**: Custom Flyleaf Fork (C# / WPF / DirectX 11)
*   **Media**: Internal FFmpeg 7.x/8.x shared libraries
*   **Extraction**: Integrated `yt-dlp`
*   **Platform**: .NET 10 (Windows x64)

## 📦 Installation & Setup

1.  **Requirements**: Ensure you have the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0-windows-desktop-runtime) installed.
2.  **Download**: Get the latest `EdgeLoop.zip` from the [Releases](https://github.com/vexfromdestiny/EdgeLoop/releases) page.
3.  **Run**: Extract and launch `EdgeLoop.exe`. No installation required.

### Site Authentication (Optional)
For sites like **Hypnotube** that require sessions for high-res content:
1.  Open your browser and log in to the site.
2.  Open DevTools (`F12`), go to the Console, and run: `copy(document.cookie)`.
3.  In EdgeLoop, go to **Settings > Session & History** and click **Paste**.

## 🤝 Contributing
Contributions are what make the open-source community such an amazing place to learn, inspire, and create.
*   See [CONTRIBUTING.md](.github/CONTRIBUTING.md) for guidelines.
*   Check the [Roadmap](docs/Flyleaf_Fork_Roadmap.md) for planned features.

## 📜 License
Distributed under the **GPL-3.0 License**. See `LICENSE` for more information.

---
*Created with 💜 for the enthusiast community.*
*Support on [Ko-fi](https://ko-fi.com/vexfromdestiny)*
