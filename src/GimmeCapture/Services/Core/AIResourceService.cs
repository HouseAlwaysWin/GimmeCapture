using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading.Tasks;
using ReactiveUI;
using GimmeCapture.Models;

namespace GimmeCapture.Services.Core;

public class AIResourceService : ReactiveObject
{
    private const string ModelUrl = "https://github.com/danielgatis/rembg/releases/download/v0.0.0/u2net.onnx";
    // Using a reliable direct link to ONNX Runtime GPU (Win x64)
    private const string OnnxRuntimeZipUrl = "https://github.com/microsoft/onnxruntime/releases/download/v1.20.1/onnxruntime-win-x64-gpu-1.20.1.zip";

    private string _lastErrorMessage;
    public string LastErrorMessage
    {
        get => _lastErrorMessage;
        set => this.RaiseAndSetIfChanged(ref _lastErrorMessage, value);
    }

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

    private readonly AppSettingsService _settingsService;

    public AIResourceService(AppSettingsService settingsService)
    {
        _settingsService = settingsService;
        _lastErrorMessage = string.Empty;
    }

    public string GetAIResourcesPath()
    {
        var path = _settingsService.Settings.AIResourcesDirectory;
        if (string.IsNullOrEmpty(path))
        {
            path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AI");
        }
        return path;
    }

    public bool AreResourcesReady()
    {
        var baseDir = GetAIResourcesPath();
        var modelPath = Path.Combine(baseDir, "models", "u2net.onnx");
        // We download the native runtime (onnxruntime.dll), not the managed one (which is in the app dir)
        var onnxDll = Path.Combine(baseDir, "runtime", "onnxruntime.dll");
        
        return File.Exists(modelPath) && File.Exists(onnxDll);
    }

    public async Task<bool> EnsureResourcesAsync()
    {
        if (AreResourcesReady()) return true;
        if (IsDownloading) return false;

        try
        {
            IsDownloading = true;
            DownloadProgress = 0;

            var baseDir = GetAIResourcesPath();
            var runtimeDir = Path.Combine(baseDir, "runtime");
            var modelsDir = Path.Combine(baseDir, "models");

            Directory.CreateDirectory(runtimeDir);
            Directory.CreateDirectory(modelsDir);

            // 1. Download Runtime
            var onnxDll = Path.Combine(runtimeDir, "onnxruntime.dll");
            if (!File.Exists(onnxDll))
            {
                await DownloadAndExtractZip(OnnxRuntimeZipUrl, runtimeDir, 0, 50);
            }
            else
            {
                // Already have runtime, set progress to 50%
                DownloadProgress = 50;
            }

            // 2. Download Model
            var modelPath = Path.Combine(modelsDir, "u2net.onnx");
            if (!File.Exists(modelPath))
            {
                await DownloadFile(ModelUrl, modelPath, 50, 50);
            }
            else
            {
                 DownloadProgress = 100;
            }

            return AreResourcesReady();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AI Resource Download Failed: {ex.Message}");
            LastErrorMessage = ex.Message;
            return false;
        }
        finally
        {
            IsDownloading = false;
        }
    }

    private async Task DownloadFile(string url, string destination, double progressOffset, double progressWeight)
    {
        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromMinutes(15);
        
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        using var contentStream = await response.Content.ReadAsStreamAsync();
        using var fileStream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

        var buffer = new byte[8192];
        long totalRead = 0;
        int read;

        while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            await fileStream.WriteAsync(buffer, 0, read);
            totalRead += read;

            if (totalBytes != -1)
            {
                DownloadProgress = progressOffset + ((double)totalRead / totalBytes * progressWeight);
            }
        }
    }

    private async Task DownloadAndExtractZip(string url, string outputDir, double progressOffset, double progressWeight)
    {
        string zipPath = Path.Combine(outputDir, "temp_ai.zip");
        await DownloadFile(url, zipPath, progressOffset, progressWeight);

        await Task.Run(() =>
        {
            // Official zip has subfolders like onnxruntime-win-x64-gpu-1.20.1/lib/
            // We want to extract just the DLLs from /lib into outputDir
            using (ZipArchive archive = ZipFile.OpenRead(zipPath))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    {
                        // Get only filename
                        string fileName = Path.GetFileName(entry.FullName);
                        string destinationPath = Path.Combine(outputDir, fileName);
                        entry.ExtractToFile(destinationPath, true);
                    }
                }
            }
            File.Delete(zipPath);
        });
    }
}
