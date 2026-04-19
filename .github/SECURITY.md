# Security Policy

## Supported Versions

| Version | Supported          |
| ------- | ------------------ |
| v1.1.x  | :white_check_mark: |
| < v1.1  | :x:                |

## Reporting a Vulnerability

We take security seriously. If you discover a vulnerability, please do NOT report it publicly (this includes opening an issue).

Instead, please send an email to the maintainer or report it via the GitHub Security feature if enabled.

### What we protect:
*   **Cookie Security**: We use Windows DPAPI (Data Protection API) to encrypt stored cookies. These are tied to your Windows User Account and cannot be decrypted on other machines.
*   **Process Integrity**: EdgeLoop executes `yt-dlp.exe` and `ffmpeg` libraries locally. We ensure no external scripts are executed without user consent.

---
*Thank you for helping keep EdgeLoop safe.*
