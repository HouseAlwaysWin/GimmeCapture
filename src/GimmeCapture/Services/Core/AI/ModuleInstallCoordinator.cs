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
    private readonly Lazy<AIResourceService> _aiResourceService;
    private readonly AppSettingsService _settingsService;
    private readonly ResourceQueueService _resourceQueue;

    public ModuleInstallCoordinator(
        Lazy<AIResourceService> aiResourceService,
        AppSettingsService settingsService,
        ResourceQueueService resourceQueue)
    {
        _aiResourceService = aiResourceService;
        _settingsService = settingsService;
        _resourceQueue = resourceQueue;
    }

    private AIResourceService AIResources => _aiResourceService.Value;

    public ObservableCollection<string> GetSam2Variants()
    {
        return new ObservableCollection<string>(Enum.GetNames(typeof(SAM2Variant)));
    }

    public ObservableCollection<string> GetDownloadableLlamaVariants()
    {
        return new ObservableCollection<string>(
            AIResources.GetDownloadableLlamaModelPresets()
                .AsValueEnumerable()
                .Select(p => p.Id)
                .ToList());
    }

    public bool IsAICoreInstalled() => AIResources.IsAICoreReady();

    public bool IsSam2Installed(SAM2Variant variant) => AIResources.IsSAM2Ready(variant);

    public bool IsOcrInstalled() => AIResources.IsOCRReady();

    public bool IsLlamaInstalled(string modelId) => AIResources.IsLlamaPresetInstalled(modelId);

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
            "AICore" => _resourceQueue.EnqueueAsync("AICore", ct => AIResources.EnsureAICoreAsync(ct)),
            "SAM2" => _resourceQueue.EnqueueAsync("SAM2", ct => AIResources.EnsureSAM2Async(_settingsService.Settings.SelectedSAM2Variant, ct)),
            "OCR" => _resourceQueue.EnqueueAsync("OCR", ct => AIResources.EnsureOCRAsync(ct)),
            "LlamaModels" => _resourceQueue.EnqueueAsync(
                "LlamaModels",
                ct => AIResources.EnsureLlamaModelAsync(llamaModelId ?? string.Empty, ct)),
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
                AIResources.RemoveAICoreResources();
                break;
            case "SAM2":
                AIResources.RemoveSAM2Resources(_settingsService.Settings.SelectedSAM2Variant);
                break;
            case "OCR":
                AIResources.RemoveOCRResources();
                break;
            case "LlamaModels":
                if (!string.IsNullOrWhiteSpace(llamaModelId))
                {
                    AIResources.RemoveLlamaModelPreset(llamaModelId);
                }
                break;
        }
    }
}
