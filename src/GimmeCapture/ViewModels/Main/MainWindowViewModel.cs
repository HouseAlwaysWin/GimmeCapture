using Avalonia;
using Avalonia.Media;
using GimmeCapture.Models;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core;
using GimmeCapture.Services.Core.Infrastructure;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Services.Translation;

namespace GimmeCapture.ViewModels.Main;

public partial class MainWindowViewModel : ViewModelBase
{
    private string _statusText = "";
    public string StatusText
    {
        get => _statusText;
        set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    private string _aiProgressText = "";
    public string AIProgressText
    {
        get => _aiProgressText;
        set => this.RaiseAndSetIfChanged(ref _aiProgressText, value);
    }

    private bool _isModified;
    public bool IsModified
    {
        get => _isModified;
        set => this.RaiseAndSetIfChanged(ref _isModified, value);
    }

    private bool _isProcessing;
    public bool IsProcessing
    {
        get => _isProcessing;
        set => this.RaiseAndSetIfChanged(ref _isProcessing, value);
    }

    private string _processingText = "";
    public string ProcessingText
    {
        get => _processingText;
        set => this.RaiseAndSetIfChanged(ref _processingText, value);
    }

    private bool _showProcessingOverlay;
    public bool ShowProcessingOverlay
    {
        get => _showProcessingOverlay;
        set => this.RaiseAndSetIfChanged(ref _showProcessingOverlay, value);
    }

    private double _progressValue;
    public double ProgressValue
    {
        get => _progressValue;
        set => this.RaiseAndSetIfChanged(ref _progressValue, value);
    }

    private bool _isIndeterminate;
    public bool IsIndeterminate
    {
        get => _isIndeterminate;
        set => this.RaiseAndSetIfChanged(ref _isIndeterminate, value);
    }

    private bool _isDataLoading = true;
    private Task? _loadTask;
    private string _currentStatusKey = "StatusReady";
    private readonly CaptureLaunchCoordinator _captureLaunchCoordinator = new();

    public void SetStatus(string key)
    {
        _currentStatusKey = key;
        StatusText = LocalizationService.Instance[key];
    }

    public Action<CaptureMode>? RequestCaptureAction { get; set; }
    public Func<int, CancellationToken, Task>? ShowCaptureCountdownAction { get; set; }
    public Action? CloseCaptureCountdownAction { get; set; }
    public Action? RequestElevatedWindowPromptAction { get; set; }
    public Action? RequestOpenModulesAction { get; set; }
    public Func<SnipWindowViewModel?>? GetActiveSnipViewModelAction { get; set; }
    public Func<Task<string?>>? PickFolderAction { get; set; }
    public Func<string, string, bool, Task<bool>>? ConfirmAction { get; set; }
    /// <summary>Two-button Yes/No confirmation (no Cancel). Returns true on Yes.</summary>
    public Func<string, string, Task<bool>>? ConfirmYesNoAction { get; set; }
    public Func<string, bool, Task<bool>>? ShowUpdateDialogAction { get; set; }

    public void CancelCaptureCountdown()
    {
        _captureLaunchCoordinator.Cancel();
    }

    public AppSettingsService AppSettingsService => _settingsService;
    /// <summary>Capture history index for saved screenshots and finalized recordings (B3 history panel).</summary>
    public CaptureHistoryService CaptureHistory { get; }
    private readonly AppSettingsService _settingsService;
    private readonly IWindowManager _windowManager;
    private readonly IThemeResourceService _themeResourceService;
    private readonly IGlobalHotkeySettingsCoordinator _globalHotkeySettingsCoordinator;
    private readonly IStartupRegistrationService _startupRegistrationService;
    private readonly ISettingsSaveCoordinator _settingsSaveCoordinator;
    private readonly IMainWindowSettingsPersistenceService _settingsPersistenceService;
    private readonly MainWindowSettingsSideEffectCoordinator _settingsSideEffectCoordinator;
    public IGlobalHotkeyService HotkeyService { get; }
    public HotkeyMappingService HotkeyMappingService { get; }
    public HotkeyRouterService HotkeyRouterService { get; }

    private readonly Lazy<FFmpegDownloaderService> _ffmpegDownloader;
    private readonly Lazy<RecordingService> _recordingService;
    private readonly Lazy<UpdateService> _updateService;
    private readonly AIModelCatalog _aiModelCatalog;
    private readonly Lazy<AIResourceService> _aiResourceService;
    private readonly Lazy<AIResourceOrchestrator> _aiResourceOrchestrator;
    private readonly Lazy<SAM2RuntimeService> _sam2RuntimeService;
    private readonly Lazy<OcrRuntimeService> _ocrRuntimeService;
    private IDisposable? _updateProcessingSubscription;
    private IDisposable? _aiProcessingSubscription;

    public FFmpegDownloaderService FfmpegDownloader => _ffmpegDownloader.Value;
    public RecordingService RecordingService => _recordingService.Value;
    public UpdateService UpdateService
    {
        get
        {
            EnsureUpdateProcessingSubscription();
            return _updateService.Value;
        }
    }

    public AIResourceService AIResourceService
    {
        get
        {
            EnsureAiProcessingSubscription();
            return _aiResourceService.Value;
        }
    }

    public SAM2RuntimeService SAM2RuntimeService => _sam2RuntimeService.Value;
    public OcrRuntimeService OcrRuntimeService => _ocrRuntimeService.Value;
    public AIPathService AIPathService { get; }
    public IResourceQueueService ResourceQueue { get; }
    private readonly ModuleInstallCoordinator _moduleInstallCoordinator;

    public ObservableCollection<ModuleItem> Modules { get; } = new();
    public string AppVersion => AppVersionInfo.CurrentVersion;

    public ReactiveCommand<CaptureMode, Unit> StartCaptureCommand { get; } = null!;
    public ReactiveCommand<Unit, Unit> ScrollingCaptureCommand { get; } = null!;
    public ReactiveCommand<Unit, Unit> SaveAndCloseCommand { get; } = null!;
    public ReactiveCommand<Unit, Unit> ResetToDefaultCommand { get; } = null!;
    public ReactiveCommand<Unit, Unit> IncreaseThicknessCommand { get; } = null!;
    public ReactiveCommand<Unit, Unit> DecreaseThicknessCommand { get; } = null!;
    public ReactiveCommand<Color, Unit> ChangeColorCommand { get; } = null!;
    public ReactiveCommand<Color, Unit> ChangeThemeColorCommand { get; } = null!;
    public ReactiveCommand<Unit, Unit> CheckUpdateCommand { get; } = null!;
    public ReactiveCommand<Unit, Unit> OpenProjectCommand { get; } = null!;
    public ReactiveCommand<Unit, Unit> MinimizeCommand { get; } = null!;
    public ReactiveCommand<Unit, Unit> MaximizeCommand { get; } = null!;
    public ReactiveCommand<Unit, Unit> CloseCommand { get; } = null!;
    public ReactiveCommand<Unit, Unit> IncreaseWingScaleCommand { get; } = null!;
    public ReactiveCommand<Unit, Unit> DecreaseWingScaleCommand { get; } = null!;
    public ReactiveCommand<Unit, Unit> IncreaseRecordFPSCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> DecreaseRecordFPSCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> IncreaseMaxRecordingSizeMBCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> DecreaseMaxRecordingSizeMBCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> IncreasePlaybackUiFpsCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> DecreasePlaybackUiFpsCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> IncreasePlaybackTimelineFpsCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> DecreasePlaybackTimelineFpsCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit>? ToggleRecordCommand { get; set; }
    public ReactiveCommand<Unit, Unit> PickAIFolderCommand { get; } = null!;
    public ReactiveCommand<Unit, Unit> RefreshReleaseCatalogCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> InstallSelectedReleaseCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> CopyDiagnosticsCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> CancelUpdateDownloadCommand { get; private set; } = null!;
    public ReactiveCommand<string, Unit> SelectLlamaModelCommand { get; private set; } = null!;

    public Color[] SettingsColors { get; } =
    {
        Color.Parse("#D4AF37"),
        Color.Parse("#E0E0E0"),
        Color.Parse("#E60012")
    };

    public MainWindowViewModel() : this(CreateDesignDependencies())
    {
    }

    public MainWindowViewModel(MainWindowViewModelDependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        TranslationMemoryDiagnostics.Log("app-startup");

        _settingsService = dependencies.SettingsService;
        CaptureHistory = new CaptureHistoryService(_settingsService);
        _windowManager = dependencies.WindowManager;
        _themeResourceService = dependencies.ThemeResourceService;
        _globalHotkeySettingsCoordinator = dependencies.GlobalHotkeySettingsCoordinator;
        _startupRegistrationService = dependencies.StartupRegistrationService;
        HotkeyService = dependencies.HotkeyService;
        _settingsSaveCoordinator = dependencies.SettingsSaveCoordinatorFactory.Create(SaveSettingsAsync);
        _settingsPersistenceService = dependencies.SettingsPersistenceService;
        _settingsSideEffectCoordinator = new MainWindowSettingsSideEffectCoordinator(
            _globalHotkeySettingsCoordinator,
            _startupRegistrationService,
            _themeResourceService,
            () => _settingsSaveCoordinator.RequestSave(),
            () => IsModified = true);
        HotkeyMappingService = dependencies.HotkeyMappingService;
        HotkeyRouterService = dependencies.HotkeyRouterService;
        _ffmpegDownloader = dependencies.FfmpegDownloader;
        _recordingService = dependencies.RecordingService;
        _updateService = dependencies.UpdateService;
        _aiModelCatalog = dependencies.AIModelCatalog;
        _aiResourceService = dependencies.AIResourceService;
        _aiResourceOrchestrator = dependencies.AIResourceOrchestrator;
        _sam2RuntimeService = dependencies.SAM2RuntimeService;
        _ocrRuntimeService = dependencies.OcrRuntimeService;
        AIPathService = dependencies.AIPathService;
        ResourceQueue = dependencies.ResourceQueue;
        _moduleInstallCoordinator = new ModuleInstallCoordinator(
            _aiModelCatalog,
            _aiResourceService,
            _aiResourceOrchestrator,
            _settingsService,
            ResourceQueue);

        LocalizationService.Instance
            .WhenAnyValue(x => x.CurrentLanguage)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(SelectedLanguageOption));
                this.RaisePropertyChanged(nameof(UpdateDownloadStageText));
                this.RaisePropertyChanged(nameof(AvailableCaptureDelays));
                this.RaisePropertyChanged(nameof(AvailableOcrTextLayouts));
                this.RaisePropertyChanged(nameof(GifUnavailableReason));
                StatusText = LocalizationService.Instance[_currentStatusKey];
            });

        SetStatus("StatusReady");

        StartCaptureCommand = ReactiveCommand.CreateFromTask<CaptureMode>(StartCapture);
        ScrollingCaptureCommand = ReactiveCommand.CreateFromTask(() => StartCapture(CaptureMode.ScrollingCapture));
        SaveAndCloseCommand = ReactiveCommand.CreateFromTask(SaveAndClose);
        ResetToDefaultCommand = ReactiveCommand.CreateFromTask(ResetToDefault);
        IncreaseThicknessCommand = ReactiveCommand.Create(() => { if (BorderThickness < 9) BorderThickness += 1; });
        DecreaseThicknessCommand = ReactiveCommand.Create(() => { if (BorderThickness > 1) BorderThickness -= 1; });
        ChangeColorCommand = ReactiveCommand.Create<Color>(c => BorderColor = c);
        ChangeThemeColorCommand = ReactiveCommand.Create<Color>(c => ThemeColor = c);
        CheckUpdateCommand = ReactiveCommand.CreateFromTask(CheckForUpdates);
        OpenProjectCommand = ReactiveCommand.Create(() => OpenProjectUrl());

        MinimizeCommand = ReactiveCommand.Create(() =>
        {
            var win = _windowManager.GetMainWindow();
            if (win != null)
            {
                win.WindowState = Avalonia.Controls.WindowState.Minimized;
            }
        });
        MaximizeCommand = ReactiveCommand.Create(() =>
        {
            var win = _windowManager.GetMainWindow();
            if (win != null)
            {
                win.WindowState = win.WindowState == Avalonia.Controls.WindowState.Maximized
                    ? Avalonia.Controls.WindowState.Normal
                    : Avalonia.Controls.WindowState.Maximized;
            }
        });
        CloseCommand = ReactiveCommand.Create(() =>
        {
            _windowManager.GetMainWindow()?.Close();
        });

        IncreaseWingScaleCommand = ReactiveCommand.Create(() => { if (WingScale < 3.0) WingScale = Math.Round(WingScale + 0.1, 1); });
        DecreaseWingScaleCommand = ReactiveCommand.Create(() => { if (WingScale > 0.5) WingScale = Math.Round(WingScale - 0.1, 1); });
        IncreaseRecordFPSCommand = ReactiveCommand.Create(() => { if (RecordingSettings.RecordFPS < 60) RecordingSettings.RecordFPS = Math.Min(60, RecordingSettings.RecordFPS + 5); });
        DecreaseRecordFPSCommand = ReactiveCommand.Create(() => { if (RecordingSettings.RecordFPS > 5) RecordingSettings.RecordFPS = Math.Max(5, RecordingSettings.RecordFPS - 5); });
        IncreaseMaxRecordingSizeMBCommand = ReactiveCommand.Create(() => { if (RecordingSettings.MaxRecordingSizeMB < 5000) RecordingSettings.MaxRecordingSizeMB = Math.Min(5000, Math.Round(RecordingSettings.MaxRecordingSizeMB + 0.1, 1)); });
        DecreaseMaxRecordingSizeMBCommand = ReactiveCommand.Create(() => { if (RecordingSettings.MaxRecordingSizeMB > 0) RecordingSettings.MaxRecordingSizeMB = Math.Max(0, Math.Round(RecordingSettings.MaxRecordingSizeMB - 0.1, 1)); });
        IncreasePlaybackUiFpsCommand = ReactiveCommand.Create(() => { if (PlaybackUiFps < 120) PlaybackUiFps = Math.Min(120, PlaybackUiFps + 5); });
        DecreasePlaybackUiFpsCommand = ReactiveCommand.Create(() => { if (PlaybackUiFps > 1) PlaybackUiFps = Math.Max(1, PlaybackUiFps - 5); });
        IncreasePlaybackTimelineFpsCommand = ReactiveCommand.Create(() => { if (PlaybackTimelineFps < 120) PlaybackTimelineFps = Math.Min(120, PlaybackTimelineFps + 5); });
        DecreasePlaybackTimelineFpsCommand = ReactiveCommand.Create(() => { if (PlaybackTimelineFps > 1) PlaybackTimelineFps = Math.Max(1, PlaybackTimelineFps - 5); });

        PickAIFolderCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (PickFolderAction != null)
            {
                var path = await PickFolderAction();
                if (!string.IsNullOrEmpty(path))
                {
                    AIResourcesDirectory = path;
                }
            }
        });

        InitializeHistoryCommands();

        RefreshLlamaModelsCommand = ReactiveCommand.Create(() => RefreshLlamaModelCatalog());
        SelectLlamaModelCommand = ReactiveCommand.Create<string>(modelId =>
        {
            if (string.IsNullOrWhiteSpace(modelId))
            {
                return;
            }

            if (!string.Equals(LlamaModelId, modelId, StringComparison.Ordinal))
            {
                LlamaModelId = modelId;
            }

            IsLlamaModelPickerOpen = false;
        });
        InitializeAboutCommands();

        HotkeyService.OnHotkeyPressed = id =>
        {
            var snipVm = GetActiveSnipViewModelAction?.Invoke();
            if (snipVm != null)
            {
                snipVm.HandleGlobalHotkey(id);
                return;
            }

            if (HotkeyRouterService.TryMapGlobalHotkeyToCaptureMode(id, out var mode))
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => StartCaptureCommand.Execute(mode).Subscribe());
            }
        };

        HotkeyService.OnElevatedWindowFocused = () =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => RequestElevatedWindowPromptAction?.Invoke());
        };

        this.PropertyChanged += (s, e) =>
        {
            if (!_isDataLoading && e.PropertyName != nameof(StatusText) && e.PropertyName != nameof(IsModified))
            {
                if (!IsModified)
                {
                    IsModified = true;
                    SetStatus("StatusModified");
                }
            }
        };

        RecordingSettings.PropertyChanged += (s, e) =>
        {
            if (!_isDataLoading)
            {
                if (!IsModified)
                {
                    IsModified = true;
                    SetStatus("StatusModified");
                }
            }
        };

        _loadTask = LoadSettingsAsync();
    }

    private void MarkModifiedAndQueueSettingsSave()
    {
        _settingsSideEffectCoordinator.QueueSave(markModified: true);
    }

    private void QueueSettingsSave()
    {
        _settingsSideEffectCoordinator.QueueSave(markModified: false);
    }

    private void EnsureUpdateProcessingSubscription()
    {
        if (_updateProcessingSubscription != null)
        {
            return;
        }

        _updateProcessingSubscription = _updateService.Value
            .WhenAnyValue(x => x.IsDownloading, x => x.DownloadProgress)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => RecomputeProcessingState());

        RecomputeProcessingState();
    }

    private void EnsureAiProcessingSubscription()
    {
        if (_aiProcessingSubscription != null)
        {
            return;
        }

        _aiProcessingSubscription = _aiResourceService.Value
            .WhenAnyValue(x => x.IsDownloading, x => x.DownloadProgress)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => RecomputeProcessingState());

        RecomputeProcessingState();
    }

    private void RecomputeProcessingState()
    {
        bool isUpdateDownloading = _updateService.IsValueCreated && _updateService.Value.IsDownloading;
        double updateProgress = _updateService.IsValueCreated ? _updateService.Value.DownloadProgress : 0;
        bool isAiDownloading = _aiResourceService.IsValueCreated && _aiResourceService.Value.IsDownloading;

        var activeModules = _modulesInitialized
            ? Modules.AsValueEnumerable().Where(m => m.IsProcessing).ToList()
            : new List<ModuleItem>();

        if (!isAiDownloading)
        {
            activeModules.Clear();
        }

        int activeCount = activeModules.Count + (isUpdateDownloading ? 1 : 0);
        if (activeCount > 0)
        {
            double totalProgress = activeModules.AsValueEnumerable().Sum(m => m.Progress) + (isUpdateDownloading ? updateProgress : 0);
            double avgProgress = totalProgress / activeCount;
            IsProcessing = true;
            ShowProcessingOverlay = true;
            ProgressValue = avgProgress;
            IsIndeterminate = false;

            if (activeCount == 1)
            {
                if (isUpdateDownloading)
                {
                    ProcessingText = string.Format(LocalizationService.Instance["UpdateDownloading"], (int)avgProgress);
                }
                else
                {
                    var module = activeModules.AsValueEnumerable().First();
                    ProcessingText = $"{module.Name}... {(int)avgProgress}%";
                }
            }
            else
            {
                var prefix = LocalizationService.Instance["ComponentDownloadingProgress"].Replace("...", "").Trim();
                ProcessingText = $"{prefix} ({activeCount})... {(int)avgProgress}%";
            }

            StatusText = ProcessingText;
        }
        else
        {
            SetStatus("StatusReady");
            IsProcessing = false;
            ShowProcessingOverlay = false;
            ProgressValue = 0;
            ProcessingText = "";
        }
    }
}
