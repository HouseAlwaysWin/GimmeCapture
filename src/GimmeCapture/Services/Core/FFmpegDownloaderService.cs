using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ReactiveUI;

namespace GimmeCapture.Services.Core;

public class FFmpegDownloaderService : ReactiveObject
{
    private const string FfmpegUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";
    private readonly AppSettingsService? _settingsService;
    private string BinFolder => Path.Combine(_settingsService?.BaseDataDirectory ?? AppDomain.CurrentDomain.BaseDirectory, "bin");
    private string LocalFfmpegPath => Path.Combine(BinFolder, "ffmpeg.exe");
    private string LocalFfplayPath => Path.Combine(BinFolder, "ffplay.exe");

    private double _downloadProgress;
    public double DownloadProgress
    {
        get => _downloadProgress;
        set => this.RaiseAndSetIfChanged(ref _downloadProgress, value);
    }

    private bool _isDownloading;
    public bool IsDownloading
    {
        get => _isDownloading;
        set => this.RaiseAndSetIfChanged(ref _isDownloading, value);
    }

    public string FfmpegExecutablePath => GetFFmpegPath();

    public FFmpegDownloaderService(AppSettingsService? settingsService = null)
    {
        _settingsService = settingsService;
    }

    public bool IsFFmpegAvailable()
    {
        return !string.IsNullOrEmpty(GetFFmpegPath());
    }
    
    public bool IsFFplayAvailable()
    {
        return !string.IsNullOrEmpty(GetFFplayPath());
    }

    public string GetFFmpegPath()
    {
        // 1. Check system PATH (User requested priority)
        var systemPath = GetFullPath("ffmpeg.exe");
        if (!string.IsNullOrEmpty(systemPath)) return systemPath;
        // 2. Check local bin folder
        if (File.Exists(LocalFfmpegPath)) return LocalFfmpegPath;
        return string.Empty;
    }

    public string GetFFplayPath()
    {
        // 1. Check system PATH (User requested priority)
        var systemPath = GetFullPath("ffplay.exe");
        if (!string.IsNullOrEmpty(systemPath)) return systemPath;
        // 2. Check local bin folder
        if (File.Exists(LocalFfplayPath)) return LocalFfplayPath;
        return string.Empty;
    }

    private string? GetFullPath(string fileName)
    {
        if (File.Exists(fileName))
            return Path.GetFullPath(fileName);

        var values = Environment.GetEnvironmentVariable("PATH");
        if (values == null) return null;
        
        foreach (var path in values.Split(Path.PathSeparator))
        {
            var fullPath = Path.Combine(path, fileName);
            if (File.Exists(fullPath))
                return fullPath;
        }
        return null;
    }

