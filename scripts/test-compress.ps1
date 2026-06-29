<#
.SYNOPSIS
    Build + verify the Compress pipeline on Windows (the parts the cloud/Linux session cannot run).

.DESCRIPTION
    1. Ensures the bundled FFmpeg libs exist (build + runtime need them).
    2. Builds the solution (Debug) and runs the normal unit tests.
    3. Runs the gated real-encode matrix (CompressIntegrationTests) against a source video, which
       self-verifies resolution downscale, CRF size ordering, H.265, drop-audio, MKV-has-audio, and the
       ~5 MB target-size landing — all via the project's own libav probes (no ffprobe needed).

    Provide a real clip with -Source (e.g. your 4K file). If omitted, a short 4K test clip is generated
    with ffmpeg.exe when it is available on PATH.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts/test-compress.ps1 -Source "D:\影片\my4k.mp4"

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts/test-compress.ps1   # auto-generate a test clip
#>
param(
    [string]$Source,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo
$testProj = 'tests/GimmeCapture.Tests/GimmeCapture.Tests.csproj'

Write-Host '== [1/5] Ensuring bundled FFmpeg libs ==' -ForegroundColor Cyan
powershell -ExecutionPolicy Bypass -File scripts/ensure-ffmpeg-libs.ps1

if (-not $SkipBuild) {
    Write-Host '== [2/5] Building (Debug) ==' -ForegroundColor Cyan
    dotnet build GimmeCapture.slnx -c Debug
}

Write-Host '== [3/5] Unit tests (excluding the encode matrix) ==' -ForegroundColor Cyan
dotnet test $testProj -c Debug --filter 'FullyQualifiedName!~CompressIntegration'

# --- Resolve a source clip -------------------------------------------------
$work = Join-Path $env:TEMP 'gimme_compress_test'
New-Item -ItemType Directory -Force -Path $work | Out-Null

if (-not $Source) {
    $ffmpegCmd = Get-Command ffmpeg -ErrorAction SilentlyContinue
    $ffmpeg = if ($ffmpegCmd) { $ffmpegCmd.Source } else { $null }
    if (-not $ffmpeg) {
        Write-Host 'No -Source given and ffmpeg.exe is not on PATH to generate one.' -ForegroundColor Yellow
        Write-Host 'Re-run with your own clip, e.g.:' -ForegroundColor Yellow
        Write-Host '  powershell -ExecutionPolicy Bypass -File scripts/test-compress.ps1 -Source "D:\path\to\video.mp4"'
        exit 1
    }
    $Source = Join-Path $work 'src4k.mp4'
    Write-Host "== [4/5] Generating a short 4K test clip -> $Source ==" -ForegroundColor Cyan
    & $ffmpeg -y -f lavfi -i 'testsrc=size=3840x2160:rate=30' -f lavfi -i 'sine=frequency=440' `
        -t 8 -c:v libx264 -preset veryfast -pix_fmt yuv420p -c:a aac -shortest $Source | Out-Null
}
else {
    if (-not (Test-Path $Source)) { throw "Source not found: $Source" }
    Write-Host "== [4/5] Using source: $Source ==" -ForegroundColor Cyan
}

# --- Run the real-encode matrix -------------------------------------------
$outDir = Join-Path $work 'out'
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$env:COMPRESS_IT_SOURCE = $Source
$env:COMPRESS_IT_OUTDIR = $outDir

Write-Host '== [5/5] Real-encode matrix (self-verifying) ==' -ForegroundColor Cyan
dotnet test $testProj -c Debug --filter 'FullyQualifiedName~CompressIntegration'
$rc = $LASTEXITCODE

Remove-Item Env:\COMPRESS_IT_SOURCE, Env:\COMPRESS_IT_OUTDIR -ErrorAction SilentlyContinue

Write-Host ''
Write-Host "Outputs written to: $outDir" -ForegroundColor Green
if (Test-Path $outDir) {
    Get-ChildItem $outDir | Select-Object Name, @{N = 'MB'; E = { [math]::Round($_.Length / 1MB, 2) } } | Format-Table -AutoSize
}

if ($rc -ne 0) { Write-Host 'Encode matrix FAILED — see assertion output above.' -ForegroundColor Red; exit $rc }
Write-Host 'Compress pipeline verified.' -ForegroundColor Green
