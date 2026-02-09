param([string]$version)

if (-not $version) {
    $version = Read-Host "Enter version to release (e.g., v1.0.0)"
}

if (-not $version.StartsWith("v")) {
    Write-Host "Error: Version must start with 'v' (e.g., v1.0.0)" -ForegroundColor Red
    exit 1
}


Write-Host "Starting release process for $version..." -ForegroundColor Cyan

# 1. Update Version in .csproj
$csprojPath = "src/GimmeCapture/GimmeCapture.csproj"
$versionPlain = $version.TrimStart('v')
Write-Host "Updating $csprojPath to version $versionPlain..." -ForegroundColor Gray
(Get-Content $csprojPath) -replace '<Version>.*</Version>', "<Version>$versionPlain</Version>" | Set-Content $csprojPath


# 3. Commit changes
git add .
$status = git status --porcelain
if ($status) {
    git commit -m "chore: release $version"
}

# 4. Add Tag
# Check if tag exists locally
if (git tag -l $version) {
    Write-Host "Warning: Tag $version already exists locally. Deleting and recreating..." -ForegroundColor Yellow
    git tag -d $version
}

git tag -a $version -m "Release $version"

# 5. Push to GitHub
Write-Host "Pushing to GitHub..." -ForegroundColor Cyan
git push origin main --follow-tags

Write-Host "Successfully triggered release! Check GitHub Actions tab." -ForegroundColor Green
