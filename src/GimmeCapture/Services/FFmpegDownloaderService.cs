using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading.Tasks;
using ReactiveUI;

namespace GimmeCapture.Services;

public class FFmpegDownloaderService : ReactiveObject
{
    private const string FfmpegUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";
    private readonly string _binFolder;
    private readonly string _ffmpegPath;

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

    public string FfmpegExecutablePath => _ffmpegPath;

    public FFmpegDownloaderService()
    {
        _binFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bin");
        _ffmpegPath = Path.Combine(_binFolder, "ffmpeg.exe");
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
        if (File.Exists(_ffmpegPath)) return _ffmpegPath;
        return string.Empty;
    }

    public string GetFFplayPath()
    {
        // 1. Check system PATH (User requested priority)
        var systemPath = GetFullPath("ffplay.exe");
        if (!string.IsNullOrEmpty(systemPath)) return systemPath;
        // 2. Check local bin folder
        var localPath = Path.Combine(_binFolder, "ffplay.exe");
        if (File.Exists(localPath)) return localPath;
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

    public async Task<bool> EnsureFFmpegAsync()
    {
        if (IsFFmpegAvailable() && IsFFplayAvailable()) return true;

        if (IsDownloading) return false;

        try
        {
            IsDownloading = true;
            DownloadProgress = 0;

            if (!Directory.Exists(_binFolder))
            {
                Directory.CreateDirectory(_binFolder);
            }

            string extractPath = Path.Combine(_binFolder, "temp_ffmpeg");
            string zipPath = Path.Combine(_binFolder, "ffmpeg.zip");

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
                    
                    using (var response = await client.GetAsync(FfmpegUrl, HttpCompletionOption.ResponseHeadersRead))
                    {
                        response.EnsureSuccessStatusCode();

                        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                        using (var contentStream = await response.Content.ReadAsStreamAsync())
                        using (var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                        {
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
                        }
                    }
                }

                // Extract
                await Task.Run(() =>
                {
                    if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);
                    ZipFile.ExtractToDirectory(zipPath, extractPath);
                    File.Delete(zipPath);
                });
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
                        if (File.Exists(_ffmpegPath)) File.Delete(_ffmpegPath);
                        File.Move(files[0], _ffmpegPath);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        // If file exists, we can assume it's usable (locked by us or system), so we skip overwriting it
                        if (!File.Exists(_ffmpegPath))
                        {
                            throw new IOException($"檔案被佔用或無權限，且無法建立新檔案 ({_ffmpegPath})。請關閉佔用程式。", ex);
                        }
                        System.Diagnostics.Debug.WriteLine($"Skipped overwriting locked ffmpeg.exe: {ex.Message}");
                    }
                    
                    // Also try to grab ffprobe if available
                    var ffprobeFiles = Directory.GetFiles(extractPath, "ffprobe.exe", SearchOption.AllDirectories);
                    if (ffprobeFiles.Length > 0)
                    {
                        string ffprobePath = Path.Combine(_binFolder, "ffprobe.exe");
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
                        string ffplayPath = Path.Combine(_binFolder, "ffplay.exe");
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
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"FFmpeg Download/Install Failed: {ex.Message}");
            DownloadProgress = 0; // Reset progress on failure
            // Show error to user
            Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                 System.Windows.Forms.MessageBox.Show($"下載或安裝失敗: {ex.Message}", "錯誤");
            });
            return false;
        }
        finally
        {
            IsDownloading = false;
        }
    }
}
