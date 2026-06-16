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
            IsInstalled = _moduleInstallCoordinator.IsAICoreInstalled(),
            InstallCommand = ReactiveCommand.CreateFromTask(() => InstallModuleAsync("AICore")),
            CancelCommand = ReactiveCommand.CreateFromTask(() => CancelModuleAsync("AICore")),
            RemoveCommand = ReactiveCommand.CreateFromTask(() => RemoveModuleAsync("AICore"))
        };
        
        _moduleInstallCoordinator.ObserveStatus("AICore")
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(snapshot =>
            {
                aiCore.QueueStatus = snapshot.Status;
                aiCore.IsPending = snapshot.Status == ResourceQueueStatus.Pending;
                aiCore.IsProcessing = snapshot.Status == ResourceQueueStatus.Running;
                aiCore.HasError = snapshot.Status == ResourceQueueStatus.Failed;
                aiCore.Progress = snapshot.Progress;
                if (snapshot.Status == ResourceQueueStatus.Failed) aiCore.ErrorMessage = snapshot.ErrorMessage ?? _moduleInstallCoordinator.LastErrorMessage;
                if (snapshot.Status == ResourceQueueStatus.Completed) aiCore.IsInstalled = _moduleInstallCoordinator.IsAICoreInstalled();
            });

        // SAM2 Model Module
        var sam2 = new ModuleItem("SAM2 Model", "ModuleSAM2Description")
        {
            HasVariants = true,
            Variants = _moduleInstallCoordinator.GetSam2Variants(),
            SelectedVariant = _settingsService.Settings.SelectedSAM2Variant.ToString(),

            IsInstalled = _moduleInstallCoordinator.IsSam2Installed(_settingsService.Settings.SelectedSAM2Variant),
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
                    sam2.IsInstalled = _moduleInstallCoordinator.IsSam2Installed(variant);
                }
            });

        // PaddleOCR v5 Module
        var ocr = new ModuleItem("PaddleOCR v5", "ModuleOCRDescription")
        {
            IsInstalled = _moduleInstallCoordinator.IsOcrInstalled(),
            InstallCommand = ReactiveCommand.CreateFromTask(() => InstallModuleAsync("OCR")),
            CancelCommand = ReactiveCommand.CreateFromTask(() => CancelModuleAsync("OCR")),
            RemoveCommand = ReactiveCommand.CreateFromTask(() => RemoveModuleAsync("OCR"))
        };

        _moduleInstallCoordinator.ObserveStatus("OCR")
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(snapshot =>
            {
                ocr.QueueStatus = snapshot.Status;
                ocr.IsPending = snapshot.Status == ResourceQueueStatus.Pending;
                ocr.IsProcessing = snapshot.Status == ResourceQueueStatus.Running;
                ocr.HasError = snapshot.Status == ResourceQueueStatus.Failed;
                ocr.Progress = snapshot.Progress;
                if (snapshot.Status == ResourceQueueStatus.Failed) ocr.ErrorMessage = snapshot.ErrorMessage ?? _moduleInstallCoordinator.LastErrorMessage;
                if (snapshot.Status == ResourceQueueStatus.Completed) ocr.IsInstalled = _moduleInstallCoordinator.IsOcrInstalled();
            });

        _moduleInstallCoordinator.ObserveStatus("SAM2")
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(snapshot =>
            {
                sam2.QueueStatus = snapshot.Status;
                sam2.IsPending = snapshot.Status == ResourceQueueStatus.Pending;
                sam2.IsProcessing = snapshot.Status == ResourceQueueStatus.Running;
                sam2.HasError = snapshot.Status == ResourceQueueStatus.Failed;
                sam2.Progress = snapshot.Progress;
                if (snapshot.Status == ResourceQueueStatus.Failed) sam2.ErrorMessage = snapshot.ErrorMessage ?? _moduleInstallCoordinator.LastErrorMessage;
                if (snapshot.Status == ResourceQueueStatus.Completed) sam2.IsInstalled = _moduleInstallCoordinator.IsSam2Installed(_settingsService.Settings.SelectedSAM2Variant);
            });

        // Llama model module (preset GGUF downloads)
        var llama = new ModuleItem("Llama Models", "ModuleLlamaModelsDescription")
        {
            HasVariants = true,
            Variants = _moduleInstallCoordinator.GetDownloadableLlamaVariants(),
            SelectedVariant = _settingsService.Settings.LlamaModelId,
            IsInstalled = _moduleInstallCoordinator.IsLlamaInstalled(_settingsService.Settings.LlamaModelId),
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
                llama.IsInstalled = _moduleInstallCoordinator.IsLlamaInstalled(modelId);
            });

        _moduleInstallCoordinator.ObserveDownloadProgress()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(state => {
                if (aiCore.IsProcessing) aiCore.SetDownloadProgress(state.Progress, state.Stage);
                if (sam2.IsProcessing) sam2.SetDownloadProgress(state.Progress, state.Stage);
                if (ocr.IsProcessing) ocr.SetDownloadProgress(state.Progress, state.Stage);
                if (llama.IsProcessing) llama.SetDownloadProgress(state.Progress, state.Stage);
            });

        aiCore.UpdateDescription();
        sam2.UpdateDescription();
        ocr.UpdateDescription();
        llama.UpdateDescription();

        Modules.Add(aiCore);
        Modules.Add(sam2);
        Modules.Add(ocr);
        Modules.Add(llama);

        _moduleInstallCoordinator.ObserveStatus("LlamaModels")
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(snapshot =>
            {
                llama.QueueStatus = snapshot.Status;
                llama.IsPending = snapshot.Status == ResourceQueueStatus.Pending;
                llama.IsProcessing = snapshot.Status == ResourceQueueStatus.Running;
                llama.HasError = snapshot.Status == ResourceQueueStatus.Failed;
                llama.Progress = snapshot.Progress;
                if (snapshot.Status == ResourceQueueStatus.Failed) llama.ErrorMessage = snapshot.ErrorMessage ?? _moduleInstallCoordinator.LastErrorMessage;
                if (snapshot.Status == ResourceQueueStatus.Completed)
                {
                    if (!string.IsNullOrWhiteSpace(llama.SelectedVariant))
                    {
                        _settingsService.Settings.LlamaModelId = llama.SelectedVariant;
                        LlamaModelId = llama.SelectedVariant;
                    }

                    RefreshLlamaModelCatalog();
                    llama.IsInstalled = _moduleInstallCoordinator.IsLlamaInstalled(llama.SelectedVariant);
                }
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

        var selectedId = Modules.AsValueEnumerable()
            .FirstOrDefault(m => m.Name == "Llama Models")?.SelectedVariant;
        if (string.IsNullOrWhiteSpace(selectedId))
        {
            selectedId = _settingsService.Settings.LlamaModelId;
        }

        await _moduleInstallCoordinator.InstallAsync(type, selectedId);
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
                _moduleInstallCoordinator.Cancel(type);
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

            var selectedId = Modules.AsValueEnumerable()
                .FirstOrDefault(m => m.Name == "Llama Models")?.SelectedVariant;
            _moduleInstallCoordinator.Remove(type, selectedId);
            
            foreach (var m in Modules)
            {
                if (m.Name == "ONNX Runtime & U2Net") m.IsInstalled = _moduleInstallCoordinator.IsAICoreInstalled();
                if (m.Name == "SAM2 Model") m.IsInstalled = _moduleInstallCoordinator.IsSam2Installed(_settingsService.Settings.SelectedSAM2Variant);
                if (m.Name == "PaddleOCR v5") m.IsInstalled = _moduleInstallCoordinator.IsOcrInstalled();
                if (m.Name == "Llama Models") m.IsInstalled = _moduleInstallCoordinator.IsLlamaInstalled(m.SelectedVariant);
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

        private ArtifactDownloadStage _downloadStage;
        public ArtifactDownloadStage DownloadStage
        {
            get => _downloadStage;
            private set
            {
                this.RaiseAndSetIfChanged(ref _downloadStage, value);
                UpdateDescription();
            }
        }

        private ResourceQueueStatus? _queueStatus;
        public ResourceQueueStatus? QueueStatus
        {
            get => _queueStatus;
            set
            {
                this.RaiseAndSetIfChanged(ref _queueStatus, value);
                UpdateDescription();
            }
        }

        private bool _hasError;
        public bool HasError
        {
            get => _hasError;
            set
            {
                this.RaiseAndSetIfChanged(ref _hasError, value);
                UpdateDescription();
            }
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

        public void SetDownloadProgress(double progress, ArtifactDownloadStage stage)
        {
            Progress = progress;
            DownloadStage = stage;
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
                StatusText = LocalizationService.Instance[
                    DownloadStage == ArtifactDownloadStage.Verifying
                        ? "DownloadStageVerifying"
                        : DownloadStage == ArtifactDownloadStage.Completed
                            ? "DownloadStageCompleted"
                        : "DownloadStageDownloading"];
            }
            else if (QueueStatus == ResourceQueueStatus.Cancelled)
            {
                StatusText = LocalizationService.Instance["DownloadStageCancelled"];
            }
            else if (HasError || QueueStatus == ResourceQueueStatus.Failed)
            {
                StatusText = LocalizationService.Instance["DownloadStageFailed"];
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
                ActionButtonText = HasError
                    ? LocalizationService.Instance["RetryDownload"]
                    : IsInstalled
                        ? LocalizationService.Instance["RemoveModule"]
                        : LocalizationService.Instance["InstallModule"];
                MainActionCommand = IsInstalled ? RemoveCommand : InstallCommand;
            }
        }
    }
}
