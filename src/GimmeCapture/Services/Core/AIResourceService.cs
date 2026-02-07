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
    private const string MobileSamEncoderUrl = "https://huggingface.co/Acly/MobileSAM/resolve/main/mobile_sam_image_encoder.onnx";
    private const string MobileSamDecoderUrl = "https://huggingface.co/Acly/MobileSAM/resolve/main/sam_mask_decoder_multi.onnx";
    
    private const string Sam2EncoderUrl = "https://huggingface.co/shubham0204/sam2-onnx-models/resolve/main/sam2_hiera_tiny_encoder.onnx";
    private const string Sam2DecoderUrl = "https://huggingface.co/shubham0204/sam2-onnx-models/resolve/main/sam2_hiera_tiny_decoder.onnx";
    
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
        
        // 1. Generic Model (U2Net)
        var modelPath = Path.Combine(baseDir, "models", "u2net.onnx");
        
        // 2. MobileSAM Models (Keep for backward compatibility or transition)
        var encoderPath = Path.Combine(baseDir, "models", "mobile_sam_image_encoder.onnx");
        var decoderPath = Path.Combine(baseDir, "models", "sam_mask_decoder_multi.onnx");
        
        // 3. SAM2 Models (NEW)
        var sam2EncoderPath = Path.Combine(baseDir, "models", "sam2_hiera_tiny_encoder.onnx");
        var sam2DecoderPath = Path.Combine(baseDir, "models", "sam2_hiera_tiny_decoder.onnx");
        
        // 4. Runtime
        var onnxDll = Path.Combine(baseDir, "runtime", "onnxruntime.dll");
        
        // We require SAM2 for the new interactive selection features.
        bool hasSam2 = File.Exists(sam2EncoderPath) && File.Exists(sam2DecoderPath);
        
        return File.Exists(modelPath) && hasSam2 && File.Exists(onnxDll);
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
                await DownloadAndExtractZip(OnnxRuntimeZipUrl, runtimeDir, 0, 30);
            }
            else
            {
                DownloadProgress = 30;
            }

            // 2. Download U2Net Model
            var modelPath = Path.Combine(modelsDir, "u2net.onnx");
            if (!File.Exists(modelPath))
            {
                await DownloadFile(ModelUrl, modelPath, 30, 20);
            }
            else
            {
                DownloadProgress = 50;
            }

            // 3. Download MobileSAM Encoder (Largest)
            var encoderPath = Path.Combine(modelsDir, "mobile_sam_image_encoder.onnx");
            if (!File.Exists(encoderPath))
            {
                await DownloadFile(MobileSamEncoderUrl, encoderPath, 50, 40);
            }
            else
            {
                DownloadProgress = 90;
            }

            // 4. Download MobileSAM Decoder
            var decoderPath = Path.Combine(modelsDir, "sam_mask_decoder_multi.onnx");
            if (!File.Exists(decoderPath))
            {
                await DownloadFile(MobileSamDecoderUrl, decoderPath, 90, 5);
            }
            else
            {
                DownloadProgress = 95;
            }

            // 5. Download SAM2 Encoder (NEW)
            var sam2EncoderPath = Path.Combine(modelsDir, "sam2_hiera_tiny_encoder.onnx");
            if (!File.Exists(sam2EncoderPath))
            {
                await DownloadFile(Sam2EncoderUrl, sam2EncoderPath, 95, 3);
            }
            else
            {
                DownloadProgress = 98;
            }

            // 6. Download SAM2 Decoder (NEW)
            var sam2DecoderPath = Path.Combine(modelsDir, "sam2_hiera_tiny_decoder.onnx");
            if (!File.Exists(sam2DecoderPath))
            {
                await DownloadFile(Sam2DecoderUrl, sam2DecoderPath, 98, 2);
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
    private static bool _isResolverSet = false;

    public void SetupNativeResolvers()
    {
        if (_isResolverSet) return;
        _isResolverSet = true;

        try
        {
            // Use .NET's modern DllImportResolver to point directly to the AI/runtime folder.
            // This is much more reliable than modifying PATH at runtime.
            // We only need to set this once for the onnxruntime assembly.
            var onnxAssembly = typeof(Microsoft.ML.OnnxRuntime.InferenceSession).Assembly;
            
            System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(onnxAssembly, (libraryName, assembly, searchPath) =>
            {
                if (libraryName == "onnxruntime")
                {
                    var runtimeDir = Path.Combine(GetAIResourcesPath(), "runtime");
                    var dllPath = Path.Combine(runtimeDir, "onnxruntime.dll");
                    
                    if (File.Exists(dllPath))
                    {
                        System.Diagnostics.Debug.WriteLine($"[AI] Custom Resolver loading: {dllPath}");
                        return System.Runtime.InteropServices.NativeLibrary.Load(dllPath);
                    }
                }
                return IntPtr.Zero;
            });

            // Also add to PATH as fallback for dependencies of onnxruntime.dll (like zlib, etc)
            var runtimeDirFallback = Path.Combine(GetAIResourcesPath(), "runtime");
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            if (!path.Contains(runtimeDirFallback))
            {
                Environment.SetEnvironmentVariable("PATH", runtimeDirFallback + Path.PathSeparator + path);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AI] Failed to setup native resolvers: {ex.Message}");
        }
    }
}
