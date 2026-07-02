[CmdletBinding()]
param([string]$version)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Command,

        [Parameter(ValueFromRemainingArguments = $true)]
        [string[]]$Arguments
    )

    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Command $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

if (-not $version) {
    $version = Read-Host "Enter version to release (e.g., v1.0.0)"
}

if ($version -notmatch '^v\d+\.\d+\.\d+(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?$') {
    throw "Version must use semantic version format and start with 'v' (e.g., v1.0.0)."
}

$branch = (& git branch --show-current).Trim()
if ($LASTEXITCODE -ne 0) {
    throw "Unable to determine the current Git branch."
}
if ($branch -ne "main") {
    throw "Releases must be created from main. Current branch: $branch"
}

$initialStatus = @(git status --porcelain)
if ($LASTEXITCODE -ne 0) {
    throw "Unable to inspect the Git working tree."
}
if ($initialStatus.Count -gt 0) {
    throw "The working tree must be clean before starting a release."
}

if (git tag -l $version) {
    throw "Tag $version already exists locally. Use a new version instead of moving an existing release tag."
}

& git ls-remote --exit-code --tags origin "refs/tags/$version" | Out-Null
$remoteTagExitCode = $LASTEXITCODE
if ($remoteTagExitCode -eq 0) {
    throw "Tag $version already exists on origin. Use a new version instead of moving an existing release tag."
}
if ($remoteTagExitCode -ne 2) {
    throw "Unable to check whether tag $version exists on origin."
}

# The release commit only ever touches the csproj (any other dirty file aborts), so
# CHANGELOG.md can never be updated during the release itself — it must be committed
# BEFORE running this script. This gate stops the changelog from silently rotting.
$changelog = Get-Content -LiteralPath "CHANGELOG.md" -Raw
$versionPattern = [regex]::Escape($version.TrimStart('v'))
if ($changelog -notmatch "(^|\n)##[^\n]*\bv?$versionPattern\b") {
    throw ("CHANGELOG.md has no section for $version. Add a '## $version' entry (own commit) " +
        "before releasing, so the user-facing release log stays current.")
}
$releasesCatalog = "docs/catalog/releases.md"
if ((Test-Path $releasesCatalog) -and ((Get-Content -LiteralPath $releasesCatalog -Raw) -notmatch "\bv?$versionPattern\b")) {
    Write-Warning "$releasesCatalog has no entry for $version - consider updating it (not enforced)."
}

Write-Host "Starting release process for $version..." -ForegroundColor Cyan

$csprojPath = "src/GimmeCapture/GimmeCapture.csproj"
$verifyScript = "scripts/verify.ps1"
$rollbackFiles = @(
    "src/GimmeCapture/GimmeCapture.csproj",
    "src/GimmeCapture/packages.lock.json",
    "tests/GimmeCapture.Tests/packages.lock.json",
    "tests/GimmeCapture.Benchmarks/packages.lock.json"
)
$versionPlain = $version.TrimStart('v')
$inheritedVersion = [Environment]::GetEnvironmentVariable("VERSION", "Process")
$releaseCommitted = $false

try {
    # MSBuild imports process environment variables as properties. A VERSION
    # value such as v0.44.0 would override the valid Version in the project.
    [Environment]::SetEnvironmentVariable("VERSION", $null, "Process")

    Write-Host "Verifying current main before changing the release version..." -ForegroundColor Gray
    & $verifyScript

    $verifyChanges = @(git status --porcelain)
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to inspect verification changes."
    }
    if ($verifyChanges.Count -gt 0) {
        throw "Verification modified tracked files: $($verifyChanges -join ', ')"
    }

    Write-Host "Updating $csprojPath to version $versionPlain..." -ForegroundColor Gray
    $csproj = Get-Content -LiteralPath $csprojPath -Raw
    $updatedCsproj = [regex]::Replace(
        $csproj,
        '<Version>[^<]*</Version>',
        "<Version>$versionPlain</Version>",
        1)
    if ($updatedCsproj -eq $csproj) {
        throw "Version $versionPlain is already set or the Version element was not found."
    }
    [IO.File]::WriteAllText(
        (Resolve-Path $csprojPath),
        $updatedCsproj,
        [Text.UTF8Encoding]::new($false))

    $changedFiles = @(git status --porcelain | ForEach-Object { $_.Substring(3) })
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to inspect release changes."
    }
    $unexpectedFiles = @($changedFiles | Where-Object { $_ -ne $csprojPath })
    if ($unexpectedFiles.Count -gt 0) {
        throw "Release produced unexpected changes: $($unexpectedFiles -join ', ')"
    }

    & git add -- $csprojPath
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to stage release files."
    }

    Invoke-Checked -Command "git" -Arguments @(
        "commit",
        "-m", "chore: release $version"
    )
    $releaseCommitted = $true

    Invoke-Checked -Command "git" -Arguments @(
        "tag",
        "-a", $version,
        "-m", "Release $version"
    )

    Write-Host "Pushing release commit and tag to GitHub..." -ForegroundColor Cyan
    Invoke-Checked -Command "git" -Arguments @(
        "push",
        "--atomic",
        "origin",
        "main",
        $version
    )

    Write-Host "Successfully triggered release! Check GitHub Actions." -ForegroundColor Green
}
catch {
    if (-not $releaseCommitted) {
        Write-Warning "Release failed before commit. Reverting generated release changes."
        & dotnet build-server shutdown | Out-Null
        & git restore --source=HEAD --staged --worktree -- $rollbackFiles
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "Automatic rollback failed. Inspect the working tree before retrying."
        }
    }

    throw
}
finally {
    [Environment]::SetEnvironmentVariable("VERSION", $inheritedVersion, "Process")
}
