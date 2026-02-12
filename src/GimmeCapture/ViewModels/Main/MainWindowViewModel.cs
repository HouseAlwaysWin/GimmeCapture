using ReactiveUI;
using Avalonia;
using Avalonia.Media;
using System;
using System.Threading.Tasks;
using GimmeCapture.Models;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.IO;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core;
using GimmeCapture.Services.Platforms.Windows;

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

    public void SetStatus(string key)
    {
        _currentStatusKey = key;
        StatusText = LocalizationService.Instance[key];
    }

    public Action<CaptureMode>? RequestCaptureAction { get; set; }
    public Func<Task<string?>>? PickFolderAction { get; set; }
    public Func<string, string, Task<bool>>? ConfirmAction { get; set; }
    
    public AppSettingsService AppSettingsService => _settingsService;
    private readonly AppSettingsService _settingsService;
    public WindowsGlobalHotkeyService HotkeyService { get; } = new();

    private const int ID_SNIP = 9000;
    private const int ID_COPY = 9001;
    private const int ID_PIN = 9002;
    private const int ID_RECORD = 9003;

    public enum CaptureMode { Normal, Copy, Pin, Record }

    public FFmpegDownloaderService FfmpegDownloader { get; }
    public RecordingService RecordingService { get; }
    public UpdateService UpdateService { get; }
    public AIResourceService AIResourceService { get; }
    public ResourceQueueService ResourceQueue => ResourceQueueService.Instance;
    
    public ObservableCollection<ModuleItem> Modules { get; } = new();
    public string AppVersion => System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    // Commands - Initialized in constructor
    public ReactiveCommand<CaptureMode, Unit> StartCaptureCommand { get; } = null!;
    public ReactiveCommand<Unit, Unit> SaveAndCloseCommand { get; } = null!;
    public ReactiveCommand<Unit, Unit> ResetToDefaultCommand { get; } = null!;
    public ReactiveCommand<Unit, Unit> IncreaseThicknessCommand { get; } = null!;
    public ReactiveCommand<Unit, Unit> DecreaseThicknessCommand { get; } = null!;
    public ReactiveCommand<Unit, Unit> IncreaseOpacityCommand { get; } = null!;
    public ReactiveCommand<Unit, Unit> DecreaseOpacityCommand { get; } = null!;
    public ReactiveCommand<Color, Unit> ChangeColorCommand { get; } = null!;
    public ReactiveCommand<Color, Unit> ChangeThemeColorCommand { get; } = null!;
    public ReactiveCommand<Unit, Unit> CheckUpdateCommand { get; } = null!;
    public ReactiveCommand<Unit, Unit> OpenProjectCommand { get; } = null!;
    public ReactiveCommand<Unit, Unit> IncreaseWingScaleCommand { get; } = null!;
    public ReactiveCommand<Unit, Unit> DecreaseWingScaleCommand { get; } = null!;
    public ReactiveCommand<Unit, Unit> IncreaseRecordFPSCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> DecreaseRecordFPSCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit>? ToggleRecordCommand { get; set; }
    public ReactiveCommand<Unit, Unit> IncreaseCornerIconScaleCommand { get; } = null!;
    public ReactiveCommand<Unit, Unit> DecreaseCornerIconScaleCommand { get; } = null!;
    public ReactiveCommand<Unit, Unit> PickAIFolderCommand { get; } = null!;
    
    public Color[] SettingsColors { get; } = new[]
    {
        Color.Parse("#D4AF37"), // Gold
        Color.Parse("#E0E0E0"), // Silver
        Color.Parse("#E60012")  // Red
    };

    public MainWindowViewModel()
    {
        _settingsService = new AppSettingsService();
        FfmpegDownloader = new FFmpegDownloaderService(_settingsService);
        RecordingService = new RecordingService(FfmpegDownloader, _settingsService);
        UpdateService = new UpdateService(AppVersion);
        AIResourceService = new AIResourceService(_settingsService);

        LocalizationService.Instance
            .WhenAnyValue(x => x.CurrentLanguage)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => 
            {
                this.RaisePropertyChanged(nameof(SelectedLanguageOption));
                StatusText = LocalizationService.Instance[_currentStatusKey];
            });

        SetStatus("StatusReady");

        // Command Initialization
        StartCaptureCommand = ReactiveCommand.CreateFromTask<CaptureMode>(StartCapture);
        SaveAndCloseCommand = ReactiveCommand.CreateFromTask(SaveAndClose);
        ResetToDefaultCommand = ReactiveCommand.CreateFromTask(ResetToDefault);
        IncreaseThicknessCommand = ReactiveCommand.Create(() => { if (BorderThickness < 9) BorderThickness += 1; });
        DecreaseThicknessCommand = ReactiveCommand.Create(() => { if (BorderThickness > 1) BorderThickness -= 1; });
        IncreaseOpacityCommand = ReactiveCommand.Create(() => { if (MaskOpacity < 1.0) MaskOpacity = Math.Min(1.0, MaskOpacity + 0.05); });
        DecreaseOpacityCommand = ReactiveCommand.Create(() => { if (MaskOpacity > 0.05) MaskOpacity = Math.Max(0.05, MaskOpacity - 0.05); });
        ChangeColorCommand = ReactiveCommand.Create<Color>(c => BorderColor = c);
        ChangeThemeColorCommand = ReactiveCommand.Create<Color>(c => ThemeColor = c);
        CheckUpdateCommand = ReactiveCommand.CreateFromTask(CheckForUpdates);
        OpenProjectCommand = ReactiveCommand.Create(() => OpenProjectUrl());
        IncreaseWingScaleCommand = ReactiveCommand.Create(() => { if (WingScale < 3.0) WingScale = Math.Round(WingScale + 0.1, 1); });
        DecreaseWingScaleCommand = ReactiveCommand.Create(() => { if (WingScale > 0.5) WingScale = Math.Round(WingScale - 0.1, 1); });
        IncreaseCornerIconScaleCommand = ReactiveCommand.Create(() => { if (CornerIconScale < 1.0) CornerIconScale = Math.Round(CornerIconScale + 0.1, 1); });
        DecreaseCornerIconScaleCommand = ReactiveCommand.Create(() => { if (CornerIconScale > 0.4) CornerIconScale = Math.Round(CornerIconScale - 0.1, 1); });
        IncreaseRecordFPSCommand = ReactiveCommand.Create(() => { if (RecordFPS < 60) RecordFPS = Math.Min(60, RecordFPS + 5); });
        DecreaseRecordFPSCommand = ReactiveCommand.Create(() => { if (RecordFPS > 5) RecordFPS = Math.Max(5, RecordFPS - 5); });
        
        PickAIFolderCommand = ReactiveCommand.CreateFromTask(async () => {
            if (PickFolderAction != null)
            {
                var path = await PickFolderAction();
                if (!string.IsNullOrEmpty(path)) AIResourcesDirectory = path;
            }
        });

        HotkeyService.OnHotkeyPressed = (id) => 
        {
            if (id == ID_SNIP) Avalonia.Threading.Dispatcher.UIThread.Post(() => StartCaptureCommand.Execute(CaptureMode.Normal));
            else if (id == ID_RECORD) Avalonia.Threading.Dispatcher.UIThread.Post(() => StartCaptureCommand.Execute(CaptureMode.Record));
        };

        // Download status aggregation
        var isAnyProcessing = Observable.CombineLatest(
            FfmpegDownloader.WhenAnyValue(x => x.IsDownloading),
            UpdateService.WhenAnyValue(x => x.IsDownloading),
            AIResourceService.WhenAnyValue(x => x.IsDownloading),
            (a, b, c) => a || b || c
        );

        isAnyProcessing.ObserveOn(RxApp.MainThreadScheduler).Subscribe(busy => IsProcessing = busy);

        var processingSources = new[] 
        {
            FfmpegDownloader.WhenAnyValue(x => x.IsDownloading, x => x.DownloadProgress).Select(x => ("FFmpeg", x.Item1, x.Item2)),
            UpdateService.WhenAnyValue(x => x.IsDownloading, x => x.DownloadProgress).Select(x => ("Update", x.Item1, x.Item2)),
            AIResourceService.WhenAnyValue(x => x.IsDownloading, x => x.DownloadProgress).Select(x => ("AI", x.Item1, x.Item2))
        };

        Observable.CombineLatest(processingSources)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(states => 
            {
                var updateState = states.FirstOrDefault(s => s.Item1 == "Update");
                bool isUpdateDownloading = updateState.Item2;
                double updateProgress = updateState.Item3;

                var activeModules = Modules.Where(m => m.IsProcessing).ToList();
                int activeCount = activeModules.Count + (isUpdateDownloading ? 1 : 0);

                if (activeCount > 0)
                {
                    double totalProgress = activeModules.Sum(m => m.Progress) + (isUpdateDownloading ? updateProgress : 0);
                    double avgProgress = totalProgress / activeCount;
                    IsProcessing = true;
                    ProgressValue = avgProgress;
                    IsIndeterminate = false;

                    if (activeCount == 1)
                    {
                        if (isUpdateDownloading) ProcessingText = string.Format(LocalizationService.Instance["UpdateDownloading"], (int)avgProgress);
                        else
                        {
                             var module = activeModules.First();
                             ProcessingText = $"{module.Name}... {(int)avgProgress}%";
                        }
                    }
                    else
                    {
                        var prefix = LocalizationService.Instance["ComponentDownloadingProgress"].Replace("...", "").Replace("中", "");
                        ProcessingText = $"{prefix} ({activeCount})... {(int)avgProgress}%";
                    }
                    StatusText = ProcessingText;
                }
                else if (IsProcessing)
                {
                    if (StatusText.Contains("Downloading") || StatusText.Contains("下載")) SetStatus("StatusReady");
                    IsProcessing = false;
                    ProgressValue = 0;
                    ProcessingText = "";
                }
            });

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

        _loadTask = LoadSettingsAsync();
    }
}
