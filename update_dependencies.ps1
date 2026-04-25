# Update Dependencies Script
# Downloads the latest versions of yt-dlp to the Dependencies folder.
# Verifies the download and reports the installed version.

$ErrorActionPreference = "Stop"

$depsDir = Join-Path $PSScriptRoot "Dependencies"
if (-not (Test-Path $depsDir)) {
    New-Item -ItemType Directory -Path $depsDir | Out-Null
}

Write-Host "Updating External Dependencies..." -ForegroundColor Cyan

# 1. Download yt-dlp
$ytDlpPath = Join-Path $depsDir "yt-dlp.exe"
$ytDlpBackup = Join-Path $depsDir "yt-dlp.exe.bak"

# Check current version and remote version
$needsUpdate = $true
if (Test-Path $ytDlpPath) {
    try {
        $currentVersion = (& $ytDlpPath --version 2>$null).Trim()
        Write-Host "Current local yt-dlp version: $currentVersion" -ForegroundColor Gray
        
        Write-Host "Checking for latest yt-dlp on GitHub..." -ForegroundColor Gray
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $apiResponse = Invoke-RestMethod -Uri "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest" -UserAgent "Mozilla/5.0"
        $latestVersion = $apiResponse.tag_name
        
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

    Write-Host "Fetching latest yt-dlp.exe..." -ForegroundColor Yellow
    try {
        $ytDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe"
        Invoke-WebRequest -Uri $ytDlpUrl -OutFile $ytDlpPath -UserAgent "Mozilla/5.0"

        # Verify the download
        if (Test-Path $ytDlpPath) {
            $fileInfo = Get-Item $ytDlpPath
            if ($fileInfo.Length -lt 10MB) {
                throw "Downloaded file is too small ($($fileInfo.Length) bytes) - likely corrupted"
            }

            $newVersion = (& $ytDlpPath --version 2>$null).Trim()
            if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrEmpty($newVersion)) {
                throw "Downloaded yt-dlp.exe failed version check"
            }

            Write-Host "yt-dlp updated to version: $newVersion" -ForegroundColor Green
            if (Test-Path $ytDlpBackup) { Remove-Item $ytDlpBackup -Force }
        }
    } catch {
        Write-Host "ERROR: Failed to update yt-dlp: $_" -ForegroundColor Red
        if (Test-Path $ytDlpBackup) {
            Write-Host "Restoring previous version..." -ForegroundColor Yellow
            Move-Item $ytDlpBackup $ytDlpPath -Force
        }
    }
}

# 2. Download and Extract FFmpeg
$ffmpegPath = Join-Path $depsDir "ffmpeg.exe"
$ffmpegZip = Join-Path $depsDir "ffmpeg.zip"
$ffmpegTempDir = Join-Path $depsDir "ffmpeg_temp"
$ffmpegUrl = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip"

Write-Host "`n--- FFmpeg Global Update Service ---" -ForegroundColor Cyan
$needsFfmpegUpdate = $true

if (Test-Path $ffmpegPath) {
    try {
        $ffmpegVerOutput = & $ffmpegPath -version 2>$null | Select-Object -First 1
        if ($ffmpegVerOutput -match "(\d{8})") {
            $localFfmpegDate = $matches[1]
            Write-Host "Current FFmpeg build date: $localFfmpegDate" -ForegroundColor Gray
            
            Write-Host "Checking for latest FFmpeg on GitHub..." -ForegroundColor Gray
            $ffmpegApi = Invoke-RestMethod -Uri "https://api.github.com/repos/yt-dlp/FFmpeg-Builds/releases/latest" -UserAgent "Mozilla/5.0"
            $latestFfmpegTag = $ffmpegApi.tag_name
            
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
    Write-Host "Fetching latest FFmpeg..." -ForegroundColor Yellow
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
        Write-Host "ERROR: Failed to update FFmpeg: $_" -ForegroundColor Red
    } finally {
        # Cleanup
        if (Test-Path $ffmpegZip) { Remove-Item $ffmpegZip -Force }
        if (Test-Path $ffmpegTempDir) { Remove-Item $ffmpegTempDir -Recurse -Force }
    }
}

Write-Host "`n`u{2705} Dependencies updated in folder: $depsDir" -ForegroundColor Green
