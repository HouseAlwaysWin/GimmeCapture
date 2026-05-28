using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Infrastructure;
using ReactiveUI;

namespace GimmeCapture.Services.Core.AI;

public sealed class ModuleInstallCoordinator
{
    private readonly AIModelCatalog _modelCatalog;
    private readonly Lazy<AIResourceService> _aiResourceService;
    private readonly Lazy<AIResourceOrchestrator> _orchestrator;
    private readonly AppSettingsService _settingsService;
    private readonly ResourceQueueService _resourceQueue;

    public ModuleInstallCoordinator(
        AIModelCatalog modelCatalog,
        Lazy<AIResourceService> aiResourceService,
        Lazy<AIResourceOrchestrator> orchestrator,
        AppSettingsService settingsService,
        ResourceQueueService resourceQueue)
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

    public bool IsOcrInstalled() => Orchestrator.IsOCRReady();

    public bool IsLlamaInstalled(string modelId) => Orchestrator.IsLlamaPresetInstalled(modelId);

    public string LastErrorMessage => AIResources.LastErrorMessage;

    public double DownloadProgress => AIResources.DownloadProgress;

    public IObservable<QueueItemStatus> ObserveStatus(string type)
    {
        return _resourceQueue.ObserveStatus(type);
    }

    public IObservable<double> ObserveDownloadProgress()
    {
        return AIResources.WhenAnyValue(x => x.DownloadProgress);
    }

    public Task InstallAsync(string type, string? llamaModelId = null, CancellationToken cancellationToken = default)
    {
        return type switch
        {
            "AICore" => _resourceQueue.EnqueueAsync("AICore", ct => Orchestrator.EnsureAICoreAsync(ct)),
            "SAM2" => _resourceQueue.EnqueueAsync("SAM2", ct => Orchestrator.EnsureSAM2Async(_settingsService.Settings.SelectedSAM2Variant, ct)),
            "OCR" => _resourceQueue.EnqueueAsync("OCR", ct => Orchestrator.EnsureOCRAsync(ct)),
            "LlamaModels" => _resourceQueue.EnqueueAsync(
                "LlamaModels",
                ct => Orchestrator.EnsureLlamaModelAsync(llamaModelId ?? string.Empty, ct)),
            _ => Task.CompletedTask
        };
    }

    public void Cancel(string type)
    {
        _resourceQueue.Cancel(type);
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
