[CmdletBinding()]
param(
    [int]$CoverageThreshold = 30,
    [switch]$SkipPublish,
    # Keep this run's artifacts even when it passes. A run costs several GB (a full build tree plus two
    # self-contained publishes), so passing runs are deleted by default; use this when you want to
    # inspect the build output or the coverage report of a run that succeeded.
    [switch]$KeepArtifacts
)

# Previous runs left behind are FAILED or interrupted ones (a passing run removes itself at the end), kept for
# post-mortem. Keep only the most recent few: thirteen accumulated runs had reached 79 GB before any of this
# existed, because nothing ever deleted them.
$KeepFailedRunCount = 2

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root "GimmeCapture.slnx"
$appProject = Join-Path $root "src\GimmeCapture\GimmeCapture.csproj"
$testProject = Join-Path $root "tests\GimmeCapture.Tests\GimmeCapture.Tests.csproj"
$coverageSettings = Join-Path $root "tests\GimmeCapture.Tests\coverage.runsettings"
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

# Pruned BEFORE this run's directory is created, so the count is purely about previous runs.
if (Test-Path $artifactsRoot) {
    Get-ChildItem $artifactsRoot -Directory -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -Skip $KeepFailedRunCount |
        ForEach-Object {
            Write-Host "Pruning artifacts from an earlier run: $($_.Name)"
            Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
        }
}

New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
Write-Host "Verification artifacts: $artifacts"

& powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "check-localization.ps1")
if ($LASTEXITCODE -ne 0) {
    throw "Localization parity check failed with exit code $LASTEXITCODE."
}

# No --runtime flag: it would override the projects' RuntimeIdentifiers (win-x64, plus
# linux-x64 on the net10.0 head) and locked mode would then flag the lock file's
# linux-x64 sections as a mismatch. Project-driven RIDs already cover win-x64.
Invoke-DotNet restore $solution `
    --locked-mode `
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
    # The Tests project multi-targets (net10.0-windows;net10.0) for the Linux CI gate. Locally we
    # only run the Windows head — the net10.0 head is exercised by linux.yml on every push, and
    # running both here would double the test (and coverage) time for no extra signal on Windows.
    "--framework", "net10.0-windows10.0.19041.0",
    "--configuration", "Release",
    "--no-build",
    "--no-restore",
    "--artifacts-path", $artifacts,
    "--disable-build-servers",
    "-maxcpucount:1",
    "-nodeReuse:false",
    # Leaves the code the XAML compiler generates from .axaml files out of the coverage figure.
    "--settings", $coverageSettings,
    "--collect:XPlat Code Coverage",
    # A test stuck for 10 minutes is dumped and stopped instead of holding the run until the job's timeout, and a
    # test host that dies names the test it was running.
    "--blame-hang", "--blame-hang-timeout", "10m",
    "--results-directory", $coverage
)
Invoke-DotNet @testArgs

$coverageFile = Get-ChildItem -Path $coverage -Filter "coverage.cobertura.xml" -Recurse -File |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1
if (-not $coverageFile) {
    throw "Coverage report was not generated."
}

# Keep a copy OUTSIDE this run's directory: a passing run deletes that directory at the end, and CI uploads the
# report only after this script returns — so every green CI run used to upload nothing at all.
$latestCoverageDir = Join-Path $root "artifacts\coverage"
New-Item -ItemType Directory -Force -Path $latestCoverageDir | Out-Null
Copy-Item -LiteralPath $coverageFile.FullName -Destination (Join-Path $latestCoverageDir "coverage.cobertura.xml") -Force

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
        # The project is multi-targeted (net10.0-windows;net10.0) since the Linux port, so publish must
        # name a framework — the win-x64 smoke uses the Windows TFM. Without this, dotnet publish errors
        # NETSDK1129 ("Publish not supported without specifying a target framework").
        "--framework", "net10.0-windows10.0.19041.0",
        "--configuration", "Release",
        # Properties/PublishProfiles/win-x64.pubxml, the profile release.yml publishes the Windows artifact with.
        "-p:PublishProfile=win-x64",
        "--no-restore",
        "--artifacts-path", $artifacts,
        "--disable-build-servers",
        "--output", $publish
    )
    Invoke-DotNet @publishArgs

    # The Windows release is a folder: the exe is only the launcher, and the app and FFmpeg sit beside it.
    foreach ($expected in @("GimmeCapture.exe", "GimmeCapture.dll", "ffmpeg-lib")) {
        if (-not (Test-Path -LiteralPath (Join-Path $publish $expected))) {
            throw "Publish smoke test did not produce $expected."
        }
    }

    # Also smoke the LINUX release artifact (net10.0 head, linux-x64 self-contained single-file). This is the
    # exact build that blocks a release in .github/workflows/release.yml, and verify used to skip it — so a
    # Linux-publish break (like the locked-restore NU1004 that sank the first v0.66.0 attempt) only surfaced
    # AFTER the tag was pushed. Running it here means "one command, then release" is trustworthy again.
    # Mirrors release.yml: --no-restore off the locked solution restore above. (Letting publish restore itself
    # in the self-contained + RID context drags in an SDK-versioned implicit Microsoft.NET.ILLink.Tasks that
    # locked mode rejects — that is exactly the failure we want caught locally.) Cross-RID publish from Windows
    # is supported; the SDK uses the linux-x64 runtime pack the solution restore already fetched.
    $publishLinux = Join-Path $artifacts "publish-smoke-linux"
    $publishLinuxArgs = @(
        "publish", $appProject,
        "--framework", "net10.0",
        "--configuration", "Release",
        "-p:PublishProfile=linux-x64",
        "--no-restore",
        "--artifacts-path", $artifacts,
        "--disable-build-servers",
        "--output", $publishLinux
    )
    Invoke-DotNet @publishLinuxArgs

    # The Linux single-file host has no extension.
    $publishedElf = Join-Path $publishLinux "GimmeCapture"
    if (-not (Test-Path -LiteralPath $publishedElf)) {
        throw "Linux publish smoke test did not produce the GimmeCapture executable."
    }
}

Write-Host "Verification completed successfully."

# Reached only when everything above passed: $ErrorActionPreference = "Stop" plus the throws in Invoke-DotNet
# abort the script otherwise. That asymmetry is deliberate — a FAILED run keeps its directory so the build log,
# the coverage report and the publish output are still there to look at, while a passing run has nothing worth
# several GB of disk.
if ($KeepArtifacts) {
    Write-Host "Artifacts kept at: $artifacts"
}
else {
    Remove-Item $artifacts -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path $artifacts) {
        # Never fail the run over cleanup, but do not let it fail silently either — silent failure here is how
        # the pile grew in the first place.
        Write-Warning "Could not fully remove $artifacts. Delete it manually."
    }
}
