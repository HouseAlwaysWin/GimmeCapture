using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ReactiveUI;

namespace GimmeCapture.Services.Core;

public class AIModelDownloader : ReactiveObject
{
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

    private string _currentDownloadName = "AI Component";
    public string CurrentDownloadName
    {
        get => _currentDownloadName;
        set => this.RaiseAndSetIfChanged(ref _currentDownloadName, value);
    }

    public virtual async Task DownloadFileAsync(string url, string destination, double progressOffset, double progressWeight, CancellationToken ct)
    {
        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromMinutes(60);
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
        
        System.Diagnostics.Debug.WriteLine($"[AI] Downloading {url} to {destination}");
        
        string tempPath = destination + ".tmp";
        
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            using var contentStream = await response.Content.ReadAsStreamAsync(ct);
            using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            var buffer = new byte[8192];
            long totalRead = 0;
            int read;

            while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, read, ct);
                totalRead += read;

                if (totalBytes != -1)
                {
                    DownloadProgress = progressOffset + ((double)totalRead / totalBytes * progressWeight);
                }
            }
            
            await fileStream.FlushAsync(ct);
            fileStream.Close();

            if (totalBytes != -1 && totalRead < totalBytes)
            {
                throw new IOException($"Download truncated: Expected {totalBytes} bytes but only received {totalRead} bytes.");
            }

            if (File.Exists(destination)) File.Delete(destination);
            File.Move(tempPath, destination);
        }
        catch (Exception)
        {
            if (File.Exists(tempPath)) try { File.Delete(tempPath); } catch { }
            throw;
        }
    }

    public virtual async Task DownloadAndExtractZipAsync(string url, string outputDir, double progressOffset, double progressWeight, CancellationToken ct)
    {
        string zipPath = Path.Combine(outputDir, "temp_ai.zip");
        await DownloadFileAsync(url, zipPath, progressOffset, progressWeight, ct);

        await Task.Run(() =>
        {
            if (ct.IsCancellationRequested) return;

            using (ZipArchive archive = ZipFile.OpenRead(zipPath))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    {
                        string fileName = Path.GetFileName(entry.FullName);
                        string destinationPath = Path.Combine(outputDir, fileName);
                        entry.ExtractToFile(destinationPath, true);
                    }
                }
            }
            File.Delete(zipPath);
        }, ct);
    }
}
