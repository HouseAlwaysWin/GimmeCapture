# Downloads BtbN FFmpeg Windows x64 GPL *shared* build and extracts native DLLs into src/GimmeCapture/ffmpeg-lib/
# Run from repo root: powershell -ExecutionPolicy Bypass -File scripts/ensure-ffmpeg-libs.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $root "src\GimmeCapture\ffmpeg-lib"
New-Item -ItemType Directory -Force -Path $dest | Out-Null

$url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-06-14-13-33/ffmpeg-N-125019-g6698195dc4-win64-gpl-shared.zip"
$expectedSha256 = "6ca2179b68ffb2658cc33ea2cb82292ab89f413d957cd44ef19b1fa5e64f3e70"
$expectedSize = 97774950
$zip = Join-Path $env:TEMP ("ffmpeg-shared-" + [Guid]::NewGuid().ToString("n") + ".zip")

Write-Host "Downloading $url ..."
Invoke-WebRequest -Uri $url -OutFile $zip

$actualSize = (Get-Item -LiteralPath $zip).Length
if ($actualSize -ne $expectedSize) {
    throw "FFmpeg archive size mismatch. Expected $expectedSize bytes, received $actualSize bytes."
}

$actualSha256 = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualSha256 -ne $expectedSha256) {
    throw "FFmpeg archive SHA-256 mismatch. Expected $expectedSha256, received $actualSha256."
}

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
