[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$localizationDir = Join-Path $root "src\GimmeCapture\Assets\Localization"
$files = @("en-US.json", "zh-TW.json", "ja-JP.json")
$keySets = @{}

foreach ($file in $files) {
    $path = Join-Path $localizationDir $file
    $json = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
    $keys = @($json.PSObject.Properties.Name)
    $keySets[$file] = [System.Collections.Generic.HashSet[string]]::new(
        [string[]]$keys,
        [System.StringComparer]::Ordinal)
}

$referenceFile = $files[0]
$referenceKeys = $keySets[$referenceFile]
$failures = @()

foreach ($file in $files | Select-Object -Skip 1) {
    $missing = @($referenceKeys | Where-Object { -not $keySets[$file].Contains($_) } | Sort-Object)
    $extra = @($keySets[$file] | Where-Object { -not $referenceKeys.Contains($_) } | Sort-Object)

    if ($missing.Count -gt 0) {
        $failures += "$file is missing: $($missing -join ', ')"
    }
    if ($extra.Count -gt 0) {
        $failures += "$file has extra keys: $($extra -join ', ')"
    }
}

if ($failures.Count -gt 0) {
    throw "Localization key parity failed.`n$($failures -join "`n")"
}

Write-Host "Localization key parity passed ($($referenceKeys.Count) keys)."
