using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ReactiveUI;
using GimmeCapture.Models;

namespace GimmeCapture.Services.Core.AI;

public class AIResourceService : ReactiveObject
{
    private readonly AIModelDownloader _downloader;
    private readonly AIModelCatalog _modelCatalog;
    private readonly AIResourceInstaller _installer;
    private readonly AIResourceOrchestrator _orchestrator = null!;

    public AIResourceService(
        IAppSettingsService settingsService,
        AIPathService pathService,
        NativeResolverService resolverService,
        AIModelDownloader downloader)
        : this(settingsService, pathService, resolverService, downloader, new AIModelCatalog(), null)
    {
    }

    public AIResourceService(
        IAppSettingsService settingsService,
        AIPathService pathService,
        NativeResolverService resolverService,
        AIModelDownloader downloader,
        AIModelCatalog modelCatalog)
        : this(settingsService, pathService, resolverService, downloader, modelCatalog, null)
    {
    }

    public AIResourceService(
        IAppSettingsService settingsService,
        AIPathService pathService,
        NativeResolverService resolverService,
        AIModelDownloader downloader,
        AIModelCatalog modelCatalog,
        Action? unloadSam2Runtime)
    {
        _downloader = downloader;
        _modelCatalog = modelCatalog;
        _installer = new AIResourceInstaller(
            settingsService,
            pathService,
            _downloader,
            _modelCatalog,
            new AIResourceInstallerCallbacks(
                message => LastErrorMessage = message,
                propertyName => this.RaisePropertyChanged(propertyName),
                () => RequestGlobalUnload?.Invoke(),
                UnloadAllSessions,
                unloadSam2Runtime ?? (() => { }),
                () => _orchestrator.IsAICoreReady(),
                variant => _orchestrator.IsSAM2Ready(variant),
                language => _orchestrator.IsOCRReady(language),
                () => _orchestrator.GetLlmModelsDir(),
                modelId => _orchestrator.GetLlamaModelPathById(modelId)));
        _orchestrator = new AIResourceOrchestrator(
            settingsService,
            pathService,
            resolverService,
            _modelCatalog,
            _installer,
            () => RequestGlobalUnload?.Invoke(),
            unloadSam2Runtime ?? (() => { }));

        // Redirect progress changes
        _downloader.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(AIModelDownloader.DownloadProgress)) this.RaisePropertyChanged(nameof(DownloadProgress));
            if (e.PropertyName == nameof(AIModelDownloader.IsDownloading)) this.RaisePropertyChanged(nameof(IsDownloading));
            if (e.PropertyName == nameof(AIModelDownloader.CurrentDownloadName)) this.RaisePropertyChanged(nameof(CurrentDownloadName));
            if (e.PropertyName == nameof(AIModelDownloader.DownloadStage)) this.RaisePropertyChanged(nameof(DownloadStage));
        };
    }

    public AIModelCatalog Catalog => _modelCatalog;
    public AIResourceOrchestrator Orchestrator => _orchestrator;

    public string GetAIResourcesPath() => _orchestrator.GetAIResourcesPath();

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
    public ArtifactDownloadStage DownloadStage => _downloader.DownloadStage;

    public (string Encoder, string Decoder) GetSAM2Paths(SAM2Variant variant) => _orchestrator.GetSAM2Paths(variant);

    public virtual (string Det, string Rec, string Dict) GetOCRPaths(OCRLanguage language) => _orchestrator.GetOCRPaths(language);

    public IReadOnlyList<LlamaModelPreset> GetLlamaModelPresets() => _modelCatalog.GetLlamaModelPresets();
    public IReadOnlyList<LlamaModelPreset> GetDownloadableLlamaModelPresets() => _modelCatalog.GetDownloadableLlamaModelPresets();

    public string GetLlmModelsDir() => _orchestrator.GetLlmModelsDir();

    public string GetLlamaModelPathById(string modelId) => _orchestrator.GetLlamaModelPathById(modelId);

    public bool IsLlamaPresetInstalled(string modelId) => _orchestrator.IsLlamaPresetInstalled(modelId);

    public IReadOnlyList<LlamaModelPreset> GetInstalledLlamaModelPresets() => _orchestrator.GetInstalledLlamaModelPresets();

    public string GetSelectedLlamaModelPath() => _orchestrator.GetSelectedLlamaModelPath();

    public bool IsLlamaModelReady() => _orchestrator.IsLlamaModelReady();

    public async Task<bool> EnsureLlamaModelAsync(string modelId, CancellationToken ct = default)
    {
        return await _installer.EnsureLlamaModelAsync(modelId, ct);
    }

    public bool RemoveLlamaModelPreset(string modelId)
    {
        return _installer.RemoveLlamaModelPreset(modelId);
    }

    public bool IsAICoreReady() => _orchestrator.IsAICoreReady();

    public bool IsSAM2Ready(SAM2Variant variant) => _orchestrator.IsSAM2Ready(variant);

    public bool IsOCRReady() => _orchestrator.IsOCRReady();

    public bool IsOCRReady(OCRLanguage language) => _orchestrator.IsOCRReady(language);

    public bool AreResourcesReady() => _orchestrator.AreResourcesReady();

    public bool RemoveAICoreResources() => _orchestrator.RemoveAICoreResources();

    public bool RemoveSAM2Resources(SAM2Variant variant) => _orchestrator.RemoveSAM2Resources(variant);

    public bool RemoveOCRResources() => _orchestrator.RemoveOCRResources();

    // Deprecated but kept for compatibility if referenced elsewhere
    public void RemoveResources()
    {
        _orchestrator.RemoveResources();
    }

    public void UnloadAllSessions() => _orchestrator.UnloadAllSessions();

    public async Task<bool> EnsureAICoreAsync(CancellationToken ct = default) => await _orchestrator.EnsureAICoreAsync(ct);

    public async Task<bool> EnsureSAM2Async(SAM2Variant variant, CancellationToken ct = default) => await _orchestrator.EnsureSAM2Async(variant, ct);

    public virtual Task<bool> EnsureOCRAsync(CancellationToken ct = default) => _orchestrator.EnsureOCRAsync(ct);

    public virtual async Task<bool> EnsureOCRAsync(OCRLanguage language, CancellationToken ct = default) => await _orchestrator.EnsureOCRAsync(language, ct);

    public void SetupNativeResolvers()
    {
        _orchestrator.SetupNativeResolvers();
    }
}
