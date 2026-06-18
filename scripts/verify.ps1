[CmdletBinding()]
param(
    [int]$CoverageThreshold = 20,
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root "GimmeCapture.slnx"
$appProject = Join-Path $root "src\GimmeCapture\GimmeCapture.csproj"
$testProject = Join-Path $root "tests\GimmeCapture.Tests\GimmeCapture.Tests.csproj"
$artifactsRoot = Join-Path $root "artifacts\verify"
$artifacts = Join-Path $artifactsRoot ([Guid]::NewGuid().ToString("N"))
$coverage = Join-Path $artifacts "coverage"
$publish = Join-Path $artifacts "publish-smoke"

function Invoke-DotNet {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
Write-Host "Verification artifacts: $artifacts"

Invoke-DotNet restore $solution `
    --locked-mode `
    --runtime win-x64 `
    --artifacts-path $artifacts `
    --disable-parallel `
    --disable-build-servers
Invoke-DotNet build $solution `
    --configuration Release `
    --no-restore `
    --artifacts-path $artifacts `
    --disable-build-servers `
    "-maxcpucount:1" `
    "-nodeReuse:false"

$testArgs = @(
    "test", $testProject,
    "--configuration", "Release",
    "--no-build",
    "--no-restore",
    "--artifacts-path", $artifacts,
    "--disable-build-servers",
    "-maxcpucount:1",
    "-nodeReuse:false",
    "--collect:XPlat Code Coverage",
    "--results-directory", $coverage
)
Invoke-DotNet @testArgs

$coverageFile = Get-ChildItem -Path $coverage -Filter "coverage.cobertura.xml" -Recurse -File |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1
if (-not $coverageFile) {
    throw "Coverage report was not generated."
}

[xml]$coverageXml = Get-Content -LiteralPath $coverageFile.FullName
$lineRate = [double]::Parse(
    $coverageXml.coverage.'line-rate',
    [Globalization.CultureInfo]::InvariantCulture)
$coveragePercent = [math]::Floor($lineRate * 100)
Write-Host "Line coverage: $coveragePercent%"
if ($coveragePercent -lt $CoverageThreshold) {
    throw "Line coverage $coveragePercent% is below threshold $CoverageThreshold%."
}

if (-not $SkipPublish) {
    $publishArgs = @(
        "publish", $appProject,
        "--configuration", "Release",
        "--runtime", "win-x64",
        "--self-contained", "true",
        "--no-restore",
        "--artifacts-path", $artifacts,
        "--disable-build-servers",
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:IncludeAllContentForSelfExtract=true",
        "-p:EnableCompressionInSingleFile=true",
        "-p:DebugType=none",
        "-p:DebugSymbols=false",
        "--output", $publish
    )
    Invoke-DotNet @publishArgs

    $publishedExe = Join-Path $publish "GimmeCapture.exe"
    if (-not (Test-Path -LiteralPath $publishedExe)) {
        throw "Publish smoke test did not produce GimmeCapture.exe."
    }
}

Write-Host "Verification completed successfully."
