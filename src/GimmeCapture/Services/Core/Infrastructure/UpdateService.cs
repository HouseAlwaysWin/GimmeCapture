using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Threading;
using GimmeCapture.Services.Abstractions;

namespace GimmeCapture.Services.Core.Infrastructure;

public sealed class ReleaseInfo
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("published_at")]
    public DateTimeOffset? PublishedAt { get; set; }

    [JsonPropertyName("assets")]
    public List<ReleaseAsset> Assets { get; set; } = new();

    [JsonIgnore]
    public string NormalizedVersion => TagName.TrimStart('v');

    /// <summary>
    /// The release artifact for the running OS: the win-x64 <c>.zip</c> on Windows, the linux-x64
    /// <c>.tar.gz</c> on Linux. (Name kept for compatibility; it returns whatever the platform installs.)
    /// </summary>
    public ReleaseAsset? GetPreferredZipAsset()
    {
        if (!OperatingSystem.IsWindows())
        {
            // Linux ships a self-contained single-file tarball.
            return Assets.Find(a => a.Name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
                                    && a.Name.Contains("linux", StringComparison.OrdinalIgnoreCase))
                   ?? Assets.Find(a => a.Name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase));
        }

        return Assets.Find(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                                && a.Name.Contains("win", StringComparison.OrdinalIgnoreCase))
               ?? Assets.Find(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
    }

    public ReleaseAsset? GetChecksumAsset() =>
        Assets.Find(a => string.Equals(a.Name, "SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase));
}

public sealed class ReleaseAsset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("browser_download_url")]
    public string DownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

