# Downloads BtbN FFmpeg Windows x64 GPL *shared* build and extracts native DLLs into src/GimmeCapture/ffmpeg-lib/
# Run from repo root: powershell -ExecutionPolicy Bypass -File scripts/ensure-ffmpeg-libs.ps1
#
# Pinned to BtbN's non-pruned "latest" tag, n8.1 asset (FFmpeg 8.1 → avcodec-62/avformat-62/avutil-60,
# the ABI FFmpeg.AutoGen 8.0.0.1 binds to). The "latest" URL is stable (dated autobuild-* releases get
# pruned and 404), but BtbN rebuilds this asset on 8.1.x point releases — if the hash/size check below
# fails, refresh $expectedSha256/$expectedSize from the new asset.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $root "src\GimmeCapture\ffmpeg-lib"
New-Item -ItemType Directory -Force -Path $dest | Out-Null

$url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n8.1-latest-win64-gpl-shared-8.1.zip"
$expectedSha256 = "f91ffa113f52dbb7c38b1196dff1660dd7257f85f4368e7e2773f9e4bce3b1e8"
$expectedSize = 79235767
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
