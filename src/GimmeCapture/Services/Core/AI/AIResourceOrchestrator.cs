using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.OCR;

namespace GimmeCapture.Services.Core.AI;

public sealed class AIResourceOrchestrator
{
    private readonly IAppSettingsService _settingsService;
    private readonly AIPathService _pathService;
    private readonly NativeResolverService _resolverService;
    private readonly AIModelCatalog _modelCatalog;
    private readonly Action _unloadSam2Runtime;
    private readonly AIResourceInstaller _installer;
    private readonly Action _requestGlobalUnload;

    internal AIResourceOrchestrator(
        IAppSettingsService settingsService,
        AIPathService pathService,
        NativeResolverService resolverService,
        AIModelCatalog modelCatalog,
        AIResourceInstaller installer,
        Action requestGlobalUnload,
        Action unloadSam2Runtime)
    {
        _settingsService = settingsService;
        _pathService = pathService;
        _resolverService = resolverService;
        _modelCatalog = modelCatalog;
        _installer = installer;
        _requestGlobalUnload = requestGlobalUnload;
        _unloadSam2Runtime = unloadSam2Runtime;
    }

    public string GetAIResourcesPath() => _pathService.GetAIResourcesPath();

    public (string Encoder, string Decoder) GetSAM2Paths(SAM2Variant variant) => _pathService.GetSAM2Paths(variant);

    public (string Det, string Rec, string Dict) GetOCRPaths(OCRLanguage language) => _pathService.GetOCRPaths(language);

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

        return _modelCatalog.GetDownloadableLlamaModelPresets().AsValueEnumerable()
            .Where(p => !string.IsNullOrWhiteSpace(p.FileName) && File.Exists(Path.Combine(modelDir, p.FileName)))
            .ToList();
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

    public bool IsAICoreReady()
    {
        var modelPath = _pathService.GetAICoreModelPath();
        // Windows downloads the ONNX runtime as onnxruntime.dll; on Linux/macOS the native runtime
        // (libonnxruntime.so/.dylib) is bundled with the app, so only the model needs to be present.
        if (!OperatingSystem.IsWindows())
        {
            return File.Exists(modelPath);
        }

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
        var paths = GetOCRPaths(_settingsService.Settings.SourceLanguage);
        return File.Exists(paths.Det) && File.Exists(paths.Rec) && File.Exists(paths.Dict);
    }

    public bool IsOCRReady(OCRLanguage language)
    {
        var paths = GetOCRPaths(language);
        return File.Exists(paths.Det) && File.Exists(paths.Rec) && File.Exists(paths.Dict);
    }

    /// <summary>
    /// Whether every installable language is present. This is what the Modules tab reports, because auto-detect can
    /// only distinguish languages whose recognisers exist — a per-current-language check would show "installed"
    /// while Auto was still structurally unable to read Japanese.
    /// </summary>
    public bool IsAllOcrReady() =>
        OcrLanguageResolver.AllReady(OcrLanguageResolver.InstallableLanguages, IsOCRReady);

    public bool AreResourcesReady()
    {
        return IsAICoreReady()
            && IsSAM2Ready(_settingsService.Settings.SelectedSAM2Variant)
            && IsOCRReady()
            && (_settingsService.Settings.SelectedTranslationEngine != TranslationEngine.LlamaSharp || IsLlamaModelReady());
    }

    public bool RemoveAICoreResources() => _installer.RemoveAICoreResources();

    public bool RemoveSAM2Resources(SAM2Variant variant) => _installer.RemoveSAM2Resources(variant);

    public bool RemoveOCRResources() => _installer.RemoveOCRResources();

    public void RemoveResources()
    {
        RemoveAICoreResources();
    }

    public void UnloadAllSessions()
    {
        _unloadSam2Runtime();
    }

    public Task<bool> EnsureAICoreAsync(CancellationToken ct = default) => _installer.EnsureAICoreAsync(ct);

    public Task<bool> EnsureSAM2Async(SAM2Variant variant, CancellationToken ct = default) => _installer.EnsureSAM2Async(variant, ct);

    public Task<bool> EnsureOCRAsync(CancellationToken ct = default) => EnsureOCRAsync(_settingsService.Settings.SourceLanguage, ct);

    public Task<bool> EnsureOCRAsync(OCRLanguage language, CancellationToken ct = default) => _installer.EnsureOCRAsync(language, ct);

    public Task<bool> EnsureAllOcrAsync(CancellationToken ct = default) => _installer.EnsureAllOcrAsync(ct);

    public Task<bool> EnsureLlamaModelAsync(string modelId, CancellationToken ct = default) => _installer.EnsureLlamaModelAsync(modelId, ct);

    public bool RemoveLlamaModelPreset(string modelId) => _installer.RemoveLlamaModelPreset(modelId);

    public void SetupNativeResolvers()
    {
        _resolverService.SetupNativeResolvers();
    }

    public void RequestGlobalUnload()
    {
        _requestGlobalUnload();
    }
}
