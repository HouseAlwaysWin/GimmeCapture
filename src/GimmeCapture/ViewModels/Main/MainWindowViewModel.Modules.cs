using ReactiveUI;
using System;
using System.Threading.Tasks;
using System.Reactive.Linq;
using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Linq;
using GimmeCapture.Models;
using GimmeCapture.Services.Core;

namespace GimmeCapture.ViewModels.Main;

public partial class MainWindowViewModel
{
    private bool _modulesInitialized;

    public void EnsureModulesInitialized()
    {
        if (_modulesInitialized)
        {
            return;
        }

        InitializeModules();
        _modulesInitialized = true;
    }

    private void InitializeModules()
    {
        Modules.Clear();
        
        // ONNX Runtime & U2Net Module (Renamed from AI Core)
        var aiCore = new ModuleItem("ONNX Runtime & U2Net", "ModuleAICoreDescription")
        {
            IsInstalled = AIResourceService.IsAICoreReady(),
            InstallCommand = ReactiveCommand.CreateFromTask(() => InstallModuleAsync("AICore")),
            CancelCommand = ReactiveCommand.CreateFromTask(() => CancelModuleAsync("AICore")),
            RemoveCommand = ReactiveCommand.CreateFromTask(() => RemoveModuleAsync("AICore"))
        };
        
        ResourceQueue.ObserveStatus("AICore")
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(status => 
            {
                aiCore.IsPending = status == QueueItemStatus.Pending;
                aiCore.IsProcessing = status == QueueItemStatus.Downloading;
                aiCore.HasError = status == QueueItemStatus.Failed;
                if (status == QueueItemStatus.Failed) aiCore.ErrorMessage = AIResourceService.LastErrorMessage;
                if (status == QueueItemStatus.Completed) aiCore.IsInstalled = AIResourceService.IsAICoreReady();
            });

        // SAM2 Model Module
        var sam2 = new ModuleItem("SAM2 Model", "ModuleSAM2Description")
        {
            HasVariants = true,
            Variants = new ObservableCollection<string>(Enum.GetNames(typeof(SAM2Variant))),
            SelectedVariant = _settingsService.Settings.SelectedSAM2Variant.ToString(),

            IsInstalled = AIResourceService.IsSAM2Ready(_settingsService.Settings.SelectedSAM2Variant),
            InstallCommand = ReactiveCommand.CreateFromTask(() => InstallModuleAsync("SAM2")),
            CancelCommand = ReactiveCommand.CreateFromTask(() => CancelModuleAsync("SAM2")),
            RemoveCommand = ReactiveCommand.CreateFromTask(() => RemoveModuleAsync("SAM2"))
        };

        sam2.WhenAnyValue(x => x.SelectedVariant)
            .Subscribe(async v => 
            {
                if (!_isDataLoading && Enum.TryParse<SAM2Variant>(v, out var variant))
                {
                    _settingsService.Settings.SelectedSAM2Variant = variant;
                    await SaveSettingsAsync(); 
                    sam2.IsInstalled = AIResourceService.IsSAM2Ready(variant);
                }
            });

        // PaddleOCR v5 Module
        var ocr = new ModuleItem("PaddleOCR v5", "ModuleOCRDescription")
        {
            IsInstalled = AIResourceService.IsOCRReady(),
            InstallCommand = ReactiveCommand.CreateFromTask(() => InstallModuleAsync("OCR")),
            CancelCommand = ReactiveCommand.CreateFromTask(() => CancelModuleAsync("OCR")),
            RemoveCommand = ReactiveCommand.CreateFromTask(() => RemoveModuleAsync("OCR"))
        };

        ResourceQueue.ObserveStatus("OCR")
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(status => 
            {
                ocr.IsPending = status == QueueItemStatus.Pending;
                ocr.IsProcessing = status == QueueItemStatus.Downloading;
                ocr.HasError = status == QueueItemStatus.Failed;
                if (status == QueueItemStatus.Failed) ocr.ErrorMessage = AIResourceService.LastErrorMessage;
                if (status == QueueItemStatus.Completed) ocr.IsInstalled = AIResourceService.IsOCRReady();
            });

        ResourceQueue.ObserveStatus("SAM2")
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(status => 
            {
                sam2.IsPending = status == QueueItemStatus.Pending;
                sam2.IsProcessing = status == QueueItemStatus.Downloading;
                sam2.HasError = status == QueueItemStatus.Failed;
                if (status == QueueItemStatus.Failed) sam2.ErrorMessage = AIResourceService.LastErrorMessage;
                if (status == QueueItemStatus.Completed) sam2.IsInstalled = AIResourceService.IsSAM2Ready(_settingsService.Settings.SelectedSAM2Variant);
            });

        // Llama model module (preset GGUF downloads)
        var llama = new ModuleItem("Llama Models", "ModuleLlamaModelsDescription")
        {
            HasVariants = true,
            Variants = new ObservableCollection<string>(
                AIResourceService.GetDownloadableLlamaModelPresets().AsValueEnumerable().Select(p => p.Id).ToList()),
            SelectedVariant = _settingsService.Settings.LlamaModelId,
            IsInstalled = AIResourceService.IsLlamaPresetInstalled(_settingsService.Settings.LlamaModelId),
            InstallCommand = ReactiveCommand.CreateFromTask(() => InstallModuleAsync("LlamaModels")),
            CancelCommand = ReactiveCommand.CreateFromTask(() => CancelModuleAsync("LlamaModels")),
            RemoveCommand = ReactiveCommand.CreateFromTask(() => RemoveModuleAsync("LlamaModels"))
        };

        if (llama.Variants != null && llama.Variants.Count > 0 && string.IsNullOrWhiteSpace(llama.SelectedVariant))
        {
            llama.SelectedVariant = llama.Variants[0];
        }

        llama.WhenAnyValue(x => x.SelectedVariant)
            .Subscribe(async modelId =>
            {
                if (_isDataLoading || string.IsNullOrWhiteSpace(modelId)) return;
                _settingsService.Settings.LlamaModelId = modelId;
                await SaveSettingsAsync();
                llama.IsInstalled = AIResourceService.IsLlamaPresetInstalled(modelId);
            });

        AIResourceService.WhenAnyValue(x => x.DownloadProgress)
            .Subscribe(p => {
                if (aiCore.IsProcessing) aiCore.Progress = p;
                if (sam2.IsProcessing) sam2.Progress = p;
                if (ocr.IsProcessing) ocr.Progress = p;
                if (llama.IsProcessing) llama.Progress = p;
            });

        aiCore.UpdateDescription();
        sam2.UpdateDescription();
        ocr.UpdateDescription();
        llama.UpdateDescription();

        Modules.Add(aiCore);
        Modules.Add(sam2);
        Modules.Add(ocr);
        Modules.Add(llama);

        ResourceQueue.ObserveStatus("LlamaModels")
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(status =>
            {
                llama.IsPending = status == QueueItemStatus.Pending;
                llama.IsProcessing = status == QueueItemStatus.Downloading;
                llama.HasError = status == QueueItemStatus.Failed;
                if (status == QueueItemStatus.Failed) llama.ErrorMessage = AIResourceService.LastErrorMessage;
                if (status == QueueItemStatus.Completed) llama.IsInstalled = AIResourceService.IsLlamaPresetInstalled(llama.SelectedVariant);
            });
    }

