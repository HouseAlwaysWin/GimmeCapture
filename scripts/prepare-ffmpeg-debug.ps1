param(
    [string]$SourceRoot = $env:GIMMECAPTURE_FFMPEG_DIR,
    [string]$TargetRoot = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($TargetRoot)) {
    $TargetRoot = Join-Path $PSScriptRoot "..\src\GimmeCapture\bin\Debug\net10.0-windows\ffmpeg-lib"
}

if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    $envFile = Join-Path $PSScriptRoot "..\.vscode\debug.env"
    if (Test-Path $envFile) {
        foreach ($line in Get-Content $envFile) {
            $trimmed = $line.Trim()
            if ([string]::IsNullOrWhiteSpace($trimmed) -or $trimmed.StartsWith("#")) {
                continue
            }

            $parts = $trimmed -split "=", 2
            if ($parts.Length -eq 2 -and $parts[0].Trim() -eq "GIMMECAPTURE_FFMPEG_DIR") {
                $SourceRoot = $parts[1].Trim()
                break
            }
        }
    }
}

if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    throw "GIMMECAPTURE_FFMPEG_DIR is not set. Put it in .vscode\debug.env or point it at the FFmpeg shared-DLL folder, or a parent folder containing bin\."
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
