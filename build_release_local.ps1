param(
    [switch]$Update
)

$ErrorActionPreference = "Stop"

if ($Update) {
    Write-Host "Updating dependencies before build..." -ForegroundColor Cyan
    & "$PSScriptRoot\update_dependencies.ps1"
}

# Path to Project File
$projectFile = Join-Path $PSScriptRoot "EdgeLoop\EdgeLoop.csproj"

if (-not (Test-Path $projectFile)) {
    Write-Error "Project file not found at $projectFile"
    exit 1
}

Write-Host "Starting Release Build..." -ForegroundColor Cyan

# 1. Cleanup
if (Test-Path "publish") {
    Write-Host "Cleaning up previous publish directory..."
    Remove-Item "publish" -Recurse -Force
}

# 2. Build and Publish
Write-Host "Building EdgeLoop project..." -ForegroundColor Yellow
dotnet publish EdgeLoop\EdgeLoop.csproj `
    -c Release `
    -r win-x64 `
    --self-contained false `
    -p:PublishSingleFile=false `
    -o publish

if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed!"
    exit 1
}

# 3. Copy Dependencies
Write-Host "Copying External Dependencies..." -ForegroundColor Yellow

if (-not (Test-Path "Dependencies\yt-dlp.exe")) {
    Write-Error "yt-dlp.exe not found in Dependencies folder!"
    exit 1
}

Copy-Item "Dependencies\yt-dlp.exe" "publish\yt-dlp.exe" -Force
Copy-Item "README.txt" "publish\README.txt" -Force

# 4. Create Data Folder
New-Item -ItemType Directory -Force -Path "publish\Data" | Out-Null

# 5. Verification
Write-Host "`nVerifying Artifacts..." -ForegroundColor Yellow
$edgeExe = Get-ChildItem "publish/Ed*.exe" | Select-Object -First 1

if ($edgeExe) {
    Write-Host "SUCCESS! Artifacts present." -ForegroundColor Green

    # 6. Final Cleanup
    Get-ChildItem "publish" -Recurse -Include *.xml, *.pdb, *.log, *.obj | Remove-Item -Force

    # 7. Create Zip Package
    Write-Host "`n[7] Creating EdgeLoop.zip package..." -ForegroundColor Yellow
    if (Test-Path "EdgeLoop.zip") { Remove-Item "EdgeLoop.zip" -Force }
    Compress-Archive -Path "publish\*" -DestinationPath "$PSScriptRoot\EdgeLoop.zip" -CompressionLevel Optimal
    
    Write-Host "`n✅ SUCCESS! Created EdgeLoop.zip" -ForegroundColor Green
}
else {
    Write-Error "Verification Failed!"
}
