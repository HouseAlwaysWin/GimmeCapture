using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ReactiveUI;
using GimmeCapture.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Collections.Generic;
using System.Linq;

namespace GimmeCapture.Services.Core;

public class AIResourceService : ReactiveObject
{
    private const string ModelUrl = "https://github.com/danielgatis/rembg/releases/download/v0.0.0/u2net.onnx";
    private const string MobileSamEncoderUrl = "https://huggingface.co/Acly/MobileSAM/resolve/main/mobile_sam_image_encoder.onnx";
    private const string MobileSamDecoderUrl = "https://huggingface.co/Acly/MobileSAM/resolve/main/sam_mask_decoder_multi.onnx";
    
    private const string Sam2TinyEncoderUrl = "https://huggingface.co/shubham0204/sam2-onnx-models/resolve/main/sam2_hiera_tiny_encoder.onnx";
    private const string Sam2TinyDecoderUrl = "https://huggingface.co/shubham0204/sam2-onnx-models/resolve/main/sam2_hiera_tiny_decoder.onnx";
    
    private const string Sam2SmallEncoderUrl = "https://huggingface.co/shubham0204/sam2-onnx-models/resolve/main/sam2_hiera_small_encoder.onnx";
    private const string Sam2SmallDecoderUrl = "https://huggingface.co/shubham0204/sam2-onnx-models/resolve/main/sam2_hiera_small_decoder.onnx";
    
    // Note: Base Plus is significantly larger
    private const string Sam2BasePlusEncoderUrl = "https://huggingface.co/shubham0204/sam2-onnx-models/resolve/main/sam2_hiera_base_plus_encoder.onnx";
    private const string Sam2BasePlusDecoderUrl = "https://huggingface.co/shubham0204/sam2-onnx-models/resolve/main/sam2_hiera_base_plus_decoder.onnx";
    
    private const string Sam2LargeEncoderUrl = "https://huggingface.co/SharpAI/sam2-hiera-large-onnx/resolve/main/encoder.onnx";
    private const string Sam2LargeDecoderUrl = "https://huggingface.co/SharpAI/sam2-hiera-large-onnx/resolve/main/decoder.onnx";

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

    public (string Encoder, string Decoder) GetSAM2Paths(SAM2Variant variant)
    {
        var baseDir = GetAIResourcesPath();
        var modelsDir = Path.Combine(baseDir, "models");
        
        return variant switch
        {
            SAM2Variant.Tiny => (Path.Combine(modelsDir, "sam2_hiera_tiny_encoder.onnx"), Path.Combine(modelsDir, "sam2_hiera_tiny_decoder.onnx")),
            SAM2Variant.Small => (Path.Combine(modelsDir, "sam2_hiera_small_encoder.onnx"), Path.Combine(modelsDir, "sam2_hiera_small_decoder.onnx")),
            SAM2Variant.BasePlus => (Path.Combine(modelsDir, "sam2_hiera_base_plus_encoder.onnx"), Path.Combine(modelsDir, "sam2_hiera_base_plus_decoder.onnx")),
            SAM2Variant.Large => (Path.Combine(modelsDir, "sam2_hiera_large_encoder.onnx"), Path.Combine(modelsDir, "sam2_hiera_large_decoder.onnx")),
            _ => (Path.Combine(modelsDir, "sam2_hiera_tiny_encoder.onnx"), Path.Combine(modelsDir, "sam2_hiera_tiny_decoder.onnx"))
        };
    }

    public bool IsAICoreReady()
    {
        var baseDir = GetAIResourcesPath();
        var modelPath = Path.Combine(baseDir, "models", "u2net.onnx");
        var onnxDll = Path.Combine(baseDir, "runtime", "onnxruntime.dll");
        return File.Exists(modelPath) && File.Exists(onnxDll);
    }