    public void RemoveFFmpeg()
    {
        try
        {
            if (File.Exists(LocalFfmpegPath)) File.Delete(LocalFfmpegPath);
            var ffprobePath = Path.Combine(BinFolder, "ffprobe.exe");
            if (File.Exists(ffprobePath)) File.Delete(ffprobePath);
            var ffplayPath = Path.Combine(BinFolder, "ffplay.exe");
            if (File.Exists(ffplayPath)) File.Delete(ffplayPath);
            
            this.RaisePropertyChanged(nameof(IsFFmpegAvailable));
            this.RaisePropertyChanged(nameof(IsFFplayAvailable));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"FFmpeg Removal Failed: {ex.Message}");
        }
    }

    public async Task<bool> EnsureFFmpegAsync(CancellationToken ct = default)
    {
        if (IsFFmpegAvailable() && IsFFplayAvailable()) return true;

        if (IsDownloading) return true;

        try
        {
            IsDownloading = true;
            DownloadProgress = 0;

            if (!Directory.Exists(BinFolder))
            {
                Directory.CreateDirectory(BinFolder);
            }

            string extractPath = Path.Combine(BinFolder, "temp_ffmpeg");
            string zipPath = Path.Combine(BinFolder, "ffmpeg.zip");

            // Check if we already have extracted files that satisfy our MISSING needs
            bool skipDownload = false;
            
            bool missingFFmpeg = !IsFFmpegAvailable();
            bool missingFFplay = !IsFFplayAvailable();
            
            if (Directory.Exists(extractPath))
            {
                 bool ffmpegInTemp = Directory.GetFiles(extractPath, "ffmpeg.exe", SearchOption.AllDirectories).Length > 0;
                 bool ffplayInTemp = Directory.GetFiles(extractPath, "ffplay.exe", SearchOption.AllDirectories).Length > 0;
                 
                 bool canSatisfyFFmpeg = !missingFFmpeg || ffmpegInTemp;
                 bool canSatisfyFFplay = !missingFFplay || ffplayInTemp;

                 if (canSatisfyFFmpeg && canSatisfyFFplay)
                 {
                     skipDownload = true;
                 }
            }

            if (!skipDownload)
            {
                using (var client = new HttpClient())
                {
                    // GitHub requires User-Agent
                    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
                    // Increase timeout for large files
                    client.Timeout = TimeSpan.FromMinutes(10);
                    
                    using (var response = await client.GetAsync(FfmpegUrl, HttpCompletionOption.ResponseHeadersRead, ct))
                    {
                        response.EnsureSuccessStatusCode();

                        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                        using (var contentStream = await response.Content.ReadAsStreamAsync(ct))
                        using (var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                        {
                            var buffer = new byte[8192];
                            long totalRead = 0;
                            int read;

                            while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                            {
                                await fileStream.WriteAsync(buffer, 0, read, ct);
                                totalRead += read;

                                if (totalBytes != -1)
                                {
                                    DownloadProgress = (double)totalRead / totalBytes * 100;
                                }
                                
                                if (ct.IsCancellationRequested)
                                {
                                     System.Diagnostics.Debug.WriteLine("[FFmpegDownloader] Loop detected cancellation!");
                                }
                            }
                        }
                    }
                }

                // Extract
                await Task.Run(() =>
                {
                    if (ct.IsCancellationRequested) return;
                    if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);
                    ZipFile.ExtractToDirectory(zipPath, extractPath);
                    File.Delete(zipPath);
                }, ct);
            }

            // Move files
            await Task.Run(() =>
            {
                // Find ffmpeg.exe in the extracted folder (it's usually in a subfolder like ffmpeg-xxx-essentials_build/bin/ffmpeg.exe)
                var files = Directory.GetFiles(extractPath, "ffmpeg.exe", SearchOption.AllDirectories);
                if (files.Length > 0)
                {
                    // Safe delete and move ffmpeg.exe
                    try 
                    {
                        if (File.Exists(LocalFfmpegPath)) File.Delete(LocalFfmpegPath);
                        File.Move(files[0], LocalFfmpegPath);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        // If file exists, we can assume it's usable (locked by us or system), so we skip overwriting it
                        if (!File.Exists(LocalFfmpegPath))
                        {
                            throw new IOException($"檔案被佔用或無權限，且無法建立新檔案 ({LocalFfmpegPath})。請關閉佔用程式。", ex);
                        }
                        System.Diagnostics.Debug.WriteLine($"Skipped overwriting locked ffmpeg.exe: {ex.Message}");
                    }
                    
                    // Also try to grab ffprobe if available
                    var ffprobeFiles = Directory.GetFiles(extractPath, "ffprobe.exe", SearchOption.AllDirectories);
                    if (ffprobeFiles.Length > 0)
                    {
                        string ffprobePath = Path.Combine(BinFolder, "ffprobe.exe");
                        try 
                        {
                            if (File.Exists(ffprobePath)) File.Delete(ffprobePath);
                            File.Move(ffprobeFiles[0], ffprobePath);
                        }
                        catch { /* Ignore non-critical errors */ }
                    }

                    // Also try to grab ffplay if available (for Pinning)
                    var ffplayFiles = Directory.GetFiles(extractPath, "ffplay.exe", SearchOption.AllDirectories);
                    if (ffplayFiles.Length > 0)
                    {
                        string ffplayPath = Path.Combine(BinFolder, "ffplay.exe");
                        try
                        {
                            if (File.Exists(ffplayPath)) File.Delete(ffplayPath);
                            File.Move(ffplayFiles[0], ffplayPath);
                        }
                        catch(Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                        {
                             // Similarly check if it exists
                             if (!File.Exists(ffplayPath))
                             {
                                 throw new IOException($"檔案被佔用或無權限，且無法建立新檔案 ({ffplayPath})。請關閉所有 ffplay 視窗後重試。", ex);
                             }
                             System.Diagnostics.Debug.WriteLine($"Skipped overwriting locked ffplay.exe: {ex.Message}");
                        }
                    }
                    
                    // Cleanup only if successful
                    try { Directory.Delete(extractPath, true); } catch {}
                }
                else
                {
                    throw new FileNotFoundException("ffmpeg.exe not found in extracted files");
                }
            });

            return IsFFmpegAvailable();
        }
        catch (OperationCanceledException)
        {
             System.Diagnostics.Debug.WriteLine($"[FFmpegDownloader] Operation Canceled.");
             return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"FFmpeg Download/Install Failed: {ex.Message}");
            DownloadProgress = 0; // Reset progress on failure
            return false;
        }
        finally
        {
            IsDownloading = false;
        }
    }
}
