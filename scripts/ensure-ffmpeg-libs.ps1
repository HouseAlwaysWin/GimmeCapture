# Downloads BtbN FFmpeg Windows x64 GPL *shared* build and extracts native DLLs into src/GimmeCapture/ffmpeg-lib/
# Run from repo root: powershell -ExecutionPolicy Bypass -File scripts/ensure-ffmpeg-libs.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $root "src\GimmeCapture\ffmpeg-lib"
New-Item -ItemType Directory -Force -Path $dest | Out-Null

$url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl-shared.zip"
$zip = Join-Path $env:TEMP ("ffmpeg-shared-" + [Guid]::NewGuid().ToString("n") + ".zip")

Write-Host "Downloading $url ..."
Invoke-WebRequest -Uri $url -OutFile $zip

$extract = Join-Path $env:TEMP ("ffmpeg-shared-" + [Guid]::NewGuid().ToString("n"))
Expand-Archive -Path $zip -DestinationPath $extract -Force

$binDir = Get-ChildItem -Path $extract -Recurse -Directory -Filter "bin" | Select-Object -First 1
if (-not $binDir) { throw "Could not find bin folder in extracted archive." }

Get-ChildItem -Path $binDir.FullName -Filter "*.dll" | ForEach-Object {
    Copy-Item $_.FullName -Destination (Join-Path $dest $_.Name) -Force
    Write-Host "Copied $($_.Name)"
}

Remove-Item $zip -Force -ErrorAction SilentlyContinue
Remove-Item $extract -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Done. FFmpeg DLLs are in $dest"