    public bool IsSAM2Ready(SAM2Variant variant)
    {
        var paths = GetSAM2Paths(variant);
        return File.Exists(paths.Encoder) && File.Exists(paths.Decoder);
    }

    // Deprecated monolithic check, keeping for compatibility if needed, but logic should move to specific checks
    public bool AreResourcesReady()
    {
        return IsAICoreReady() && IsSAM2Ready(_settingsService.Settings.SelectedSAM2Variant);
    }

    public void RemoveAICoreResources()
    {
        try
        {
            var baseDir = GetAIResourcesPath();
            var runtimeDir = Path.Combine(baseDir, "runtime");
            var modelsDir = Path.Combine(baseDir, "models");

            if (Directory.Exists(runtimeDir)) Directory.Delete(runtimeDir, true);
            
            var u2net = Path.Combine(modelsDir, "u2net.onnx");
            if (File.Exists(u2net)) File.Delete(u2net);

            this.RaisePropertyChanged(nameof(IsAICoreReady));
            this.RaisePropertyChanged(nameof(AreResourcesReady));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AI Core Removal Failed: {ex.Message}");
        }
    }

    public void RemoveSAM2Resources(SAM2Variant variant)
    {
        try
        {
            var paths = GetSAM2Paths(variant);
            
            if (File.Exists(paths.Encoder)) File.Delete(paths.Encoder);
            if (File.Exists(paths.Decoder)) File.Delete(paths.Decoder);

            this.RaisePropertyChanged(nameof(IsSAM2Ready));
            this.RaisePropertyChanged(nameof(AreResourcesReady));
        }
        catch (Exception ex)
        {
             System.Diagnostics.Debug.WriteLine($"SAM2 Removal Failed: {ex.Message}");
        }
    }

    // Deprecated but kept for compatibility if referenced elsewhere
    public void RemoveResources()
    {
        RemoveAICoreResources();
        // Default to removing all if generic called, or maybe just log warning
        var baseDir = GetAIResourcesPath();
        var modelsDir = Path.Combine(baseDir, "models");
        if (Directory.Exists(modelsDir))
        {
            var files = Directory.GetFiles(modelsDir, "sam2_*.onnx");
            foreach (var f in files) try { File.Delete(f); } catch { }
        }
    }

    public async Task<bool> EnsureAICoreAsync(CancellationToken ct = default)
    {
        if (IsAICoreReady()) return true;
        if (IsDownloading) return true; 

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
                await DownloadAndExtractZip(OnnxRuntimeZipUrl, runtimeDir, 0, 60, ct); 
            }
            else
            {
                DownloadProgress = 60;
            }

            // 2. Download U2Net Model
            var modelPath = Path.Combine(modelsDir, "u2net.onnx");
            if (!File.Exists(modelPath))
            {
                await DownloadFile(ModelUrl, modelPath, 60, 40, ct);
            }
            else
            {
                DownloadProgress = 100;
            }

