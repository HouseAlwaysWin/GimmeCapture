using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Avalonia.Media;
using ReactiveUI;

namespace GimmeCapture.Services;

public class ReleaseInfo
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = string.Empty;

    [JsonPropertyName("assets")]
    public List<ReleaseAsset> Assets { get; set; } = new();
}

public class ReleaseAsset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("browser_download_url")]
    public string DownloadUrl { get; set; } = string.Empty;
}

public class UpdateService : ReactiveObject
{
    private const string RepoUrl = "https://api.github.com/repos/HouseAlwaysWin/GimmeCapture/releases/latest";
    private readonly string _currentVersion;
    
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

    public UpdateService(string currentVersion)
    {
        _currentVersion = currentVersion.TrimStart('v');
    }

    public async Task<ReleaseInfo?> CheckForUpdateAsync()
    {
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "GimmeCapture-Updater");
            
            var release = await client.GetFromJsonAsync<ReleaseInfo>(RepoUrl);
            if (release != null)
            {
                var newVersion = release.TagName.TrimStart('v');
                if (IsNewerVersion(newVersion, _currentVersion))
                {
                    return release;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Update check failed: {ex.Message}");
        }
        return null;
    }

    private bool IsNewerVersion(string newVer, string currentVer)
    {
        try
        {
            return Version.Parse(newVer) > Version.Parse(currentVer);
        }
        catch
        {
            return string.Compare(newVer, currentVer, StringComparison.OrdinalIgnoreCase) > 0;
        }
    }

    public async Task<string?> DownloadUpdateAsync(ReleaseInfo release)
    {
        if (IsDownloading) return null;

        try
        {
            IsDownloading = true;
            DownloadProgress = 0;

            // Find the zip asset for Windows
            var asset = release.Assets.Find(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && a.Name.Contains("win", StringComparison.OrdinalIgnoreCase)) 
                        ?? release.Assets.Find(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

            if (asset == null) throw new Exception("No suitable zip asset found in release.");

            var tempPath = Path.Combine(Path.GetTempPath(), "GimmeCapture_Update_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempPath);
            var zipPath = Path.Combine(tempPath, asset.Name);

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "GimmeCapture-Updater");
            
            using var response = await client.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            using var contentStream = await response.Content.ReadAsStreamAsync();
            using var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            var buffer = new byte[8192];
            long totalRead = 0;
            int read;

            while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, read);
                totalRead += read;

                if (totalBytes != -1)
                {
                    DownloadProgress = (double)totalRead / totalBytes * 100;
                }
            }
            
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

    public void ApplyUpdate(string zipPath)
    {
        try
        {
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            var tempExtractDir = Path.Combine(Path.GetDirectoryName(zipPath)!, "extract");
            Directory.CreateDirectory(tempExtractDir);
            
            ZipFile.ExtractToDirectory(zipPath, tempExtractDir);

            // Create update script
            var scriptPath = Path.Combine(Path.GetTempPath(), "GimmeCapture_Update.bat");
            var currentExe = Process.GetCurrentProcess().MainModule?.FileName ?? "GimmeCapture.exe";
            var currentExeName = Path.GetFileName(currentExe);

            var script = $@"
@echo off
timeout /t 2 /nobreak > nul
xcopy /s /y ""{tempExtractDir}\*"" ""{appDir}""
rd /s /q ""{tempExtractDir}""
del ""{zipPath}""
start """" ""{Path.Combine(appDir, currentExeName)}""
del ""%~f0""
";
            File.WriteAllText(scriptPath, script, System.Text.Encoding.Default);

            Process.Start(new ProcessStartInfo
            {
                FileName = scriptPath,
                UseShellExecute = true,
                CreateNoWindow = true
            });

            Environment.Exit(0);
        }
        catch (Exception ex)
        {
             Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                 System.Windows.Forms.MessageBox.Show($"無法啟動更新程序: {ex.Message}", "更新錯誤");
            });
        }
    }
}
