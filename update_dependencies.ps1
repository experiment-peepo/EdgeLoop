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

# Backup existing version before updating
if (Test-Path $ytDlpPath) {
    $oldVersion = & $ytDlpPath --version 2>$null
    Write-Host "Current yt-dlp version: $oldVersion" -ForegroundColor Gray
    Copy-Item $ytDlpPath $ytDlpBackup -Force
}

Write-Host "Fetching latest yt-dlp.exe..." -ForegroundColor Yellow
try {
    $ytDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe"
    Invoke-WebRequest -Uri $ytDlpUrl -OutFile $ytDlpPath -UserAgent "Mozilla/5.0"

    # Verify the download
    if (Test-Path $ytDlpPath) {
        $fileInfo = Get-Item $ytDlpPath
        if ($fileInfo.Length -lt 1MB) {
            throw "Downloaded file is too small ($($fileInfo.Length) bytes) - likely corrupted"
        }

        $newVersion = & $ytDlpPath --version 2>$null
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrEmpty($newVersion)) {
            throw "Downloaded yt-dlp.exe failed version check"
        }

        Write-Host "yt-dlp updated to version: $newVersion" -ForegroundColor Green

        # Clean up backup on success
        if (Test-Path $ytDlpBackup) {
            Remove-Item $ytDlpBackup -Force
        }
    }
} catch {
    Write-Host "ERROR: Failed to update yt-dlp: $_" -ForegroundColor Red

    # Restore backup if available
    if (Test-Path $ytDlpBackup) {
        Write-Host "Restoring previous version..." -ForegroundColor Yellow
        Move-Item $ytDlpBackup $ytDlpPath -Force
        Write-Host "Previous version restored." -ForegroundColor Yellow
    }
}

Write-Host "`n`u{2705} Dependencies updated in folder: $depsDir" -ForegroundColor Green
