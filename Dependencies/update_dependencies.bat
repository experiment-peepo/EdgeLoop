<# :
@echo off
powershell -NoProfile -ExecutionPolicy Bypass -Command "iex ((Get-Content '%~f0') -join \"`n\")"
pause
exit /b
#>

# yt-dlp Auto-Update Script (Polyglot Edition)
# This file is a single-file solution that handles both batch execution and PowerShell logic.

$ErrorActionPreference = "Stop"
$currentDir = $pwd
$ytDlpPath = Join-Path $currentDir "yt-dlp.exe"
$ytDlpBackup = Join-Path $currentDir "yt-dlp.exe.bak"

Write-Host "--- yt-dlp Global Update Service ---" -ForegroundColor Cyan

# 1. Check current version
if (Test-Path $ytDlpPath) {
    try {
        $currentVersion = & $ytDlpPath --version 2>$null
        Write-Host "Current Version: $currentVersion" -ForegroundColor Gray
    } catch {
        Write-Host "Current version could not be determined." -ForegroundColor DarkGray
    }
    
    Write-Host "Creating backup..." -ForegroundColor Gray
    Copy-Item $ytDlpPath $ytDlpBackup -Force
}

# 2. Download latest yt-dlp
$ytDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe"
Write-Host "Downloading latest yt-dlp.exe from GitHub..." -ForegroundColor Yellow

try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $ytDlpUrl -OutFile $ytDlpPath -UserAgent "Mozilla/5.0" -TimeoutSec 60

    # 3. Verify yt-dlp download
    if (Test-Path $ytDlpPath) {
        $fileSize = (Get-Item $ytDlpPath).Length
        if ($fileSize -lt 10MB) {
            throw "Downloaded yt-dlp size is suspiciously small ($($fileSize / 1KB) KB). Update failed."
        }

        $newVersion = & $ytDlpPath --version 2>$null
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrEmpty($newVersion)) {
            throw "Verification failed: The downloaded yt-dlp is not a valid executable."
        }

        Write-Host "Successfully updated yt-dlp to version: $newVersion" -ForegroundColor Green
        if (Test-Path $ytDlpBackup) { Remove-Item $ytDlpBackup -Force }
    }
} catch {
    Write-Host "ERROR (yt-dlp): $_" -ForegroundColor Red
    if (Test-Path $ytDlpBackup) {
        Write-Host "Restoring backup yt-dlp version..." -ForegroundColor Yellow
        Move-Item $ytDlpBackup $ytDlpPath -Force
    }
}

# 4. Download and Extract FFmpeg
$ffmpegPath = Join-Path $currentDir "ffmpeg.exe"
$ffmpegZip = Join-Path $currentDir "ffmpeg.zip"
$ffmpegTempDir = Join-Path $currentDir "ffmpeg_temp"
$ffmpegUrl = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip"

Write-Host "`nDownloading latest FFmpeg from GitHub..." -ForegroundColor Yellow
try {
    Invoke-WebRequest -Uri $ffmpegUrl -OutFile $ffmpegZip -UserAgent "Mozilla/5.0" -TimeoutSec 120
    
    if (Test-Path $ffmpegTempDir) { Remove-Item $ffmpegTempDir -Recurse -Force }
    New-Item -ItemType Directory -Path $ffmpegTempDir | Out-Null
    
    Write-Host "Extracting FFmpeg..." -ForegroundColor Gray
    Expand-Archive -Path $ffmpegZip -DestinationPath $ffmpegTempDir -Force
    
    $extractedExe = Get-ChildItem -Path $ffmpegTempDir -Filter "ffmpeg.exe" -Recurse | Select-Object -First 1
    if ($extractedExe) {
        Move-Item -Path $extractedExe.FullName -Destination $ffmpegPath -Force
        Write-Host "FFmpeg updated successfully." -ForegroundColor Green
    } else {
        throw "ffmpeg.exe not found in downloaded archive."
    }
} catch {
    Write-Host "ERROR (FFmpeg): $_" -ForegroundColor Red
} finally {
    # Cleanup
    if (Test-Path $ffmpegZip) { Remove-Item $ffmpegZip -Force }
    if (Test-Path $ffmpegTempDir) { Remove-Item $ffmpegTempDir -Recurse -Force }
}

Write-Host "`nUpdate process finished." -ForegroundColor Cyan
