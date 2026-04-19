# Contributing to EdgeLoop

First off, thank you for considering contributing to EdgeLoop! It's people like you that make EdgeLoop such a great tool for the community.

## 🌈 Code of Conduct
Please be respectful and professional in all interactions. EdgeLoop is a niche project, but we maintain high standards for technical excellence and community behavior.

## 🚀 How Can I Contribute?

### Reporting Bugs
*   Check the existing [Issues](https://github.com/vexfromdestiny/EdgeLoop/issues) first.
*   Use the **Bug Report** template.
*   Include steps to reproduce, expected vs. actual behavior, and logs if possible.

### Suggesting Enhancements
*   Submit an [Issue](https://github.com/vexfromdestiny/EdgeLoop/issues) with the tag `enhancement`.
*   Explain why this feature would be useful and how it should work.

### Pull Requests
1.  Fork the repo and create your branch from `main`.
2.  Follow the existing code style (clean C#, MVVM pattern).
3.  Include tests for any new logic.
4.  Update the documentation (`README.md`, `docs/`) if necessary.
5.  Open a Pull Request with a clear description of the change.

## 💻 Tech Stack
EdgeLoop is built on .NET 10 (windows-specific) using WPF and DirectX 11 via the Flyleaf engine.

*   **UI**: XAML / WPF
*   **Engine**: Flyleaf (Custom Fork)
*   **Media**: FFmpeg
*   **Logic**: C# 12+

## 📜 Development Workflow
1.  Open `EdgeLoop.sln` in VS Code or Visual Studio 2022+.
2.  Ensure you have the .NET 10 SDK installed.
3.  Run `update_dependencies.ps1` to pull the latest `yt-dlp` binary.
4.  Build in `Debug` configuration for a full verification.

---
*Happy Coding!*
