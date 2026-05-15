using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ReactiveUI;
using GimmeCapture.Models;

namespace GimmeCapture.Services.Core.AI;

public class AIResourceService : ReactiveObject
{
    private readonly AppSettingsService _settingsService;
    private readonly AIPathService _pathService;
    private readonly NativeResolverService _resolverService;
    private readonly AIModelDownloader _downloader;
    private readonly AIModelCatalog _modelCatalog;
    private readonly Action _unloadSam2Runtime;
    private readonly AIResourceInstaller _installer;

    public AIResourceService(
        AppSettingsService settingsService,
        AIPathService pathService,
        NativeResolverService resolverService,
        AIModelDownloader downloader)
        : this(settingsService, pathService, resolverService, downloader, new AIModelCatalog(), null)
    {
    }

    public AIResourceService(
        AppSettingsService settingsService,
        AIPathService pathService,
        NativeResolverService resolverService,
        AIModelDownloader downloader,
        AIModelCatalog modelCatalog)
        : this(settingsService, pathService, resolverService, downloader, modelCatalog, null)
    {
    }

    public AIResourceService(
        AppSettingsService settingsService,
        AIPathService pathService,
        NativeResolverService resolverService,
        AIModelDownloader downloader,
        AIModelCatalog modelCatalog,
        Action? unloadSam2Runtime)
    {
        _settingsService = settingsService;
        _pathService = pathService;
        _resolverService = resolverService;
        _downloader = downloader;
        _modelCatalog = modelCatalog;
        _unloadSam2Runtime = unloadSam2Runtime ?? (() => { });
        _installer = new AIResourceInstaller(
            _settingsService,
            _pathService,
            _downloader,
            _modelCatalog,
            new AIResourceInstallerCallbacks(
                message => LastErrorMessage = message,
                propertyName => this.RaisePropertyChanged(propertyName),
                () => RequestGlobalUnload?.Invoke(),
                UnloadAllSessions,
                _unloadSam2Runtime,
                IsAICoreReady,
                IsSAM2Ready,
                IsOCRReady,
                IsNmtReady,
                GetLlmModelsDir,
                GetLlamaModelPathById));

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
        return await _installer.EnsureLlamaModelAsync(modelId, ct);
    }

    public bool RemoveLlamaModelPreset(string modelId)
    {
        return _installer.RemoveLlamaModelPreset(modelId);
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
        return _installer.RemoveAICoreResources();
    }

    public bool RemoveSAM2Resources(SAM2Variant variant)
    {
        return _installer.RemoveSAM2Resources(variant);
    }

    public bool RemoveOCRResources()
    {
        return _installer.RemoveOCRResources();
    }

    public bool RemoveNmtResources()
    {
        return _installer.RemoveNmtResources();
    }

    // Deprecated but kept for compatibility if referenced elsewhere
    public void RemoveResources()
    {
        RemoveAICoreResources();
    }

    public void UnloadAllSessions()
    {
        _unloadSam2Runtime();
        // BackgroundRemovalService will handle its own _session via RequestGlobalUnload
    }

    public async Task<bool> EnsureAICoreAsync(CancellationToken ct = default)
    {
        return await _installer.EnsureAICoreAsync(ct);
    }

    public async Task<bool> EnsureSAM2Async(SAM2Variant variant, CancellationToken ct = default)
    {
        return await _installer.EnsureSAM2Async(variant, ct);
    }

    public virtual Task<bool> EnsureOCRAsync(CancellationToken ct = default)
    {
        return EnsureOCRAsync(_settingsService.Settings.SourceLanguage, ct);
    }

    public virtual async Task<bool> EnsureOCRAsync(OCRLanguage language, CancellationToken ct = default)
    {
        return await _installer.EnsureOCRAsync(language, ct);
    }

    public virtual async Task<bool> EnsureNmtAsync(CancellationToken ct = default)
    {
        return await _installer.EnsureNmtAsync(ct);
    }

    public void SetupNativeResolvers()
    {
        _resolverService.SetupNativeResolvers();
    }
}