    public async Task InstallModuleAsync(string type)
    {
        foreach (var m in Modules)
        {
            if ((m.Name == "ONNX Runtime & U2Net" && type == "AICore") ||
                (m.Name == "SAM2 Model" && type == "SAM2") ||
                (m.Name == "PaddleOCR v5" && type == "OCR") ||
                (m.Name == "Llama Models" && type == "LlamaModels"))
            {
                m.HasError = false;
                m.ErrorMessage = "";
            }
        }

        if (type == "AICore")
        {
            await ResourceQueue.EnqueueAsync("AICore", (ct) => AIResourceService.EnsureAICoreAsync(ct));
        }
        else if (type == "SAM2")
        {
             var variant = _settingsService.Settings.SelectedSAM2Variant;
             await ResourceQueue.EnqueueAsync("SAM2", (ct) => AIResourceService.EnsureSAM2Async(variant, ct));
        }
        else if (type == "OCR")
        {
            await ResourceQueue.EnqueueAsync("OCR", (ct) => AIResourceService.EnsureOCRAsync(ct));
        }
        else if (type == "LlamaModels")
        {
            var selectedId = Modules.AsValueEnumerable()
                .FirstOrDefault(m => m.Name == "Llama Models")?.SelectedVariant;
            if (string.IsNullOrWhiteSpace(selectedId))
            {
                selectedId = _settingsService.Settings.LlamaModelId;
            }
            await ResourceQueue.EnqueueAsync("LlamaModels", (ct) => AIResourceService.EnsureLlamaModelAsync(selectedId ?? string.Empty, ct));
        }
    }

    private async Task CancelModuleAsync(string type)
    {
        if (ConfirmAction != null)
        {
            var result = await ConfirmAction(
                LocalizationService.Instance["UpdateCheckTitle"], 
                LocalizationService.Instance["ConfirmCancelDownload"],
                false);

            if (result)
            {
                ResourceQueue.Cancel(type);
            }
        }
    }