public sealed class PendingUpdateState
{
    public string TargetVersion { get; set; } = string.Empty;
    public string ExpectedExePath { get; set; } = string.Empty;
    public string AppDirectory { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Persisted record of a completed update download so it survives app restarts. Lets the user pick
/// "Later" and still install directly afterwards without re-downloading.
/// </summary>
public sealed class CachedDownloadState
{
    public string TargetVersion { get; set; } = string.Empty;
    public string ZipPath { get; set; } = string.Empty;
    public DateTimeOffset CachedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class UpdateVerificationResult
{
    public bool HasPendingUpdate { get; init; }
    public bool IsSuccess { get; init; }
    public string? FailureMessage { get; init; }
    public PendingUpdateState? State { get; init; }

    public static UpdateVerificationResult None() => new() { HasPendingUpdate = false, IsSuccess = true };
    public static UpdateVerificationResult Success(PendingUpdateState state) => new() { HasPendingUpdate = true, IsSuccess = true, State = state };
    public static UpdateVerificationResult Failure(PendingUpdateState? state, string message) => new() { HasPendingUpdate = true, IsSuccess = false, State = state, FailureMessage = message };
}

public sealed class UpdateService : ReactiveObject
{
    private const string ReleasesUrl = "https://api.github.com/repos/HouseAlwaysWin/GimmeCapture/releases";
    private readonly string _currentVersion;
    private readonly HttpClient _httpClient;
    private readonly IArtifactDownloader _artifactDownloader;
    private CancellationTokenSource? _downloadCancellation;
    private static readonly JsonSerializerOptions PendingStateSerializerOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private double _downloadProgress;
    public double DownloadProgress
    {
        get => _downloadProgress;
        private set => this.RaiseAndSetIfChanged(ref _downloadProgress, value);
    }

    private bool _isDownloading;
    public bool IsDownloading
    {
        get => _isDownloading;
        private set => this.RaiseAndSetIfChanged(ref _isDownloading, value);
    }

    public string? DownloadedZipPath { get; private set; }
    public string? DownloadedVersion { get; private set; }
    private ArtifactDownloadStage _downloadStage;
    public ArtifactDownloadStage DownloadStage
    {
        get => _downloadStage;
        private set => this.RaiseAndSetIfChanged(ref _downloadStage, value);
    }

    private string? _lastErrorMessage;
    public string? LastErrorMessage
    {
        get => _lastErrorMessage;
        private set => this.RaiseAndSetIfChanged(ref _lastErrorMessage, value);
    }

    public UpdateService(
        string currentVersion,
        HttpClient? httpClient = null,
        IArtifactDownloader? artifactDownloader = null)
    {
        _currentVersion = currentVersion.TrimStart('v');
        _httpClient = httpClient ?? SharedHttpClient.Instance;
        _artifactDownloader = artifactDownloader ?? new ArtifactDownloader(_httpClient);
        LoadCachedDownload();
    }

    // Restore a previously completed download (persisted to disk) so "Later" → install needs no re-download,
    // even after the app has been closed and reopened. Ignored if the file is gone or already superseded.
    private void LoadCachedDownload()
    {
        try
        {
            var state = ReadCachedDownloadState(GetCurrentAppDirectory());
            if (state != null
                && !string.IsNullOrEmpty(state.ZipPath)
                && File.Exists(state.ZipPath)
                && IsNewerVersion(state.TargetVersion, _currentVersion))
            {
                DownloadedZipPath = state.ZipPath;
                DownloadedVersion = state.TargetVersion;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("Update.LoadCachedDownload", ex);
        }
    }

    public async Task<ReleaseInfo?> CheckForUpdateAsync()
    {
        try
        {
            var releases = await GetAvailableReleasesAsync();
            return releases.FirstOrDefault(r => IsNewerVersion(r.NormalizedVersion, _currentVersion));
        }
        catch (Exception ex)
        {
            AppLog.Warning("Update.Check", ex);
            return null;
        }
    }

    public async Task<IReadOnlyList<ReleaseInfo>> GetAvailableReleasesAsync()
    {
        var releases = await _httpClient.GetFromJsonAsync<List<ReleaseInfo>>(ReleasesUrl) ?? new List<ReleaseInfo>();
        return FilterAndSortReleases(releases);
    }

    public bool IsUpdateDownloaded(ReleaseInfo release)
    {
        var targetVersion = release.NormalizedVersion;
        return DownloadedVersion == targetVersion
            && !string.IsNullOrEmpty(DownloadedZipPath)
            && File.Exists(DownloadedZipPath);
    }

    public async Task<string?> DownloadUpdateAsync(
        ReleaseInfo release,
        CancellationToken cancellationToken = default)
    {
        if (IsDownloading)
        {
            return null;
        }

        var targetVersion = release.NormalizedVersion;
        if (DownloadedVersion == targetVersion
            && !string.IsNullOrEmpty(DownloadedZipPath)
            && File.Exists(DownloadedZipPath))
        {
            return DownloadedZipPath;
        }

        string? downloadDirectory = null;
        bool downloadSucceeded = false;
        try
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _downloadCancellation = linkedCancellation;
            IsDownloading = true;
            DownloadProgress = 0;
            DownloadStage = ArtifactDownloadStage.Downloading;
            LastErrorMessage = null;

            var asset = release.GetPreferredZipAsset();
            if (asset == null)
            {
                throw new InvalidOperationException("No suitable zip asset found in release.");
            }

            var checksumAsset = release.GetChecksumAsset()
                ?? throw new InvalidOperationException("Release checksum manifest is missing.");
            var checksumManifest = await _httpClient
                .GetStringAsync(checksumAsset.DownloadUrl, linkedCancellation.Token)
                .ConfigureAwait(false);
            var expectedSha256 = ParseChecksum(checksumManifest, asset.Name)
                ?? throw new InvalidDataException($"Checksum for {asset.Name} was not found.");

            downloadDirectory = Path.Combine(Path.GetTempPath(), "GimmeCapture_Update_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(downloadDirectory);
            var descriptor = new ArtifactDescriptor(
                new Uri(asset.DownloadUrl),
                asset.Name,
                expectedSha256,
                asset.Size);
            var progress = new Progress<ArtifactDownloadProgress>(value =>
            {
                DownloadStage = value.Stage;
                DownloadProgress = value.Percentage;
            });
            var zipPath = await _artifactDownloader
                .DownloadAsync(descriptor, downloadDirectory, progress, linkedCancellation.Token)
                .ConfigureAwait(false);

            DownloadedZipPath = zipPath;
            DownloadedVersion = targetVersion;
            downloadSucceeded = true;
            PersistCachedDownload(targetVersion, zipPath);
            return zipPath;
        }
        catch (Exception ex)
        {
            AppLog.Warning("Update.Download", ex);
            LastErrorMessage = ex.Message;
            DownloadStage = ex is OperationCanceledException
                ? ArtifactDownloadStage.Cancelled
                : ArtifactDownloadStage.Failed;
            return null;
        }
        finally
        {
            _downloadCancellation = null;
            IsDownloading = false;
            if (!downloadSucceeded && downloadDirectory != null)
            {
                try
                {
                    Directory.Delete(downloadDirectory, recursive: true);
                }
                catch
                {
                }
            }
        }
    }

    public void CancelDownload()
    {
        try
        {
            _downloadCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void ApplyUpdate(string zipPath, string targetVersion)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                ApplyUpdateLinux(zipPath, targetVersion);
                return;
            }

            var currentExePath = RuntimePathProvider.GetExecutablePath();
            var appDir = Path.GetDirectoryName(currentExePath) ?? RuntimePathProvider.GetExecutableDirectory();
            var currentProcessId = Environment.ProcessId;
            var tempExtractDir = Path.Combine(Path.GetDirectoryName(zipPath)!, "extract-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempExtractDir);

            SafeZipExtractor.ExtractToDirectory(zipPath, tempExtractDir, overwriteFiles: true);

            var currentExe = currentExePath;
            var currentExeName = Path.GetFileName(currentExe);
            var extractSourceDir = ResolveExtractSourceDirectory(tempExtractDir, currentExeName);
            var expectedExePath = Path.Combine(appDir, currentExeName);
            WritePendingUpdateState(new PendingUpdateState
            {
                TargetVersion = targetVersion.TrimStart('v'),
                ExpectedExePath = expectedExePath,
                AppDirectory = appDir
            });

            var scriptPath = Path.Combine(Path.GetTempPath(), "GimmeCapture_Update.bat");
            var parentDir = Path.GetDirectoryName(zipPath)!;
            var currentVersionedConfigPath = AppStoragePaths.GetVersionedConfigPath(appDir, _currentVersion);
            var localConfigPath = Path.Combine(appDir, "config.json");
            var legacyConfigPath = AppStoragePaths.GetLegacyConfigPath();
            var sourceAppDataConfigPath = File.Exists(currentVersionedConfigPath)
                ? currentVersionedConfigPath
                : (File.Exists(localConfigPath) ? localConfigPath : legacyConfigPath);
            var targetAppDataConfigPath = AppStoragePaths.GetVersionedConfigPath(appDir, targetVersion);
            var appDataConfigBackupPath = Path.Combine(parentDir, "config.appdata.backup.json");
            var appDataConfigMarkerPath = Path.Combine(parentDir, "config.appdata.exists.marker");

            var script = BuildUpdateScript(
                extractSourceDir,
                appDir,
                parentDir,
                expectedExePath,
                currentProcessId,
                sourceAppDataConfigPath,
                targetAppDataConfigPath,
                appDataConfigBackupPath,
                appDataConfigMarkerPath);
            File.WriteAllText(scriptPath, script, System.Text.Encoding.Default);

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });

            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                PlatformErrorDialog.ShowError($"Update failed: {ex.Message}", "Update Error");
            });
        }
    }

