# Downloads BtbN FFmpeg Windows x64 GPL *shared* build and extracts native DLLs into src/GimmeCapture/ffmpeg-lib/
# Run from repo root: powershell -ExecutionPolicy Bypass -File scripts/ensure-ffmpeg-libs.ps1
#
# Pinned to BtbN's non-pruned "latest" tag, n8.1 asset (FFmpeg 8.1 -> avcodec-62/avformat-62/avutil-60,
# the ABI FFmpeg.AutoGen 8.0.0.1 binds to). The "latest" URL never 404s (dated autobuild-* releases get
# pruned), but BtbN REBUILDS this asset on 8.1.x point releases, so its exact byte size / SHA-256 drift.
# Pinning an exact hash/size therefore breaks CI on every upstream rebuild. Instead we validate the
# download FUNCTIONALLY: a sane archive size + the expected FFmpeg 8.x ABI DLLs present after extraction.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $root "src\GimmeCapture\ffmpeg-lib"
New-Item -ItemType Directory -Force -Path $dest | Out-Null

$url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n8.1-latest-win64-gpl-shared-8.1.zip"
$zip = Join-Path $env:TEMP ("ffmpeg-shared-" + [Guid]::NewGuid().ToString("n") + ".zip")

Write-Host "Downloading $url ..."
Invoke-WebRequest -Uri $url -OutFile $zip

# Sanity check: a real archive is tens of MB. Anything tiny is a failed download (e.g. an HTML error page).
$actualSize = (Get-Item -LiteralPath $zip).Length
if ($actualSize -lt 20MB) {
    throw "FFmpeg archive looks wrong: only $actualSize bytes (expected tens of MB). Download likely failed."
}

$extract = Join-Path $env:TEMP ("ffmpeg-shared-" + [Guid]::NewGuid().ToString("n"))
Expand-Archive -Path $zip -DestinationPath $extract -Force

$binDir = Get-ChildItem -Path $extract -Recurse -Directory -Filter "bin" | Select-Object -First 1
if (-not $binDir) { throw "Could not find bin folder in extracted archive." }

# Validate the FFmpeg 8.x ABI the app binds to is present. This catches a wrong/corrupt build (or an
# upstream major-version bump) without pinning a brittle exact hash against the rolling 'latest' asset.
$requiredDlls = @("avcodec-62.dll", "avformat-62.dll", "avutil-60.dll")
foreach ($name in $requiredDlls) {
    if (-not (Test-Path -LiteralPath (Join-Path $binDir.FullName $name))) {
        throw "Expected FFmpeg 8.x DLL '$name' not found in the download (wrong/changed build). bin: $($binDir.FullName)"
    }
}

Get-ChildItem -Path $binDir.FullName -Filter "*.dll" | ForEach-Object {
    Copy-Item $_.FullName -Destination (Join-Path $dest $_.Name) -Force
    Write-Host "Copied $($_.Name)"
}

Remove-Item $zip -Force -ErrorAction SilentlyContinue
Remove-Item $extract -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Done. FFmpeg DLLs are in $dest"
