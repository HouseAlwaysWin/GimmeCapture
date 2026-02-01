using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading.Tasks;
using ReactiveUI;

namespace GimmeCapture.Services;

public class FFmpegDownloaderService : ReactiveObject
{
    private const string FfmpegUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
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
        return File.Exists(_ffmpegPath);
    }

    public async Task<bool> EnsureFFmpegAsync()
    {
        if (IsFFmpegAvailable()) return true;

        if (IsDownloading) return false;

        try
        {
            IsDownloading = true;
            DownloadProgress = 0;

            if (!Directory.Exists(_binFolder))
            {
                Directory.CreateDirectory(_binFolder);
            }

            string zipPath = Path.Combine(_binFolder, "ffmpeg.zip");

            using (var client = new HttpClient())
            {
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
                string extractPath = Path.Combine(_binFolder, "temp_ffmpeg");
                if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);
                
                ZipFile.ExtractToDirectory(zipPath, extractPath);

                // Find ffmpeg.exe in the extracted folder (it's usually in a subfolder like ffmpeg-xxx-essentials_build/bin/ffmpeg.exe)
                var files = Directory.GetFiles(extractPath, "ffmpeg.exe", SearchOption.AllDirectories);
                if (files.Length > 0)
                {
                    if (File.Exists(_ffmpegPath)) File.Delete(_ffmpegPath);
                    File.Move(files[0], _ffmpegPath);
                    
                    // Also try to grab ffprobe if available
                    var ffprobeFiles = Directory.GetFiles(extractPath, "ffprobe.exe", SearchOption.AllDirectories);
                    if (ffprobeFiles.Length > 0)
                    {
                        string ffprobePath = Path.Combine(_binFolder, "ffprobe.exe");
                        if (File.Exists(ffprobePath)) File.Delete(ffprobePath);
                        File.Move(ffprobeFiles[0], ffprobePath);
                    }
                }

                // Cleanup
                Directory.Delete(extractPath, true);
                File.Delete(zipPath);
            });

            return IsFFmpegAvailable();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"FFmpeg Download Failed: {ex.Message}");
            return false;
        }
        finally
        {
            IsDownloading = false;
        }
    }
}
