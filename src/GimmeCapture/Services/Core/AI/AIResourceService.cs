using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ReactiveUI;
using GimmeCapture.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Collections.Generic;
namespace GimmeCapture.Services.Core.AI;

public class AIResourceService : ReactiveObject
{
    private readonly AppSettingsService _settingsService;
    private readonly AIPathService _pathService;
    private readonly NativeResolverService _resolverService;
    private readonly AIModelDownloader _downloader;
    private readonly AIModelCatalog _modelCatalog;

    public AIResourceService(
        AppSettingsService settingsService,
        AIPathService pathService,
        NativeResolverService resolverService,
        AIModelDownloader downloader)
        : this(settingsService, pathService, resolverService, downloader, new AIModelCatalog())
    {
    }

    public AIResourceService(
        AppSettingsService settingsService,
        AIPathService pathService,
        NativeResolverService resolverService,
        AIModelDownloader downloader,
        AIModelCatalog modelCatalog)
    {
        _settingsService = settingsService;
        _pathService = pathService;
        _resolverService = resolverService;
        _downloader = downloader;
        _modelCatalog = modelCatalog;

        // Redirect progress changes
        _downloader.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(AIModelDownloader.DownloadProgress)) this.RaisePropertyChanged(nameof(DownloadProgress));
            if (e.PropertyName == nameof(AIModelDownloader.IsDownloading)) this.RaisePropertyChanged(nameof(IsDownloading));
            if (e.PropertyName == nameof(AIModelDownloader.CurrentDownloadName)) this.RaisePropertyChanged(nameof(CurrentDownloadName));
        };
    }

    public string GetAIResourcesPath() => _pathService.GetAIResourcesPath();

    private string _lastErrorMessage = string.Empty;
    public string LastErrorMessage
    {
        get => _lastErrorMessage;
        set => this.RaiseAndSetIfChanged(ref _lastErrorMessage, value);
    }

    public static event Action? RequestGlobalUnload;

    public double DownloadProgress => _downloader.DownloadProgress;
    public bool IsDownloading => _downloader.IsDownloading;
    public string CurrentDownloadName => _downloader.CurrentDownloadName;

    public (string Encoder, string Decoder) GetSAM2Paths(SAM2Variant variant) => _pathService.GetSAM2Paths(variant);

    public virtual (string Det, string Rec, string Dict) GetOCRPaths(OCRLanguage language) => _pathService.GetOCRPaths(language);

    public virtual bool IsNmtReady()
    {
        var paths = GetNmtPaths();
        string[] files = { paths.Encoder, paths.Decoder, paths.Spm, paths.Config };
        
        foreach (var file in files)
        {
            if (!File.Exists(file)) return false;
            
            var info = new FileInfo(file);
            // Quantized model size checks (encoder ~288MB, decoder ~339MB)
            if (file.EndsWith("encoder_model.onnx") && info.Length < 50 * 1024 * 1024) return false;
            if (file.EndsWith("decoder_model.onnx") && info.Length < 50 * 1024 * 1024) return false;
        }
        return true;
    }

    public virtual (string Encoder, string Decoder, string Tokenizer, string Spm, string Config, string GenConfig) GetNmtPaths() => _pathService.GetNmtPaths();

    public IReadOnlyList<LlamaModelPreset> GetLlamaModelPresets() => _modelCatalog.GetLlamaModelPresets();
    public IReadOnlyList<LlamaModelPreset> GetDownloadableLlamaModelPresets() => _modelCatalog.GetDownloadableLlamaModelPresets();

    public string GetLlmModelsDir()
    {
        string custom = _settingsService.Settings.LlamaCustomModelPath;
        if (!string.IsNullOrWhiteSpace(custom))
        {
            if (File.Exists(custom))
            {
                return Path.GetDirectoryName(custom) ?? custom;
            }

            return custom;
        }

        return Path.Combine(GetAIResourcesPath(), "llm", "models");
    }

    public string GetLlamaModelPathById(string modelId)
    {
        if (_modelCatalog.TryGetLlamaModelPreset(modelId, out var preset) && !string.IsNullOrWhiteSpace(preset.FileName))
        {
            return Path.Combine(GetLlmModelsDir(), preset.FileName);
        }

        return string.Empty;
    }

    public bool IsLlamaPresetInstalled(string modelId)
    {
        string path = GetLlamaModelPathById(modelId);
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
    }

    public IReadOnlyList<LlamaModelPreset> GetInstalledLlamaModelPresets()
    {
        string modelDir = GetLlmModelsDir();
        if (!Directory.Exists(modelDir))
        {
            return Array.Empty<LlamaModelPreset>();
        }

        var installed = _modelCatalog.GetLlamaModelPresets().AsValueEnumerable()
            .Where(p => !string.IsNullOrWhiteSpace(p.FileName) && File.Exists(Path.Combine(modelDir, p.FileName)))
            .ToList();
        return installed;
    }

    public string GetSelectedLlamaModelPath()
    {
        var settings = _settingsService.Settings;
        string path = GetLlamaModelPathById(settings.LlamaModelId);
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            return path;
        }

        if (!string.IsNullOrWhiteSpace(settings.LlamaCustomModelPath))
        {
            if (Directory.Exists(settings.LlamaCustomModelPath))
            {
                var first = Directory.GetFiles(settings.LlamaCustomModelPath, "*.gguf", SearchOption.TopDirectoryOnly)
                    .AsValueEnumerable()
                    .FirstOrDefault();
                return first ?? string.Empty;
            }

            return settings.LlamaCustomModelPath;
        }

        return path;
    }

    public bool IsLlamaModelReady()
    {
        string modelPath = GetSelectedLlamaModelPath();
        return !string.IsNullOrWhiteSpace(modelPath) && File.Exists(modelPath);
    }

    public async Task<bool> EnsureLlamaModelAsync(string modelId, CancellationToken ct = default)
    {
        if (!_modelCatalog.TryGetLlamaModelPreset(modelId, out var preset) ||
            string.IsNullOrWhiteSpace(preset.DownloadUrl) ||
            string.IsNullOrWhiteSpace(preset.FileName))
        {
            LastErrorMessage = "Selected Llama model is not downloadable. Please place a GGUF file in the custom model path.";
            return false;
        }

        string modelDir = GetLlmModelsDir();
        Directory.CreateDirectory(modelDir);
        string modelPath = Path.Combine(modelDir, preset.FileName);
        if (File.Exists(modelPath))
        {
            return true;
        }

        await _downloadLock.WaitAsync(ct);
        try
        {
            _downloader.IsDownloading = true;
            _downloader.CurrentDownloadName = $"LLM Model ({preset.DisplayName})";
            _downloader.DownloadProgress = 0;
            await _downloader.DownloadFileAsync(preset.DownloadUrl, modelPath, 0, 100, ct);
            _settingsService.Settings.LlamaModelId = modelId;
            return File.Exists(modelPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastErrorMessage = ex.Message;
            return false;
        }
        finally
        {
            _downloader.IsDownloading = false;
            _downloadLock.Release();
        }
    }

    public bool RemoveLlamaModelPreset(string modelId)
    {
        try
        {
            string path = GetLlamaModelPathById(modelId);
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
            return true;
        }
        catch (Exception ex)
        {
            LastErrorMessage = $"LLM model removal failed: {ex.Message}";
            return false;
        }
    }

    public bool IsAICoreReady()
    {
        var modelPath = _pathService.GetAICoreModelPath();
        var onnxDll = _pathService.GetOnnxDllPath();
        return File.Exists(modelPath) && File.Exists(onnxDll);
    }

    public bool IsSAM2Ready(SAM2Variant variant)
    {
        var paths = GetSAM2Paths(variant);
        return File.Exists(paths.Encoder) && File.Exists(paths.Decoder);
    }

    public bool IsOCRReady()
    {
        // Check if CURRENT selected language model exists
        var paths = GetOCRPaths(_settingsService.Settings.SourceLanguage);
        return File.Exists(paths.Det) && File.Exists(paths.Rec) && File.Exists(paths.Dict);
    }

    public bool IsOCRReady(OCRLanguage language)
    {
        var paths = GetOCRPaths(language);
        return File.Exists(paths.Det) && File.Exists(paths.Rec) && File.Exists(paths.Dict);
    }

    // Deprecated monolithic check, keeping for compatibility if needed, but logic should move to specific checks
    public bool AreResourcesReady()
    {
        return IsAICoreReady() && IsSAM2Ready(_settingsService.Settings.SelectedSAM2Variant) && IsOCRReady() &&
               (_settingsService.Settings.SelectedTranslationEngine != TranslationEngine.LlamaSharp || IsLlamaModelReady());
    }

    public bool IsNmtResourcesPresent()
    {
        var paths = GetNmtPaths();
        return File.Exists(paths.Encoder) && File.Exists(paths.Decoder) && File.Exists(paths.Tokenizer);
    }

    public bool RemoveAICoreResources()
    {
        try
        {
            RequestGlobalUnload?.Invoke();
            UnloadAllSessions();

            var runtimeDir = _pathService.GetRuntimeDir();
            var modelsDir = _pathService.GetAIModelsDir();
            var u2net = _pathService.GetAICoreModelPath();

            if (File.Exists(u2net)) File.Delete(u2net);
            if (Directory.Exists(runtimeDir)) Directory.Delete(runtimeDir, true);

            this.RaisePropertyChanged(nameof(IsAICoreReady));
            this.RaisePropertyChanged(nameof(AreResourcesReady));
            return true;
        }
        catch (Exception ex)
        {
            LastErrorMessage = $"AI Core Removal Failed: {ex.Message}";
            System.Diagnostics.Debug.WriteLine(LastErrorMessage);
            return false;
        }
    }

    public bool RemoveSAM2Resources(SAM2Variant variant)
    {
        try
        {
            RequestGlobalUnload?.Invoke();
            UnloadSAM2Models();

            var paths = _pathService.GetSAM2Paths(variant);
            
            if (File.Exists(paths.Encoder)) File.Delete(paths.Encoder);
            if (File.Exists(paths.Decoder)) File.Delete(paths.Decoder);

            this.RaisePropertyChanged(nameof(IsSAM2Ready));
            this.RaisePropertyChanged(nameof(AreResourcesReady));
            return true;
        }
        catch (Exception ex)
        {
            LastErrorMessage = $"SAM2 Removal Failed: {ex.Message}";
            System.Diagnostics.Debug.WriteLine(LastErrorMessage);
            return false;
        }
    }

    public bool RemoveOCRResources()
    {
        try
        {
            RequestGlobalUnload?.Invoke();
            
            var baseDir = _pathService.GetAIResourcesPath();
            var ocrDir = Path.Combine(baseDir, "ocr");

            if (Directory.Exists(ocrDir)) Directory.Delete(ocrDir, true);

            this.RaisePropertyChanged(nameof(IsOCRReady));
            this.RaisePropertyChanged(nameof(AreResourcesReady));
            return true;
        }
        catch (Exception ex)
        {
            LastErrorMessage = $"OCR Removal Failed: {ex.Message}";
            System.Diagnostics.Debug.WriteLine(LastErrorMessage);
            return false;
        }
    }

    public bool RemoveNmtResources()
    {
        try
        {
            var baseDir = _pathService.GetAIResourcesPath();
            var nmtDir = Path.Combine(baseDir, "nmt");
            if (Directory.Exists(nmtDir)) Directory.Delete(nmtDir, true);
            this.RaisePropertyChanged(nameof(IsNmtReady));
            return true;
        }
        catch (Exception ex)
        {
            LastErrorMessage = $"NMT Removal Failed: {ex.Message}";
            System.Diagnostics.Debug.WriteLine(LastErrorMessage);
            return false;
        }
    }

    // Deprecated but kept for compatibility if referenced elsewhere
    public void RemoveResources()
    {
        RemoveAICoreResources();
    }

    public void UnloadAllSessions()
    {
        UnloadSAM2Models();
        // BackgroundRemovalService will handle its own _session via RequestGlobalUnload
    }

    private readonly SemaphoreSlim _downloadLock = new(1, 1);


    public async Task<bool> EnsureAICoreAsync(CancellationToken ct = default)
    {
        if (IsAICoreReady()) return true;

        await _downloadLock.WaitAsync(ct);
        try
        {
            if (IsAICoreReady()) return true;

            _downloader.IsDownloading = true;
            _downloader.CurrentDownloadName = "AI Core";
            _downloader.DownloadProgress = 0;

            var baseDir = _pathService.GetAIResourcesPath();
            var runtimeDir = _pathService.GetRuntimeDir();
            var modelsDir = _pathService.GetAIModelsDir();

            Directory.CreateDirectory(runtimeDir);
            Directory.CreateDirectory(modelsDir);

            var package = _modelCatalog.GetAICorePackage();

            // 1. Download Runtime
            var onnxDll = _pathService.GetOnnxDllPath();
            if (!File.Exists(onnxDll))
            {
                await _downloader.DownloadAndExtractZipAsync(package.OnnxRuntimeZipUrl, runtimeDir, 0, 60, ct); 
            }
            else
            {
                _downloader.DownloadProgress = 60;
            }

            // 2. Download U2Net Model
            var modelPath = _pathService.GetAICoreModelPath();
            if (!File.Exists(modelPath))
            {
                await _downloader.DownloadFileAsync(package.U2NetModelUrl, modelPath, 60, 40, ct);
            }
            else
            {
                _downloader.DownloadProgress = 100;
            }

            return IsAICoreReady();
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            LastErrorMessage = ex.Message;
            return false;
        }
        finally
        {
            _downloader.IsDownloading = false;
            _downloadLock.Release();
        }
    }

    public async Task<bool> EnsureSAM2Async(SAM2Variant variant, CancellationToken ct = default)
    {
        if (IsSAM2Ready(variant)) return true;

        await _downloadLock.WaitAsync(ct);
        try
        {
            if (IsSAM2Ready(variant)) return true;

            _downloader.IsDownloading = true;
            _downloader.CurrentDownloadName = $"SAM2 Model ({variant})";
            _downloader.DownloadProgress = 0;

            var baseDir = _pathService.GetAIResourcesPath();
            var modelsDir = _pathService.GetAIModelsDir();
            Directory.CreateDirectory(modelsDir);

            var paths = _pathService.GetSAM2Paths(variant);
            var package = _modelCatalog.GetSam2Package(variant);

            // 1. Download Encoder
            if (!File.Exists(paths.Encoder))
            {
                await _downloader.DownloadFileAsync(package.EncoderUrl, paths.Encoder, 0, 90, ct);
            }
            else
            {
                _downloader.DownloadProgress = 90;
            }

            // 2. Download Decoder
            if (!File.Exists(paths.Decoder))
            {
                await _downloader.DownloadFileAsync(package.DecoderUrl, paths.Decoder, 90, 10, ct);
            }
            else
            {
                _downloader.DownloadProgress = 100;
            }

            return IsSAM2Ready(variant);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            LastErrorMessage = ex.Message;
            return false;
        }
        finally
        {
            _downloader.IsDownloading = false;
            _downloadLock.Release();
        }
    }

    public virtual Task<bool> EnsureOCRAsync(CancellationToken ct = default)
    {
        return EnsureOCRAsync(_settingsService.Settings.SourceLanguage, ct);
    }

    public virtual async Task<bool> EnsureOCRAsync(OCRLanguage language, CancellationToken ct = default)
    {
        if (IsOCRReady(language)) return true;

        await _downloadLock.WaitAsync(ct);
        try
        {
            if (IsOCRReady(language)) return true;

            _downloader.IsDownloading = true;
            _downloader.CurrentDownloadName = "OCR Models (PaddleOCR v5)";
            _downloader.DownloadProgress = 0;

            var baseDir = _pathService.GetAIResourcesPath();
            var ocrDir = Path.Combine(baseDir, "ocr");
            Directory.CreateDirectory(ocrDir);

            var paths = _pathService.GetOCRPaths(language);
            var package = _modelCatalog.GetOcrPackage(language);

            if (!File.Exists(paths.Det))
                await _downloader.DownloadFileAsync(package.DetectionUrl, paths.Det, 0, 40, ct);
            else
                _downloader.DownloadProgress = 40;

            if (!File.Exists(paths.Rec))
                await _downloader.DownloadFileAsync(package.RecognitionUrl, paths.Rec, 40, 50, ct);
            else
                _downloader.DownloadProgress = 90;

            if (!File.Exists(paths.Dict))
                await _downloader.DownloadFileAsync(package.DictionaryUrl, paths.Dict, 90, 10, ct);
            else
                _downloader.DownloadProgress = 100;

            return IsOCRReady(language);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            LastErrorMessage = ex.Message;
            return false;
        }
        finally
        {
            _downloader.IsDownloading = false;
            _downloadLock.Release();
        }
    }

    public virtual async Task<bool> EnsureNmtAsync(CancellationToken ct = default)
    {
        if (IsNmtReady()) return true;

        await _downloadLock.WaitAsync(ct);
        try
        {
            if (IsNmtReady()) return true;

            _downloader.IsDownloading = true;
            _downloader.CurrentDownloadName = "NMT Translation Models (MarianMT)";
            _downloader.DownloadProgress = 0;

            var baseDir = _pathService.GetAIResourcesPath();
            var nmtDir = Path.Combine(baseDir, "nmt");
            Directory.CreateDirectory(nmtDir);

            var paths = _pathService.GetNmtPaths();
            var package = _modelCatalog.GetNmtPackage();

            if (!File.Exists(paths.Encoder))
                await _downloader.DownloadFileAsync(package.EncoderUrl, paths.Encoder, 0, 40, ct);
            else
                _downloader.DownloadProgress = 40;

            if (!File.Exists(paths.Decoder))
                await _downloader.DownloadFileAsync(package.DecoderUrl, paths.Decoder, 40, 50, ct);
            else
                _downloader.DownloadProgress = 90;

            if (!File.Exists(paths.Tokenizer))
                await _downloader.DownloadFileAsync(package.TokenizerUrl, paths.Tokenizer, 90, 2.5, ct);

            if (!File.Exists(paths.Spm))
                await _downloader.DownloadFileAsync(package.SentencePieceUrl, paths.Spm, 92.5, 2.5, ct);
            
            if (!File.Exists(paths.Config))
                await _downloader.DownloadFileAsync(package.ConfigUrl, paths.Config, 95, 2.5, ct);
            if (!File.Exists(paths.GenConfig))
                await _downloader.DownloadFileAsync(package.GenerationConfigUrl, paths.GenConfig, 97.5, 2.5, ct);

            _downloader.DownloadProgress = 100;
            return IsNmtReady();
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            LastErrorMessage = ex.Message;
            return false;
        }
        finally
        {
            _downloader.IsDownloading = false;
            _downloadLock.Release();
        }
    }

    public void SetupNativeResolvers()
    {
        _resolverService.SetupNativeResolvers();
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
                     WarmupSessions();
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

    private void WarmupSessions()
    {
        if (_isWarmedUp || _cachedEncoder == null || _cachedDecoder == null) return;

        System.Diagnostics.Debug.WriteLine("[AI] Warming up SAM2 sessions centralized...");
        try
        {
            // Encoder Warmup
            var encoderInput = new DenseTensor<float>(new[] { 1, 3, 1024, 1024 });
            var encInputMetaData = _cachedEncoder.InputMetadata;
            var encInputName = encInputMetaData.Keys.AsValueEnumerable().FirstOrDefault(k => k == "image" || k == "pixel_values") ?? "image";
            var encInputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(encInputName, encoderInput) };
            using var encResults = _cachedEncoder.Run(encInputs);

            // Decoder Warmup (Requires mock embeddings and points)
            var decInputMetaData = _cachedDecoder.InputMetadata;
            var decInputNames = decInputMetaData.Keys.AsValueEnumerable().ToList();
            var decInputs = new List<NamedOnnxValue>();

            void AddMock(string[] aliases, int[] dims, float val = 0f)
            {
                var name = decInputNames.AsValueEnumerable().FirstOrDefault(n => aliases.AsValueEnumerable().Any(a => n == a || n == a.Replace("_", "") || n.Contains(a)));
                if (name == null) return;
                
                var meta = decInputMetaData[name];
                if (meta.ElementType == typeof(int))
                {
                    var data = new int[dims.AsValueEnumerable().Aggregate(1, (a, b) => a * b)];
                    if (val != 0) Array.Fill(data, (int)val);
                    decInputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<int>(data, dims)));
                }
                else if (meta.ElementType == typeof(long))
                {
                    var data = new long[dims.AsValueEnumerable().Aggregate(1, (a, b) => a * b)];
                    if (val != 0) Array.Fill(data, (long)val);
                    decInputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(data, dims)));
                }
                else
                {
                    var data = new float[dims.AsValueEnumerable().Aggregate(1, (a, b) => a * b)];
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
