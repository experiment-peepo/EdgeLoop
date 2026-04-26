@echo off
powershell -NoProfile -ExecutionPolicy Bypass -Command "iex ((Get-Content '%~f0') -join \"`n\")"
exit /b

# yt-dlp Auto-Update Script (Polyglot Edition)
# This file is a single-file solution that handles both batch execution and PowerShell logic.

$ErrorActionPreference = "Stop"
try {
    # 0. Resolve correct paths based on the script's actual location
    $scriptFile = '%~f0'
    $currentDir = Split-Path -Path $scriptFile -Parent
    if ([string]::IsNullOrEmpty($currentDir)) { $currentDir = $pwd }
    
    $ytDlpPath = Join-Path $currentDir "yt-dlp.exe"
    $ytDlpBackup = Join-Path $currentDir "yt-dlp.exe.bak"
    
    Write-Host "--- EdgeLoop Dependency Update Service ---" -ForegroundColor Cyan
    Write-Host "Working Directory: $currentDir" -ForegroundColor Gray
    
    # 1. Check current version and remote version
    $needsUpdate = $true
    if (Test-Path $ytDlpPath) {
        try {
            $currentVersion = (& $ytDlpPath --version 2>$null).Trim()
            Write-Host "Current local yt-dlp: $currentVersion" -ForegroundColor Gray
            
            Write-Host "Checking for latest version on GitHub..." -ForegroundColor Gray
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls13
            $apiResponse = Invoke-RestMethod -Uri "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest" -UserAgent "Mozilla/5.0"
            $latestVersion = $apiResponse.tag_name
            
            # tag_name might be "2024.03.15" or "v2024.03.15", clean it
            $latestVersionClean = $latestVersion.TrimStart('v')
            
            if ($currentVersion -eq $latestVersionClean) {
                Write-Host "yt-dlp is already up to date ($currentVersion)." -ForegroundColor Green
                $needsUpdate = $false
            } else {
                Write-Host "New yt-dlp version available: $latestVersionClean" -ForegroundColor Yellow
            }
        } catch {
            Write-Host "Could not determine versions. Proceeding with fresh download." -ForegroundColor DarkGray
        }
    }
    
    if ($needsUpdate) {
        if (Test-Path $ytDlpPath) {
            Write-Host "Creating backup..." -ForegroundColor Gray
            Copy-Item $ytDlpPath $ytDlpBackup -Force
        }
    
        # 2. Download latest yt-dlp
        $ytDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe"
        Write-Host "Downloading latest yt-dlp.exe..." -ForegroundColor Yellow
    
        try {
            Invoke-WebRequest -Uri $ytDlpUrl -OutFile $ytDlpPath -UserAgent "Mozilla/5.0" -TimeoutSec 60
    
            # 3. Verify yt-dlp download
            if (Test-Path $ytDlpPath) {
                $fileSize = (Get-Item $ytDlpPath).Length
                if ($fileSize -lt 1MB) { # Changed from 10MB to be safer if they release a tiny version
                    throw "Downloaded yt-dlp size is suspiciously small ($($fileSize / 1KB) KB). Update failed."
                }
    
                $newVersion = (& $ytDlpPath --version 2>$null).Trim()
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
    }
    
    # 4. Download and Extract FFmpeg
    $ffmpegPath = Join-Path $currentDir "ffmpeg.exe"
    $ffmpegZip = Join-Path $currentDir "ffmpeg.zip"
    $ffmpegTempDir = Join-Path $currentDir "ffmpeg_temp"
    $ffmpegUrl = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip"
    
    Write-Host "`n--- FFmpeg Update Service ---" -ForegroundColor Cyan
    $needsFfmpegUpdate = $true
    
    if (Test-Path $ffmpegPath) {
        try {
            $ffmpegVerOutput = & $ffmpegPath -version 2>$null | Select-Object -First 1
            # Extract date like 20240321
            if ($ffmpegVerOutput -match "(\d{8})") {
                $localFfmpegDate = $matches[1]
                Write-Host "Current local FFmpeg build date: $localFfmpegDate" -ForegroundColor Gray
                
                Write-Host "Checking for latest FFmpeg on GitHub..." -ForegroundColor Gray
                $ffmpegApi = Invoke-RestMethod -Uri "https://api.github.com/repos/yt-dlp/FFmpeg-Builds/releases/latest" -UserAgent "Mozilla/5.0"
                $latestFfmpegTag = $ffmpegApi.tag_name
                
                # Extract date from tag like autobuild-2024-03-21-12-52
                if ($latestFfmpegTag -match "(\d{4})-(\d{2})-(\d{2})") {
                    $remoteFfmpegDate = "$($matches[1])$($matches[2])$($matches[3])"
                    
                    if ($localFfmpegDate -eq $remoteFfmpegDate) {
                        Write-Host "FFmpeg is already up to date ($localFfmpegDate)." -ForegroundColor Green
                        $needsFfmpegUpdate = $false
                    } else {
                        Write-Host "New FFmpeg build available: $remoteFfmpegDate" -ForegroundColor Yellow
                    }
                }
            }
        } catch {
            Write-Host "Could not determine FFmpeg version. Proceeding with fresh download." -ForegroundColor DarkGray
        }
    }
    
    if ($needsFfmpegUpdate) {
        Write-Host "Downloading latest FFmpeg from GitHub..." -ForegroundColor Yellow
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
    }
    
    Write-Host "`nUpdate process finished." -ForegroundColor Cyan

} catch {
    Write-Host "`nFATAL ERROR: $_" -ForegroundColor Red
    Write-Host "Stack Trace: $($_.ScriptStackTrace)" -ForegroundColor Gray
} finally {
    # Only pause if not running in CI environment (GitHub Actions)
    if ($null -eq $env:GITHUB_ACTIONS -and $null -eq $env:CI) {
        Write-Host "`nPress any key to exit..." -ForegroundColor Gray
        $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
    }
}
