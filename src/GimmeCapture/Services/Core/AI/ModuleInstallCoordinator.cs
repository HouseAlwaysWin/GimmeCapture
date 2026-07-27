using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.Infrastructure;
using ReactiveUI;

namespace GimmeCapture.Services.Core.AI;

public sealed class ModuleInstallCoordinator
{
    private readonly AIModelCatalog _modelCatalog;
    private readonly Lazy<AIResourceService> _aiResourceService;
    private readonly Lazy<AIResourceOrchestrator> _orchestrator;
    private readonly IAppSettingsService _settingsService;
    private readonly IResourceQueueService _resourceQueue;

    public ModuleInstallCoordinator(
        AIModelCatalog modelCatalog,
        Lazy<AIResourceService> aiResourceService,
        Lazy<AIResourceOrchestrator> orchestrator,
        IAppSettingsService settingsService,
        IResourceQueueService resourceQueue)
    {
        _modelCatalog = modelCatalog;
        _aiResourceService = aiResourceService;
        _orchestrator = orchestrator;
        _settingsService = settingsService;
        _resourceQueue = resourceQueue;
    }

    private AIResourceService AIResources => _aiResourceService.Value;
    private AIResourceOrchestrator Orchestrator => _orchestrator.Value;

    public ObservableCollection<string> GetSam2Variants()
    {
        return new ObservableCollection<string>(Enum.GetNames(typeof(SAM2Variant)));
    }

    public ObservableCollection<string> GetDownloadableLlamaVariants()
    {
        return new ObservableCollection<string>(
            _modelCatalog.GetDownloadableLlamaModelPresets()
                .AsValueEnumerable()
                .Select(p => p.Id)
                .ToList());
    }

    public bool IsAICoreInstalled() => Orchestrator.IsAICoreReady();

    public bool IsSam2Installed(SAM2Variant variant) => Orchestrator.IsSAM2Ready(variant);

    // Reports the whole language set, not just the current source language: auto-detect compares recognisers
    // against each other, so a green tick earned by having only Chinese installed would be a lie about what
    // OCR can read.
    public bool IsOcrInstalled() => Orchestrator.IsAllOcrReady();

    public bool IsLlamaInstalled(string modelId) => Orchestrator.IsLlamaPresetInstalled(modelId);

    public string LastErrorMessage => AIResources.LastErrorMessage;

    public double DownloadProgress => AIResources.DownloadProgress;

    public ArtifactDownloadStage DownloadStage => AIResources.DownloadStage;

    public IObservable<ResourceQueueSnapshot> ObserveStatus(string type)
    {
        return _resourceQueue.Observe(type);
    }

    public IObservable<(double Progress, ArtifactDownloadStage Stage)> ObserveDownloadProgress()
    {
        return AIResources.WhenAnyValue(
            x => x.DownloadProgress,
            x => x.DownloadStage,
            (progress, stage) => (progress, stage));
    }

    public async Task InstallAsync(string type, string? llamaModelId = null, CancellationToken cancellationToken = default)
    {
        var task = type switch
        {
            "AICore" => _resourceQueue.EnqueueAsync(
                "AICore",
                (ct, progress) => ExecuteWithProgressAsync(token => Orchestrator.EnsureAICoreAsync(token), ct, progress)),
            "SAM2" => _resourceQueue.EnqueueAsync(
                "SAM2",
                (ct, progress) => ExecuteWithProgressAsync(
                    token => Orchestrator.EnsureSAM2Async(_settingsService.Settings.SelectedSAM2Variant, token),
                    ct,
                    progress)),
            "OCR" => _resourceQueue.EnqueueAsync(
                "OCR",
                (ct, progress) => ExecuteWithProgressAsync(token => Orchestrator.EnsureAllOcrAsync(token), ct, progress)),
            "LlamaModels" => _resourceQueue.EnqueueAsync(
                "LlamaModels",
                (ct, progress) => ExecuteWithProgressAsync(
                    token => Orchestrator.EnsureLlamaModelAsync(llamaModelId ?? string.Empty, token),
                    ct,
                    progress)),
            _ => Task.FromResult(new ResourceQueueResult(type, ResourceQueueStatus.Failed, "Unknown module type."))
        };

        await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Cancel(string type)
    {
        _resourceQueue.Cancel(type);
    }

    private async Task<bool> ExecuteWithProgressAsync(
        Func<CancellationToken, Task<bool>> operation,
        CancellationToken cancellationToken,
        IProgress<double> progress)
    {
        using var subscription = AIResources
            .WhenAnyValue(x => x.DownloadProgress)
            .Subscribe(progress.Report);
        return await operation(cancellationToken).ConfigureAwait(false);
    }

    public void Remove(string type, string? llamaModelId = null)
    {
        switch (type)
        {
            case "AICore":
                Orchestrator.RemoveAICoreResources();
                break;
            case "SAM2":
                Orchestrator.RemoveSAM2Resources(_settingsService.Settings.SelectedSAM2Variant);
                break;
            case "OCR":
                Orchestrator.RemoveOCRResources();
                break;
            case "LlamaModels":
                if (!string.IsNullOrWhiteSpace(llamaModelId))
                {
                    Orchestrator.RemoveLlamaModelPreset(llamaModelId);
                }
                break;
        }
    }
}
