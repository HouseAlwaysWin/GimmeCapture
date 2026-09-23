# Downloads BtbN FFmpeg Windows x64 GPL *shared* build and extracts native DLLs into src/GimmeCapture/ffmpeg-lib/
# Run from repo root: powershell -ExecutionPolicy Bypass -File scripts/ensure-ffmpeg-libs.ps1
#
# FFmpeg 8.1 (avcodec-62/avformat-62/avutil-60, the ABI FFmpeg.AutoGen 8.0.0.1 binds to), pinned by URL AND
# SHA-256 to one exact build mirrored in this repository — see $url below for why BtbN's own assets could not be
# pinned. Every build, local or CI, now bundles byte-identical DLLs, and a changed file fails loudly.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $root "src\GimmeCapture\ffmpeg-lib"
New-Item -ItemType Directory -Force -Path $dest | Out-Null

# Pinned to ONE exact build, mirrored in this repository's `deps-ffmpeg-n8.1-20260922` prerelease: BtbN's own
# "latest" asset is rebuilt continuously (its hash changes with every build) and its dated autobuild-* releases
# are pruned, so neither could be pinned directly. The mirror is byte-identical to BtbN's "Latest Auto-Build
# (2026-09-22 13:18)" — the release notes list BtbN's own checksums. To move to a newer FFmpeg, mirror the new
# build the same way and update the URL and hash together.
$url = "https://github.com/HouseAlwaysWin/GimmeCapture/releases/download/deps-ffmpeg-n8.1-20260922/ffmpeg-n8.1-20260922-win64-gpl-shared.zip"
$expectedSha256 = "393c050bd6515986c7ce6559c5bf69489d831b37b6cd68eb99ecfab4200688f6"
$zip = Join-Path $env:TEMP ("ffmpeg-shared-" + [Guid]::NewGuid().ToString("n") + ".zip")

Write-Host "Downloading $url ..."
Invoke-WebRequest -Uri $url -OutFile $zip

# The hash is the whole guarantee: a failed download (an HTML error page), a truncated file or a different build
# all fail here, before anything is extracted into the app.
$actualSha256 = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualSha256 -ne $expectedSha256) {
    Remove-Item $zip -Force -ErrorAction SilentlyContinue
    throw "FFmpeg archive SHA-256 mismatch: expected $expectedSha256, got $actualSha256. Refusing to use it."
}

$extract = Join-Path $env:TEMP ("ffmpeg-shared-" + [Guid]::NewGuid().ToString("n"))
Expand-Archive -Path $zip -DestinationPath $extract -Force

$binDir = Get-ChildItem -Path $extract -Recurse -Directory -Filter "bin" | Select-Object -First 1
if (-not $binDir) { throw "Could not find bin folder in extracted archive." }

# Validate the FFmpeg 8.x ABI the app binds to is present. The hash above already fixes the exact build; this
# catches the one mistake it cannot — re-pinning to a newer mirror whose FFmpeg major version the app does not bind.
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
