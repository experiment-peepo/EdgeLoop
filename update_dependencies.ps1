# Update Dependencies Script
# Downloads the latest versions of yt-dlp and ffmpeg to the Dependencies folder.

$ErrorActionPreference = "Stop"

$depsDir = Join-Path $PSScriptRoot "Dependencies"
if (-not (Test-Path $depsDir)) {
    New-Item -ItemType Directory -Path $depsDir | Out-Null
}

Write-Host "Updating External Dependencies..." -ForegroundColor Cyan

# 1. Download yt-dlp
Write-Host "Fetching latest yt-dlp.exe..." -ForegroundColor Yellow
$ytDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe"
Invoke-WebRequest -Uri $ytDlpUrl -OutFile (Join-Path $depsDir "yt-dlp.exe") -UserAgent "Mozilla/5.0"
Write-Host "yt-dlp updated." -ForegroundColor Green

Write-Host "`n✅ Dependencies updated in folder: $depsDir" -ForegroundColor Green
