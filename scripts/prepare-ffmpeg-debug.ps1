param(
    [string]$SourceRoot = $env:GIMMECAPTURE_FFMPEG_DIR,
    [string]$TargetRoot = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($TargetRoot)) {
    $TargetRoot = Join-Path $PSScriptRoot "..\src\GimmeCapture\bin\Debug\net10.0-windows\ffmpeg-lib"
}

if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    $SourceRoot = Join-Path $PSScriptRoot "..\src\GimmeCapture\ffmpeg-lib"
    Write-Host "GIMMECAPTURE_FFMPEG_DIR not set; defaulting to '$SourceRoot'"
}

$candidateDirs = @(
    $SourceRoot,
    (Join-Path $SourceRoot "bin"),
    (Join-Path $SourceRoot "ffmpeg-lib")
)

$resolvedSource = $candidateDirs |
    Where-Object { Test-Path $_ } |
    Where-Object { (Get-ChildItem $_ -Filter "avcodec-*.dll" -ErrorAction SilentlyContinue | Select-Object -First 1) -ne $null } |
    Select-Object -First 1

if ([string]::IsNullOrWhiteSpace($resolvedSource)) {
    throw "No FFmpeg shared DLLs found under '$SourceRoot'. Expected avcodec-*.dll in that folder, bin\, or ffmpeg-lib\."
}

New-Item -ItemType Directory -Force -Path $TargetRoot | Out-Null
Copy-Item (Join-Path $resolvedSource "*.dll") $TargetRoot -Force

$copied = Get-ChildItem $TargetRoot -Filter "*.dll" | Measure-Object | Select-Object -ExpandProperty Count
Write-Host "Prepared FFmpeg debug DLLs from '$resolvedSource' -> '$TargetRoot' ($copied files)."