    // Linux counterpart of the Windows apply flow. The release is a self-contained single-file ELF, so
    // "apply" = extract the tarball, wait for this process to exit, swap the one executable in place, and
    // relaunch it — via a detached /bin/sh script (there is no cmd.exe). A backup is kept and restored if
    // the swap fails. The pending-update state + startup verification are cross-platform and reused as-is.
    private void ApplyUpdateLinux(string tarGzPath, string targetVersion)
    {
        var currentExePath = RuntimePathProvider.GetExecutablePath();
        var appDir = Path.GetDirectoryName(currentExePath) ?? RuntimePathProvider.GetExecutableDirectory();
        var currentExeName = Path.GetFileName(currentExePath);
        var currentProcessId = Environment.ProcessId;
        var parentDir = Path.GetDirectoryName(tarGzPath)!;

        var tempExtractDir = Path.Combine(parentDir, "extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempExtractDir);
        ExtractTarGz(tarGzPath, tempExtractDir);

        var newExePath = ResolveLinuxNewExecutable(tempExtractDir, currentExeName)
            ?? throw new InvalidOperationException("Updated executable was not found in the downloaded archive.");

        WritePendingUpdateState(new PendingUpdateState
        {
            TargetVersion = targetVersion.TrimStart('v'),
            ExpectedExePath = currentExePath,
            AppDirectory = appDir
        });

        // Carry user settings into the new version's config directory (config is per-version), mirroring the
        // Windows script: prefer the current version's versioned config, then a local, then the legacy config.
        var currentVersionedConfig = AppStoragePaths.GetVersionedConfigPath(appDir, _currentVersion);
        var localConfig = Path.Combine(appDir, "config.json");
        var legacyConfig = AppStoragePaths.GetLegacyConfigPath();
        var sourceConfig = File.Exists(currentVersionedConfig)
            ? currentVersionedConfig
            : (File.Exists(localConfig) ? localConfig : legacyConfig);
        var targetConfig = AppStoragePaths.GetVersionedConfigPath(appDir, targetVersion.TrimStart('v'));
        var backupExe = Path.Combine(parentDir, currentExeName + ".backup");
        var pendingStatePath = AppStoragePaths.GetPendingUpdateStatePath(appDir);

        var scriptPath = Path.Combine(Path.GetTempPath(), "GimmeCapture_Update_" + Guid.NewGuid().ToString("N") + ".sh");
        var script = BuildLinuxUpdateScript(
            currentProcessId,
            newExePath,
            currentExePath,
            backupExe,
            parentDir,
            sourceConfig,
            targetConfig,
            pendingStatePath);
        File.WriteAllText(scriptPath, script);

        Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/sh",
            Arguments = $"\"{scriptPath}\"",
            UseShellExecute = false,
        });

