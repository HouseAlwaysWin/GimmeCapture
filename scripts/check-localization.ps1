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

# ── Phase 2: key USAGE ─────────────────────────────────────────────────────────
# Parity only proves the three JSON files agree with each other; this phase checks
# they agree with what the app actually requests.
#   FAIL  on keys referenced statically in code/XAML but missing from en-US.json
#         (those render blank/fallback at runtime).
#   WARN  on keys defined but apparently never used (heuristic: dynamic lookups
#         like Instance[$"Prefix{...}"] and EnumToLocalizedConverter parameters
#         are treated as prefix wildcards, and any literal appearance in source
#         counts as usage — so this list is advisory, not enforced).
$srcDir = Join-Path $root "src\GimmeCapture"
$csText = (Get-ChildItem -Path $srcDir -Recurse -Filter *.cs | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
$xamlText = (Get-ChildItem -Path $srcDir -Recurse -Filter *.axaml | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
$allText = $csText + $xamlText

$staticRefs = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
foreach ($m in [regex]::Matches($csText, 'Instance\["([A-Za-z0-9_]+)"\]')) { [void]$staticRefs.Add($m.Groups[1].Value) }
foreach ($m in [regex]::Matches($csText, 'SetStatus\("([A-Za-z0-9_]+)"\)')) { [void]$staticRefs.Add($m.Groups[1].Value) }
foreach ($m in [regex]::Matches($xamlText, 'Binding\s+\[([A-Za-z0-9_]+)\]')) { [void]$staticRefs.Add($m.Groups[1].Value) }

$prefixes = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
foreach ($m in [regex]::Matches($csText, 'Instance\[\$"([A-Za-z0-9_]+)\{')) { [void]$prefixes.Add($m.Groups[1].Value) }
foreach ($m in [regex]::Matches($xamlText, "ConverterParameter='([A-Za-z0-9_]+)'")) { [void]$prefixes.Add($m.Groups[1].Value) }
foreach ($m in [regex]::Matches($xamlText, 'ConverterParameter="([A-Za-z0-9_]+)"')) { [void]$prefixes.Add($m.Groups[1].Value) }

$missingRefs = @($staticRefs | Where-Object { -not $referenceKeys.Contains($_) } | Sort-Object)
if ($missingRefs.Count -gt 0) {
    throw "Keys referenced in code/XAML but missing from en-US.json (would be blank at runtime): $($missingRefs -join ', ')"
}

$unused = @()
foreach ($key in ($referenceKeys | Sort-Object)) {
    if ($staticRefs.Contains($key)) { continue }
    $prefixMatched = $false
    foreach ($p in $prefixes) { if ($key.StartsWith($p, [System.StringComparison]::Ordinal)) { $prefixMatched = $true; break } }
    if ($prefixMatched) { continue }
    if ($allText.Contains($key)) { continue }
    $unused += $key
}

if ($unused.Count -gt 0) {
    Write-Warning "Possibly unused localization keys ($($unused.Count)) - review and prune: $($unused -join ', ')"
}

Write-Host "Localization key usage passed ($($staticRefs.Count) static refs, $($prefixes.Count) dynamic prefixes)."