            return IsAICoreReady();
        }
        catch (OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine("AI Core Download Cancelled");
            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AI Core Download Failed: {ex.Message}");
            LastErrorMessage = ex.Message;
            return false;
        }
        finally
        {
            IsDownloading = false;
        }
    }

    public async Task<bool> EnsureSAM2Async(SAM2Variant variant, CancellationToken ct = default)
    {
        if (IsSAM2Ready(variant)) return true;
        if (IsDownloading) return true;

        try
        {
            IsDownloading = true;
            DownloadProgress = 0;

            var baseDir = GetAIResourcesPath();
            var modelsDir = Path.Combine(baseDir, "models");
            Directory.CreateDirectory(modelsDir);

            var paths = GetSAM2Paths(variant);
            
            // Determine URLs
            string encoderUrl = variant switch {
                SAM2Variant.Tiny => Sam2TinyEncoderUrl,
                SAM2Variant.Small => Sam2SmallEncoderUrl,
                SAM2Variant.BasePlus => Sam2BasePlusEncoderUrl,
                SAM2Variant.Large => Sam2LargeEncoderUrl,
                _ => Sam2TinyEncoderUrl
            };
            
            string decoderUrl = variant switch {
                SAM2Variant.Tiny => Sam2TinyDecoderUrl,
                SAM2Variant.Small => Sam2SmallDecoderUrl,
                SAM2Variant.BasePlus => Sam2BasePlusDecoderUrl,
                SAM2Variant.Large => Sam2LargeDecoderUrl,
                _ => Sam2TinyDecoderUrl
            };

            // 1. Download Encoder
            if (!File.Exists(paths.Encoder))
            {
                // Encoder is much larger (~90%)
                await DownloadFile(encoderUrl, paths.Encoder, 0, 90, ct);
            }
            else
            {
                DownloadProgress = 90;
            }

            // 2. Download Decoder
            if (!File.Exists(paths.Decoder))
            {
                await DownloadFile(decoderUrl, paths.Decoder, 90, 10, ct);
            }
            else
            {
                DownloadProgress = 100;
            }

            return IsSAM2Ready(variant);
        }
        catch (OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine("SAM2 Download Cancelled");
            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SAM2 Download Failed: {ex.Message}");
            LastErrorMessage = ex.Message;
            return false;
        }
        finally
        {
            IsDownloading = false;
        }
    }

    private async Task DownloadFile(string url, string destination, double progressOffset, double progressWeight, CancellationToken ct)
    {
        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromMinutes(15);
        
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        using var fileStream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

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
    }

    private async Task DownloadAndExtractZip(string url, string outputDir, double progressOffset, double progressWeight, CancellationToken ct)
    {
        string zipPath = Path.Combine(outputDir, "temp_ai.zip");
        await DownloadFile(url, zipPath, progressOffset, progressWeight, ct);

        await Task.Run(() =>
        {
            if (ct.IsCancellationRequested) return;

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
        }, ct);
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

    private InferenceSession? _cachedEncoder;
    private InferenceSession? _cachedDecoder;
    private SAM2Variant? _cachedVariant;
    private bool _isWarmedUp = false;
    private readonly SemaphoreSlim _modelLoadingLock = new(1, 1);

    public async Task LoadSAM2ModelsAsync(SAM2Variant variant)
    {
        if (_cachedVariant == variant && _cachedEncoder != null && _cachedDecoder != null) return;

        await _modelLoadingLock.WaitAsync();
        try
        {
             if (_cachedVariant == variant && _cachedEncoder != null && _cachedDecoder != null) return;

             UnloadSAM2Models();

             var paths = GetSAM2Paths(variant);
             if (!File.Exists(paths.Encoder) || !File.Exists(paths.Decoder))
             {
                 System.Diagnostics.Debug.WriteLine("[AI] Check Model files missing, cannot load.");
                 return;
             }

             await Task.Run(async () =>
             {
                 try
                 {
                     var options = new SessionOptions
                     {
                         GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_BASIC,
                         LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
                     };
                     
                     // Try GPU if available
                     try { options.AppendExecutionProvider_CUDA(0); } catch { }
                     try { options.AppendExecutionProvider_DML(0); } catch { }
                     
                     System.Diagnostics.Debug.WriteLine($"[AI] Loading Encoder: {paths.Encoder}");
                     _cachedEncoder = new InferenceSession(paths.Encoder, options);
                     
                     System.Diagnostics.Debug.WriteLine($"[AI] Loading Decoder: {paths.Decoder}");
                     _cachedDecoder = new InferenceSession(paths.Decoder, options);
                     
                     _cachedVariant = variant;
                     _isWarmedUp = false; // Reset for new variant
                     System.Diagnostics.Debug.WriteLine("[AI] Models Loaded Successfully");
                     
                     // Centralized Warmup: Trigger it once when sessions are created
                     await WarmupSessionsAsync();
                 }
                 catch (Exception ex)
                 {
                     System.Diagnostics.Debug.WriteLine($"[AI] Model Load Error: {ex.Message}");
                     UnloadSAM2Models();
                     throw;
                 }
             });
        }
        finally
        {
            _modelLoadingLock.Release();
        }
    }

    private async Task WarmupSessionsAsync()
    {
        if (_isWarmedUp || _cachedEncoder == null || _cachedDecoder == null) return;

        System.Diagnostics.Debug.WriteLine("[AI] Warming up SAM2 sessions centralized...");
        try
        {
            // Encoder Warmup
            var encoderInput = new DenseTensor<float>(new[] { 1, 3, 1024, 1024 });
            var encInputMetaData = _cachedEncoder.InputMetadata;
            var encInputName = encInputMetaData.Keys.FirstOrDefault(k => k == "image" || k == "pixel_values") ?? "image";
            var encInputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(encInputName, encoderInput) };
            using var encResults = _cachedEncoder.Run(encInputs);

            // Decoder Warmup (Requires mock embeddings and points)
            var decInputMetaData = _cachedDecoder.InputMetadata;
            var decInputNames = decInputMetaData.Keys.ToList();
            var decInputs = new List<NamedOnnxValue>();

            void AddMock(string[] aliases, int[] dims, float val = 0f)
            {
                var name = decInputNames.FirstOrDefault(n => aliases.Any(a => n == a || n == a.Replace("_", "") || n.Contains(a)));
                if (name == null) return;
                
                var meta = decInputMetaData[name];
                if (meta.ElementType == typeof(int))
                {
                    var data = new int[dims.Aggregate(1, (a, b) => a * b)];
                    if (val != 0) Array.Fill(data, (int)val);
                    decInputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<int>(data, dims)));
                }
                else if (meta.ElementType == typeof(long))
                {
                    var data = new long[dims.Aggregate(1, (a, b) => a * b)];
                    if (val != 0) Array.Fill(data, (long)val);
                    decInputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(data, dims)));
                }
                else
                {
                    var data = new float[dims.Aggregate(1, (a, b) => a * b)];
                    if (val != 0) Array.Fill(data, val);
                    decInputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<float>(data, dims)));
                }
            }

            AddMock(new[] { "image_embeddings", "image_embed", "embeddings", "image_embedding" }, new[] { 1, 256, 64, 64 });
            AddMock(new[] { "high_res_feats_0", "feat_0", "high_res_feat_0" }, new[] { 1, 32, 256, 256 });
            AddMock(new[] { "high_res_feats_1", "feat_1", "high_res_feat_1" }, new[] { 1, 64, 128, 128 });
            AddMock(new[] { "point_coords", "coords" }, new[] { 1, 1, 2 });
            AddMock(new[] { "point_labels", "labels" }, new[] { 1, 1 }, 1f);
            AddMock(new[] { "mask_input", "mask" }, new[] { 1, 1, 256, 256 });
            AddMock(new[] { "has_mask_input", "has_mask" }, new[] { 1 }, 0f);
            AddMock(new[] { "orig_im_size", "im_size" }, new[] { 2 }, 1024f);
            
            using var decResults = _cachedDecoder.Run(decInputs);
            
            _isWarmedUp = true;
            System.Diagnostics.Debug.WriteLine("[AI] Centralized warmup complete.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AI] Session Warmup Warning (Non-fatal): {ex.Message}");
        }
    }

    public (InferenceSession? Encoder, InferenceSession? Decoder) GetSAM2Sessions()
    {
        return (_cachedEncoder, _cachedDecoder);
    }

    public void UnloadSAM2Models()
    {
        _cachedEncoder?.Dispose();
        _cachedEncoder = null;
        
        _cachedDecoder?.Dispose();
        _cachedDecoder = null;
        
        _cachedVariant = null;
        _isWarmedUp = false;
    }
}
