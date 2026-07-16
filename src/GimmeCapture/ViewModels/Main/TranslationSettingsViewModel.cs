using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using GimmeCapture.Models;
using ReactiveUI;

namespace GimmeCapture.ViewModels.Main;

// Translation settings section (OCR source language, target language, engine + the Llama translation-model
// catalog / picker). The language/engine scalars are settings-backed via the MainWindowViewModel bridge
// (which keeps forwarder properties so the many Snip / toolbar consumers stay untouched); the Llama catalog
// keeps its intricate Id<->Option<->Index sync self-contained here and reaches the AI resource service /
// status text through the injected callbacks (AIResourceService is a lazy, side-effecting getter, so it is
// passed as a Func to preserve that laziness).
public class TranslationSettingsViewModel : ViewModelBase
{
    private readonly Func<AIResourceService> _aiResource;
    private readonly Action<string> _setStatus;

    public TranslationSettingsViewModel(Func<AIResourceService> aiResource, Action<string> setStatus)
    {
        _aiResource = aiResource;
        _setStatus = setStatus;
    }

    public List<TranslationLanguage> AvailableTranslationLanguages =>
        Enum.GetValues<TranslationLanguage>().AsValueEnumerable().ToList();

    public List<OCRLanguage> AvailableOCRLanguages =>
        Enum.GetValues<OCRLanguage>().AsValueEnumerable().ToList();

    public List<TranslationEngine> AvailableTranslationEngines { get; } = new() { TranslationEngine.LlamaSharp };

    private OCRLanguage _sourceLanguage;
    public OCRLanguage SourceLanguage
    {
        get => _sourceLanguage;
        set => this.RaiseAndSetIfChanged(ref _sourceLanguage, value);
    }

    private TranslationLanguage _targetLanguage;
    public TranslationLanguage TargetLanguage
    {
        get => _targetLanguage;
        set => this.RaiseAndSetIfChanged(ref _targetLanguage, value);
    }

    private TranslationEngine _selectedTranslationEngine;
    public TranslationEngine SelectedTranslationEngine
    {
        get => _selectedTranslationEngine;
        set
        {
            if (_selectedTranslationEngine != value)
            {
                this.RaiseAndSetIfChanged(ref _selectedTranslationEngine, value);
                this.RaisePropertyChanged(nameof(IsLlamaVisible));

                // Notify language lists changed
                this.RaisePropertyChanged(nameof(AvailableOCRLanguages));
                this.RaisePropertyChanged(nameof(AvailableTranslationLanguages));
            }
        }
    }

    public bool IsLlamaVisible => SelectedTranslationEngine == TranslationEngine.LlamaSharp;

    // --- Llama translation-model catalog / picker (moved from MainWindowViewModel) ---
    // The four scalars below are settings-backed; the immediate settings-model mirror + auto-save are wired by
    // the MainWindowViewModel.Translation bridge (guarded by _isDataLoading there), exactly as before.

