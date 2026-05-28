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

    public ReleaseAsset? GetPreferredZipAsset()
    {
        return Assets.Find(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                                && a.Name.Contains("win", StringComparison.OrdinalIgnoreCase))
               ?? Assets.Find(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class ReleaseAsset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("browser_download_url")]
    public string DownloadUrl { get; set; } = string.Empty;
}

public sealed class PendingUpdateState
{
    public string TargetVersion { get; set; } = string.Empty;
    public string ExpectedExePath { get; set; } = string.Empty;
    public string AppDirectory { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
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

    public UpdateService(string currentVersion)
    {
        _currentVersion = currentVersion.TrimStart('v');
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
            Debug.WriteLine($"Update check failed: {ex.Message}");
            return null;
        }
    }

    public async Task<IReadOnlyList<ReleaseInfo>> GetAvailableReleasesAsync()
    {
        using var client = CreateClient();
        var releases = await client.GetFromJsonAsync<List<ReleaseInfo>>(ReleasesUrl) ?? new List<ReleaseInfo>();
        return FilterAndSortReleases(releases);
    }

    public bool IsUpdateDownloaded(ReleaseInfo release)
    {
        var targetVersion = release.NormalizedVersion;
        return DownloadedVersion == targetVersion
            && !string.IsNullOrEmpty(DownloadedZipPath)
            && File.Exists(DownloadedZipPath);
    }

    public async Task<string?> DownloadUpdateAsync(ReleaseInfo release)
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

        try
        {
            IsDownloading = true;
            DownloadProgress = 0;

            var asset = release.GetPreferredZipAsset();
            if (asset == null)
            {
                throw new InvalidOperationException("No suitable zip asset found in release.");
            }

            var tempPath = Path.Combine(Path.GetTempPath(), "GimmeCapture_Update_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempPath);
            var zipPath = Path.Combine(tempPath, asset.Name);

            using var client = CreateClient();
            using var response = await client.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            await using var contentStream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            var buffer = new byte[8192];
            long totalRead = 0;
            int read;

            while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, read);
                totalRead += read;

                if (totalBytes > 0)
                {
                    DownloadProgress = (double)totalRead / totalBytes * 100;
                }
            }

            DownloadedZipPath = zipPath;
            DownloadedVersion = targetVersion;
            return zipPath;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Download failed: {ex.Message}");
            return null;
        }
        finally
        {
            IsDownloading = false;
        }
    }

    public void ApplyUpdate(string zipPath, string targetVersion)
    {
        try
        {
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            var tempExtractDir = Path.Combine(Path.GetDirectoryName(zipPath)!, "extract");
            Directory.CreateDirectory(tempExtractDir);

            ZipFile.ExtractToDirectory(zipPath, tempExtractDir, overwriteFiles: true);

            var currentExe = Process.GetCurrentProcess().MainModule?.FileName ?? "GimmeCapture.exe";
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
            var localConfigPath = Path.Combine(appDir, "config.json");
            var appDataConfigPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GimmeCapture",
                "config.json");
            var localConfigBackupPath = Path.Combine(parentDir, "config.local.backup.json");
            var appDataConfigBackupPath = Path.Combine(parentDir, "config.appdata.backup.json");
            var localConfigMarkerPath = Path.Combine(parentDir, "config.local.exists.marker");
            var appDataConfigMarkerPath = Path.Combine(parentDir, "config.appdata.exists.marker");

            var script = BuildUpdateScript(
                extractSourceDir,
                appDir,
                parentDir,
                expectedExePath,
                localConfigPath,
                appDataConfigPath,
                localConfigBackupPath,
                appDataConfigBackupPath,
                localConfigMarkerPath,
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
                System.Windows.Forms.MessageBox.Show($"Update failed: {ex.Message}", "Update Error");
            });
        }
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
        var state = ReadPendingUpdateState();
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

        ClearPendingUpdateState();
        return UpdateVerificationResult.Success(state);
    }

    public static string BuildUpdateScript(
        string extractSourceDir,
        string appDir,
        string parentDir,
        string expectedExePath,
        string localConfigPath,
        string appDataConfigPath,
        string localConfigBackupPath,
        string appDataConfigBackupPath,
        string localConfigMarkerPath,
        string appDataConfigMarkerPath)
    {
        var appDataConfigDir = Path.GetDirectoryName(appDataConfigPath) ?? string.Empty;

        return $@"
@echo off
timeout /t 2 /nobreak > nul
if not exist ""{extractSourceDir}"" exit /b 1
if exist ""{localConfigPath}"" (
  copy /y ""{localConfigPath}"" ""{localConfigBackupPath}"" > nul
  type nul > ""{localConfigMarkerPath}""
)
if exist ""{appDataConfigPath}"" (
  copy /y ""{appDataConfigPath}"" ""{appDataConfigBackupPath}"" > nul
  type nul > ""{appDataConfigMarkerPath}""
)
xcopy /s /y /i ""{extractSourceDir.TrimEnd('\\')}"" ""{appDir.TrimEnd('\\')}""
if exist ""{localConfigMarkerPath}"" (
  copy /y ""{localConfigBackupPath}"" ""{localConfigPath}"" > nul
) else if exist ""{appDataConfigMarkerPath}"" (
  if exist ""{localConfigPath}"" del /f /q ""{localConfigPath}""
)
if exist ""{appDataConfigMarkerPath}"" (
  if not exist ""{appDataConfigDir}"" mkdir ""{appDataConfigDir}""
  copy /y ""{appDataConfigBackupPath}"" ""{appDataConfigPath}"" > nul
)
if not exist ""{expectedExePath}"" exit /b 1
rd /s /q ""{parentDir.TrimEnd('\\')}""
start """" ""{expectedExePath}""
del ""%~f0""
";
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Add("User-Agent", "GimmeCapture-Updater");
        return client;
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

    private static string GetPendingUpdateStatePath()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GimmeCapture");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "pending-update.json");
    }

    private static string GetFailedVerificationReportPath()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GimmeCapture");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "pending-update.failed.txt");
    }

    private static void WritePendingUpdateState(PendingUpdateState state)
    {
        File.WriteAllText(GetPendingUpdateStatePath(), JsonSerializer.Serialize(state, PendingStateSerializerOptions));
    }

    private static PendingUpdateState? ReadPendingUpdateState()
    {
        var path = GetPendingUpdateStatePath();
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PendingUpdateState>(File.ReadAllText(path), PendingStateSerializerOptions);
        }
        catch
        {
            return null;
        }
    }

    private static void ClearPendingUpdateState()
    {
        var path = GetPendingUpdateStatePath();
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void WriteFailedVerificationReport(PendingUpdateState state, string failureMessage)
    {
        var report = $"Update verification failed at {DateTimeOffset.Now:O}{Environment.NewLine}"
                   + $"TargetVersion: {state.TargetVersion}{Environment.NewLine}"
                   + $"ExpectedExePath: {state.ExpectedExePath}{Environment.NewLine}"
                   + $"AppDirectory: {state.AppDirectory}{Environment.NewLine}"
                   + failureMessage;
        File.WriteAllText(GetFailedVerificationReportPath(), report);
    }
}