    private async Task RemoveModuleAsync(string type)
    {
        try 
        {
            var result = await (ConfirmAction?.Invoke(
                LocalizationService.Instance["TabModules"], 
                LocalizationService.Instance["ConfirmRemoveModule"],
                false) ?? Task.FromResult(false));

            if (!result) return;

            if (type == "AICore")
            {
                AIResourceService.RemoveAICoreResources(); 
            }
            else if (type == "SAM2")
            {
                 AIResourceService.RemoveSAM2Resources(_settingsService.Settings.SelectedSAM2Variant);
            }
            else if (type == "OCR")
            {
                AIResourceService.RemoveOCRResources();
            }
            else if (type == "LlamaModels")
            {
                var selectedId = Modules.AsValueEnumerable()
                    .FirstOrDefault(m => m.Name == "Llama Models")?.SelectedVariant;
                if (!string.IsNullOrWhiteSpace(selectedId))
                {
                    AIResourceService.RemoveLlamaModelPreset(selectedId);
                }
            }
            
            foreach (var m in Modules)
            {
                if (m.Name == "ONNX Runtime & U2Net") m.IsInstalled = AIResourceService.IsAICoreReady();
                if (m.Name == "SAM2 Model") m.IsInstalled = AIResourceService.IsSAM2Ready(_settingsService.Settings.SelectedSAM2Variant);
                if (m.Name == "PaddleOCR v5") m.IsInstalled = AIResourceService.IsOCRReady();
                if (m.Name == "Llama Models") m.IsInstalled = AIResourceService.IsLlamaPresetInstalled(m.SelectedVariant);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to remove module {type}: {ex}");
        }
    }

    public class ModuleItem : ReactiveObject
    {
        public string Name { get; }
        public string DescriptionKey { get; }
        
        private string _description = "";
        public string Description
        {
            get => _description;
            set => this.RaiseAndSetIfChanged(ref _description, value);
        }
        
        private string _statusText = "";
        public string StatusText
        {
            get => _statusText;
            set => this.RaiseAndSetIfChanged(ref _statusText, value);
        }

        private bool _isInstalled;
        public bool IsInstalled
        {
            get => _isInstalled;
            set 
            {
                this.RaiseAndSetIfChanged(ref _isInstalled, value);
                UpdateDescription();
            }
        }

        private bool _isProcessing;
        public bool IsProcessing
        {
            get => _isProcessing;
            set 
            {
                this.RaiseAndSetIfChanged(ref _isProcessing, value);
                UpdateDescription();
            }
        }

        private bool _isPending;
        public bool IsPending
        {
            get => _isPending;
            set 
            {
                this.RaiseAndSetIfChanged(ref _isPending, value);
                UpdateDescription();
            }
        }

        private double _progress;
        public double Progress
        {
            get => _progress;
            set => this.RaiseAndSetIfChanged(ref _progress, value);
        }

        private bool _hasError;
        public bool HasError
        {
            get => _hasError;
            set => this.RaiseAndSetIfChanged(ref _hasError, value);
        }

        private string _errorMessage = "";
        public string ErrorMessage
        {
            get => _errorMessage;
            set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
        }

        public bool HasVariants { get; init; } = false;
        public ObservableCollection<string>? Variants { get; init; }

        private string _selectedVariant = "";
        public string SelectedVariant
        {
            get => _selectedVariant;
            set => this.RaiseAndSetIfChanged(ref _selectedVariant, value);
        }

        public ICommand InstallCommand { get; init; } = null!;
        public ICommand CancelCommand { get; set; } = null!;
        public ICommand RemoveCommand { get; init; } = null!;

        private ICommand _mainActionCommand = null!;
        public ICommand MainActionCommand
        {
            get => _mainActionCommand;
            set => this.RaiseAndSetIfChanged(ref _mainActionCommand, value);
        }

        private string _actionButtonText = "";
        public string ActionButtonText
        {
            get => _actionButtonText;
            set => this.RaiseAndSetIfChanged(ref _actionButtonText, value);
        }

        public ModuleItem(string name, string descriptionKey)
        {
            Name = name;
            DescriptionKey = descriptionKey;
            
            LocalizationService.Instance.WhenAnyValue(x => x.CurrentLanguage)
                .Subscribe(_ => UpdateDescription());
            UpdateDescription();
        }
        
        public void UpdateDescription()
        {
            Description = LocalizationService.Instance[DescriptionKey];

            if (IsPending)
            {
                StatusText = LocalizationService.Instance["Pending"];
            }
            else if (IsProcessing)
            {
                StatusText = LocalizationService.Instance["ComponentDownloadingProgress"];
            }
            else
            {
                StatusText = IsInstalled 
                    ? LocalizationService.Instance["Installed"] 
                    : LocalizationService.Instance["NotInstalled"];
            }
            
            if (IsProcessing || IsPending)
            {
                 ActionButtonText = LocalizationService.Instance["CancelDownload"];
                 MainActionCommand = CancelCommand;
            }
            else
            {
                ActionButtonText = IsInstalled 
                    ? LocalizationService.Instance["RemoveModule"] 
                    : LocalizationService.Instance["InstallModule"];
                MainActionCommand = IsInstalled ? RemoveCommand : InstallCommand;
            }
        }
    }
}