    private string _llamaModelId = "translategemma-4b-it";
    public string LlamaModelId
    {
        get => _llamaModelId;
        set
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            this.RaiseAndSetIfChanged(ref _llamaModelId, value);
            SyncSelectedLlamaModelSelection();
            this.RaisePropertyChanged(nameof(SelectedLlamaModelDisplayName));
        }
    }

    private string _llamaCustomModelPath = string.Empty;
    public string LlamaCustomModelPath
    {
        get => _llamaCustomModelPath;
        set => this.RaiseAndSetIfChanged(ref _llamaCustomModelPath, value);
    }

    private int _llamaContextSize = 2048;
    public int LlamaContextSize
    {
        get => _llamaContextSize;
        set => this.RaiseAndSetIfChanged(ref _llamaContextSize, IntParameterValidator.ClampLlamaContextSize(value));
    }

    private int _llamaGpuLayers;
    public int LlamaGpuLayers
    {
        get => _llamaGpuLayers;
        set => this.RaiseAndSetIfChanged(ref _llamaGpuLayers, IntParameterValidator.ClampGpuLayers(value));
    }

    public sealed class LlamaModelOption
    {
        public required string Id { get; init; }
        public required string DisplayName { get; init; }

        public override string ToString() => DisplayName;
    }

    private ObservableCollection<LlamaModelOption> _availableLlamaModels = new(
    [
        ToLlamaModelOption("translategemma-4b-it"),
        ToLlamaModelOption("gemma-3-4b-it-q4"),
        ToLlamaModelOption("translategemma-12b-it")
    ]);
    public ObservableCollection<LlamaModelOption> AvailableLlamaModels
    {
        get => _availableLlamaModels;
        private set
        {
            this.RaiseAndSetIfChanged(ref _availableLlamaModels, value);
            this.RaisePropertyChanged(nameof(SelectedLlamaModelDisplayName));
        }
    }

    private LlamaModelOption? _selectedLlamaModelOption;
    public LlamaModelOption? SelectedLlamaModelOption
    {
        get => _selectedLlamaModelOption;
        set
        {
            if (ReferenceEquals(_selectedLlamaModelOption, value))
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _selectedLlamaModelOption, value);
            SyncSelectedLlamaModelIndexCore(value);
            this.RaisePropertyChanged(nameof(SelectedLlamaModelDisplayName));

            if (value != null && !string.Equals(LlamaModelId, value.Id, StringComparison.Ordinal))
            {
                LlamaModelId = value.Id;
            }
        }
    }

    private bool _isLlamaModelPickerOpen;
    public bool IsLlamaModelPickerOpen
    {
        get => _isLlamaModelPickerOpen;
        set => this.RaiseAndSetIfChanged(ref _isLlamaModelPickerOpen, value);
    }

    public string SelectedLlamaModelDisplayName =>
        SelectedLlamaModelOption?.DisplayName
        ?? ToLlamaModelOption(LlamaModelId).DisplayName;

    private int _selectedLlamaModelIndex = -1;
    public int SelectedLlamaModelIndex
    {
        get => _selectedLlamaModelIndex;
        set
        {
            if (_selectedLlamaModelIndex == value)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _selectedLlamaModelIndex, value);

            LlamaModelOption? nextOption = value >= 0 && value < AvailableLlamaModels.Count
                ? AvailableLlamaModels[value]
                : null;

            if (!ReferenceEquals(_selectedLlamaModelOption, nextOption))
            {
                _selectedLlamaModelOption = nextOption;
                this.RaisePropertyChanged(nameof(SelectedLlamaModelOption));
            }

            if (nextOption != null && !string.Equals(LlamaModelId, nextOption.Id, StringComparison.Ordinal))
            {
                LlamaModelId = nextOption.Id;
            }
        }
    }

    public bool HasDownloadedLlamaModels =>
        _aiResource().GetInstalledLlamaModelPresets().Count > 0 || _aiResource().IsLlamaModelReady();
    public bool NoDownloadedLlamaModels => !HasDownloadedLlamaModels;

    public void RefreshLlamaModelCatalog()
    {
        var presets = _aiResource().GetDownloadableLlamaModelPresets();
        var nextModels = new List<LlamaModelOption>();
        foreach (var preset in presets)
        {
            nextModels.Add(ToLlamaModelOption(preset.Id));
        }

        if (nextModels.Count == 0)
        {
            nextModels.Add(ToLlamaModelOption("translategemma-4b-it"));
            nextModels.Add(ToLlamaModelOption("gemma-3-4b-it-q4"));
            nextModels.Add(ToLlamaModelOption("translategemma-12b-it"));
        }

        string nextSelectedModelId = LlamaModelId;
        if (nextModels.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(nextSelectedModelId)
                || !nextModels.AsValueEnumerable().Any(static option => !string.IsNullOrWhiteSpace(option.Id))
                || !nextModels.AsValueEnumerable().Any(option => string.Equals(option.Id, nextSelectedModelId, StringComparison.Ordinal)))
            {
                nextSelectedModelId = nextModels[0].Id;
            }
        }
        else if (!_aiResource().IsLlamaModelReady())
        {
            _setStatus(LocalizationService.Instance["StatusLlamaModelNotReady"]);
        }

        ReplaceLlamaModelOptions(nextModels);

        if (nextModels.Count > 0 && !string.Equals(LlamaModelId, nextSelectedModelId, StringComparison.Ordinal))
        {
            LlamaModelId = nextSelectedModelId;
        }
        else
        {
            SyncSelectedLlamaModelSelection();
        }

        this.RaisePropertyChanged(nameof(HasDownloadedLlamaModels));
        this.RaisePropertyChanged(nameof(NoDownloadedLlamaModels));
    }

    private void ReplaceLlamaModelOptions(IEnumerable<LlamaModelOption> nextModels)
    {
        var collection = new ObservableCollection<LlamaModelOption>(nextModels);
        AvailableLlamaModels = collection;
    }

    private void SyncSelectedLlamaModelSelection()
    {
        var nextOption = AvailableLlamaModels.AsValueEnumerable()
            .FirstOrDefault(option => string.Equals(option.Id, LlamaModelId, StringComparison.Ordinal));

        if (!ReferenceEquals(_selectedLlamaModelOption, nextOption))
        {
            _selectedLlamaModelOption = nextOption;
            this.RaisePropertyChanged(nameof(SelectedLlamaModelOption));
            this.RaisePropertyChanged(nameof(SelectedLlamaModelDisplayName));
        }

        SyncSelectedLlamaModelIndexCore(nextOption);
    }

    private void SyncSelectedLlamaModelIndexCore(LlamaModelOption? option)
    {
        int nextIndex = ModelOptionSelector.FindIndexById(option, AvailableLlamaModels, m => m.Id);

        if (_selectedLlamaModelIndex != nextIndex)
        {
            _selectedLlamaModelIndex = nextIndex;
            this.RaisePropertyChanged(nameof(SelectedLlamaModelIndex));
        }
    }

    private static LlamaModelOption ToLlamaModelOption(string modelId) =>
        new() { Id = modelId, DisplayName = LlamaModelDisplayNames.Get(modelId) };
}
