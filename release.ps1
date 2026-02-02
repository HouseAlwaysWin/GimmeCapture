param([string]$version)

if (-not $version) {
    $version = Read-Host "Enter version to release (e.g., v1.0.0)"
}

if (-not $version.StartsWith("v")) {
    Write-Host "Error: Version must start with 'v' (e.g., v1.0.0)" -ForegroundColor Red
    exit 1
}

Write-Host "Starting release for $version..." -ForegroundColor Cyan

# 1. Commit any remaining changes
git add .
$status = git status --porcelain
if ($status) {
    git commit -m "chore: prepare release $version"
} else {
    Write-Host "No changes to commit, proceeding with tag." -ForegroundColor Yellow
}

# 2. Add Tag
# Check if tag exists locally
if (git tag -l $version) {
    Write-Host "Warning: Tag $version already exists locally. Deleting and recreating..." -ForegroundColor Yellow
    git tag -d $version
}
git tag $version

# 3. Push to GitHub
Write-Host "Pushing to GitHub..." -ForegroundColor Cyan
git push origin main --tags

Write-Host "Successfully triggered release! Check GitHub Actions tab." -ForegroundColor Green