        Environment.Exit(0);
    }

    private static void ExtractTarGz(string tarGzPath, string destDir)
    {
        using var fs = File.OpenRead(tarGzPath);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        System.Formats.Tar.TarFile.ExtractToDirectory(gz, destDir, overwriteFiles: true);
    }

    // The tarball holds the single self-contained executable. Prefer the same file name as the running
    // exe; otherwise the only extracted file; otherwise any file matching the name deeper in the tree.
    private static string? ResolveLinuxNewExecutable(string extractRoot, string expectedExeName)
    {
        var direct = Path.Combine(extractRoot, expectedExeName);
        if (File.Exists(direct))
        {
            return direct;
        }

        var files = Directory.GetFiles(extractRoot, "*", SearchOption.AllDirectories);
        var named = Array.Find(files, f => string.Equals(Path.GetFileName(f), expectedExeName, StringComparison.Ordinal));
        if (named != null)
        {
            return named;
        }

        return files.Length == 1 ? files[0] : null;
    }

    /// <summary>
    /// Builds the detached POSIX shell script that finishes the Linux update: wait for the old process to
    /// exit, back up and swap the executable, migrate config, relaunch, and clean up (restoring the backup
    /// on failure). Pure string builder so it is unit-testable off a Linux host.
    /// </summary>
    public static string BuildLinuxUpdateScript(
        int currentProcessId,
        string newExePath,
        string currentExePath,
        string backupExePath,
        string parentDir,
        string sourceConfigPath,
        string targetConfigPath,
        string pendingUpdateStatePath)
    {
        // Derive the parent dir with a forward-slash string op, not Path.GetDirectoryName — this builds a
        // Linux shell script, and on a Windows host (e.g. CI) Path.GetDirectoryName rewrites '/' to '\',
        // which would corrupt the mkdir/cp paths.
        int lastSlash = targetConfigPath.LastIndexOf('/');
        var targetConfigDir = lastSlash > 0 ? targetConfigPath[..lastSlash] : string.Empty;

        // Single-quote every path so spaces/metacharacters are inert; escape any embedded single quotes.
        static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";

        return $@"#!/bin/sh
trap '' HUP INT
# Wait (up to ~60s) for the old process to exit before swapping the busy executable.
i=0
while kill -0 {currentProcessId} 2>/dev/null; do
  i=$((i+1))
  if [ ""$i"" -ge 120 ]; then
    # Timed out: the old binary is still untouched, so relaunch it rather than leave the app closed.
    rm -f {Q(pendingUpdateStatePath)}
    setsid {Q(currentExePath)} >/dev/null 2>&1 &
    exit 1
  fi
  sleep 0.5
done
# Back up the current binary first; if the backup can't be written, do NOT risk an unrecoverable swap —
# relaunch the untouched old binary instead.
if ! cp -f {Q(currentExePath)} {Q(backupExePath)}; then
  rm -f {Q(pendingUpdateStatePath)}
  setsid {Q(currentExePath)} >/dev/null 2>&1 &
  exit 1
fi
if cp -f {Q(newExePath)} {Q(currentExePath)}; then
  chmod +x {Q(currentExePath)}
  # Migrate user config into the new version's dir, but only if its directory could be created.
  if [ -f {Q(sourceConfigPath)} ]; then
    if mkdir -p {Q(targetConfigDir)} 2>/dev/null; then
      cp -f {Q(sourceConfigPath)} {Q(targetConfigPath)} 2>/dev/null
    fi
  fi
  setsid {Q(currentExePath)} >/dev/null 2>&1 &
  rm -rf {Q(parentDir)}
  rm -f ""$0""
  exit 0
else
  # Swap failed: restore the backup and relaunch the old binary.
  cp -f {Q(backupExePath)} {Q(currentExePath)} 2>/dev/null
  chmod +x {Q(currentExePath)} 2>/dev/null
  rm -f {Q(pendingUpdateStatePath)}
  setsid {Q(currentExePath)} >/dev/null 2>&1 &
  exit 1
fi
";
    }

    public static IReadOnlyList<ReleaseInfo> FilterAndSortReleases(IEnumerable<ReleaseInfo> releases)
    {
        return releases
            .Where(r => !r.Draft && !r.Prerelease && r.GetPreferredZipAsset() != null)
            .OrderByDescending(r => ParseVersionOrNull(r.NormalizedVersion) ?? new Version(0, 0, 0))
            .ThenByDescending(r => r.TagName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static UpdateVerificationResult VerifyPendingUpdateOnStartup(string currentVersion, string currentExePath)
    {
        var currentAppDirectory = Path.GetDirectoryName(currentExePath) ?? RuntimePathProvider.GetExecutableDirectory();
        var state = ReadPendingUpdateState(currentAppDirectory);
        if (state == null)
        {
            return UpdateVerificationResult.None();
        }

        var normalizedCurrentVersion = currentVersion.TrimStart('v');
        var normalizedCurrentPath = Path.GetFullPath(currentExePath);
        var normalizedExpectedPath = Path.GetFullPath(state.ExpectedExePath);

        if (!string.Equals(normalizedCurrentPath, normalizedExpectedPath, StringComparison.OrdinalIgnoreCase))
        {
            var message = $"Expected executable path: {normalizedExpectedPath}\nActual executable path: {normalizedCurrentPath}\nExpected version: {state.TargetVersion}\nActual version: {normalizedCurrentVersion}";
            WriteFailedVerificationReport(state, message);
            return UpdateVerificationResult.Failure(state, message);
        }

        if (!string.Equals(normalizedCurrentVersion, state.TargetVersion, StringComparison.OrdinalIgnoreCase))
        {
            var message = $"Expected version: {state.TargetVersion}\nActual version: {normalizedCurrentVersion}\nExecutable path: {normalizedCurrentPath}";
            WriteFailedVerificationReport(state, message);
            return UpdateVerificationResult.Failure(state, message);
        }

        ClearPendingUpdateState(currentAppDirectory);
        ClearCachedDownloadState(currentAppDirectory);
        return UpdateVerificationResult.Success(state);
    }

    public static bool TryRedirectToPendingUpdatedInstance(string currentVersion, string currentExePath)
    {
        var currentAppDirectory = Path.GetDirectoryName(currentExePath) ?? RuntimePathProvider.GetExecutableDirectory();
        if (ReadPendingUpdateState(currentAppDirectory) != null)
        {
            return false;
        }

        var normalizedCurrentPath = Path.GetFullPath(currentExePath);
        var normalizedCurrentVersion = currentVersion.TrimStart('v');

        var redirectTarget = EnumeratePendingUpdateStates()
            .Where(state => !string.IsNullOrWhiteSpace(state.ExpectedExePath))
            .Select(state => new
            {
                State = state,
                NormalizedExpectedPath = Path.GetFullPath(state.ExpectedExePath)
            })
            .Where(x => !string.Equals(x.NormalizedExpectedPath, normalizedCurrentPath, StringComparison.OrdinalIgnoreCase))
            .Where(x => File.Exists(x.NormalizedExpectedPath))
            .Where(x => IsNewerVersion(x.State.TargetVersion, normalizedCurrentVersion))
            .OrderByDescending(x => ParseVersionOrNull(x.State.TargetVersion) ?? new Version(0, 0, 0))
            .ThenByDescending(x => x.State.CreatedAt)
            .FirstOrDefault();

        if (redirectTarget == null)
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = redirectTarget.NormalizedExpectedPath,
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warning("Update.PendingRedirect", ex);
            return false;
        }
    }

    public static string BuildUpdateScript(
        string extractSourceDir,
        string appDir,
        string parentDir,
        string expectedExePath,
        int currentProcessId,
        string sourceAppDataConfigPath,
        string targetAppDataConfigPath,
        string appDataConfigBackupPath,
        string appDataConfigMarkerPath)
    {
        var targetAppDataConfigDir = Path.GetDirectoryName(targetAppDataConfigPath) ?? string.Empty;
        var appBackupDir = Path.Combine(parentDir, "app-backup");
        var pendingUpdateStatePath = AppStoragePaths.GetPendingUpdateStatePath(appDir);

        return $@"
@echo off
setlocal EnableExtensions EnableDelayedExpansion
set ""WAIT_COUNT=0""
:wait_for_exit
tasklist /FI ""PID eq {currentProcessId}"" | find ""{currentProcessId}"" > nul
if not errorlevel 1 (
  if !WAIT_COUNT! GEQ 60 goto wait_failed
  set /a WAIT_COUNT+=1
  timeout /t 1 /nobreak > nul
  goto wait_for_exit
)
if not exist ""{extractSourceDir}"" goto update_failed
if exist ""{appBackupDir}"" rd /s /q ""{appBackupDir}""
mkdir ""{appBackupDir}""
robocopy ""{appDir.TrimEnd('\\')}"" ""{appBackupDir.TrimEnd('\\')}"" /E /R:2 /W:1 /NFL /NDL /NJH /NJS /NP > nul
if !ERRORLEVEL! GEQ 8 goto update_failed
if exist ""{sourceAppDataConfigPath}"" (
  copy /y ""{sourceAppDataConfigPath}"" ""{appDataConfigBackupPath}"" > nul
  type nul > ""{appDataConfigMarkerPath}""
)
set ""COPY_COUNT=0""
:copy_retry
robocopy ""{extractSourceDir.TrimEnd('\\')}"" ""{appDir.TrimEnd('\\')}"" /E /R:2 /W:1 /NFL /NDL /NJH /NJS /NP > nul
set ""ROBOCOPY_EXIT=!ERRORLEVEL!""
if !ROBOCOPY_EXIT! GEQ 8 (
  if !COPY_COUNT! GEQ 5 goto rollback
  set /a COPY_COUNT+=1
  timeout /t 1 /nobreak > nul
  goto copy_retry
)
if not exist ""{expectedExePath}"" goto rollback
if exist ""{appDataConfigMarkerPath}"" (
  if not exist ""{targetAppDataConfigDir}"" mkdir ""{targetAppDataConfigDir}""
  copy /y ""{appDataConfigBackupPath}"" ""{targetAppDataConfigPath}"" > nul
)
rd /s /q ""{parentDir.TrimEnd('\\')}"" 
start """" ""{expectedExePath}""
del ""%~f0""
exit /b 0

:rollback
robocopy ""{appBackupDir.TrimEnd('\\')}"" ""{appDir.TrimEnd('\\')}"" /MIR /R:2 /W:1 /NFL /NDL /NJH /NJS /NP > nul

:update_failed
if exist ""{pendingUpdateStatePath}"" del /f /q ""{pendingUpdateStatePath}""
if exist ""{expectedExePath}"" start """" ""{expectedExePath}""
exit /b 1

:wait_failed
if exist ""{pendingUpdateStatePath}"" del /f /q ""{pendingUpdateStatePath}""
exit /b 1
";
    }

    internal static string? ParseChecksum(string manifest, string fileName)
    {
        foreach (var line in manifest.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 &&
                parts[0].Length == 64 &&
                string.Equals(parts[1].TrimStart('*'), fileName, StringComparison.OrdinalIgnoreCase))
            {
                return parts[0].ToLowerInvariant();
            }
        }

        return null;
    }

    private static bool IsNewerVersion(string newVer, string currentVer)
    {
        var newVersion = ParseVersionOrNull(newVer);
        var currentVersion = ParseVersionOrNull(currentVer);
        if (newVersion != null && currentVersion != null)
        {
            return newVersion > currentVersion;
        }

        return string.Compare(newVer, currentVer, StringComparison.OrdinalIgnoreCase) > 0;
    }

    private static Version? ParseVersionOrNull(string version)
    {
        return Version.TryParse(version, out var parsed) ? parsed : null;
    }

    private static string ResolveExtractSourceDirectory(string extractRoot, string expectedExeName)
    {
        if (File.Exists(Path.Combine(extractRoot, expectedExeName)))
        {
            return extractRoot;
        }

        var nestedDirectories = Directory.GetDirectories(extractRoot);
        foreach (var nestedDirectory in nestedDirectories)
        {
            if (File.Exists(Path.Combine(nestedDirectory, expectedExeName)))
            {
                return nestedDirectory;
            }
        }

        return extractRoot;
    }

    private static string GetPendingUpdateStatePath(string appDirectory)
    {
        var path = AppStoragePaths.GetPendingUpdateStatePath(appDirectory);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        return path;
    }

    private static string GetFailedVerificationReportPath(string appDirectory)
    {
        var path = AppStoragePaths.GetFailedVerificationReportPath(appDirectory);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        return path;
    }

    private static string GetCurrentAppDirectory()
    {
        var exe = RuntimePathProvider.GetExecutablePath();
        return Path.GetDirectoryName(exe) ?? RuntimePathProvider.GetExecutableDirectory();
    }

    // Stored next to the pending-update state so it shares the per-install storage location.
    private static string GetCachedDownloadStatePath(string appDirectory)
    {
        var directory = Path.GetDirectoryName(GetPendingUpdateStatePath(appDirectory))!;
        return Path.Combine(directory, "cached-download.json");
    }

    private void PersistCachedDownload(string targetVersion, string zipPath)
    {
        try
        {
            WriteCachedDownloadState(GetCurrentAppDirectory(), new CachedDownloadState
            {
                TargetVersion = targetVersion,
                ZipPath = zipPath
            });
        }
        catch (Exception ex)
        {
            AppLog.Warning("Update.PersistCachedDownload", ex);
        }
    }

    private static void WriteCachedDownloadState(string appDirectory, CachedDownloadState state)
    {
        File.WriteAllText(GetCachedDownloadStatePath(appDirectory), JsonSerializer.Serialize(state, PendingStateSerializerOptions));
    }

    private static CachedDownloadState? ReadCachedDownloadState(string appDirectory)
    {
        try
        {
            var path = GetCachedDownloadStatePath(appDirectory);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<CachedDownloadState>(File.ReadAllText(path), PendingStateSerializerOptions)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void ClearCachedDownloadState(string appDirectory)
    {
        try
        {
            var path = GetCachedDownloadStatePath(appDirectory);
            if (!File.Exists(path))
            {
                return;
            }

            // Best-effort: also remove the cached installer's temp directory.
            try
            {
                var state = JsonSerializer.Deserialize<CachedDownloadState>(File.ReadAllText(path), PendingStateSerializerOptions);
                var zipDir = string.IsNullOrEmpty(state?.ZipPath) ? null : Path.GetDirectoryName(state!.ZipPath);
                if (!string.IsNullOrEmpty(zipDir) && Directory.Exists(zipDir))
                {
                    Directory.Delete(zipDir, recursive: true);
                }
            }
            catch
            {
            }

            File.Delete(path);
        }
        catch
        {
        }
    }

    private static void WritePendingUpdateState(PendingUpdateState state)
    {
        File.WriteAllText(GetPendingUpdateStatePath(state.AppDirectory), JsonSerializer.Serialize(state, PendingStateSerializerOptions));
    }

    private static PendingUpdateState? ReadPendingUpdateState(string appDirectory)
    {
        var primaryPath = GetPendingUpdateStatePath(appDirectory);
        var legacyPath = AppStoragePaths.GetLegacyPendingUpdateStatePath();
        string? pathToRead = File.Exists(primaryPath)
            ? primaryPath
            : (File.Exists(legacyPath) ? legacyPath : null);

        if (pathToRead == null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PendingUpdateState>(File.ReadAllText(pathToRead), PendingStateSerializerOptions);
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<PendingUpdateState> EnumeratePendingUpdateStates()
    {
        var rootDirectory = AppStoragePaths.GetRootDirectory();
        if (!Directory.Exists(rootDirectory))
        {
            yield break;
        }

        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(rootDirectory, "pending-update.json", SearchOption.AllDirectories))
        {
            var normalizedPath = Path.GetFullPath(path);
            if (!seenPaths.Add(normalizedPath))
            {
                continue;
            }

            PendingUpdateState? state;
            try
            {
                state = JsonSerializer.Deserialize<PendingUpdateState>(File.ReadAllText(path), PendingStateSerializerOptions);
            }
            catch
            {
                continue;
            }

            if (state != null)
            {
                yield return state;
            }
        }
    }

    private static void ClearPendingUpdateState(string appDirectory)
    {
        var paths = new[]
        {
            GetPendingUpdateStatePath(appDirectory),
            AppStoragePaths.GetLegacyPendingUpdateStatePath()
        };

        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static void WriteFailedVerificationReport(PendingUpdateState state, string failureMessage)
    {
        var report = $"Update verification failed at {DateTimeOffset.Now:O}{Environment.NewLine}"
                   + $"TargetVersion: {state.TargetVersion}{Environment.NewLine}"
                   + $"ExpectedExePath: {state.ExpectedExePath}{Environment.NewLine}"
                   + $"AppDirectory: {state.AppDirectory}{Environment.NewLine}"
                   + failureMessage;
        File.WriteAllText(GetFailedVerificationReportPath(state.AppDirectory), report);
    }
}
