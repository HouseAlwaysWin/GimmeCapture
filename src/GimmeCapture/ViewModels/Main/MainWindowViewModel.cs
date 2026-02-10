using ReactiveUI;
using Avalonia;
using Avalonia.Media;
using System;
using System.Threading.Tasks;
using Avalonia.Controls.ApplicationLifetimes;
using GimmeCapture.Models;
using GimmeCapture.Views.Dialogs;
using GimmeCapture.Views.Main;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.IO;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core;
using GimmeCapture.Services.Platforms.Windows;
using System.Windows.Input;
using DynamicData;

namespace GimmeCapture.ViewModels.Main;

public class MainWindowViewModel : ViewModelBase
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

    // Hotkey IDs
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

    // Commands
    public ReactiveCommand<CaptureMode, Unit> StartCaptureCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveAndCloseCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetToDefaultCommand { get; }
    public ReactiveCommand<Unit, Unit> IncreaseThicknessCommand { get; }
    public ReactiveCommand<Unit, Unit> DecreaseThicknessCommand { get; }
    public ReactiveCommand<Unit, Unit> IncreaseOpacityCommand { get; }
    public ReactiveCommand<Unit, Unit> DecreaseOpacityCommand { get; }
    public ReactiveCommand<Color, Unit> ChangeColorCommand { get; }
    public ReactiveCommand<Color, Unit> ChangeThemeColorCommand { get; }
    public ReactiveCommand<Unit, Unit> CheckUpdateCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenProjectCommand { get; }
    public ReactiveCommand<Unit, Unit> IncreaseWingScaleCommand { get; }
    public ReactiveCommand<Unit, Unit> DecreaseWingScaleCommand { get; }

    public ReactiveCommand<Unit, Unit> IncreaseRecordFPSCommand { get; set; }
    public ReactiveCommand<Unit, Unit> DecreaseRecordFPSCommand { get; set; }

    public ReactiveCommand<Unit, Unit>? ToggleRecordCommand { get; set; }
    public ReactiveCommand<Unit, Unit> IncreaseCornerIconScaleCommand { get; }
    public ReactiveCommand<Unit, Unit> DecreaseCornerIconScaleCommand { get; }
    public ReactiveCommand<Unit, Unit> PickAIFolderCommand { get; }
    
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
        // Sync ViewModel with Service using ReactiveUI
        // When Service language changes, notify ViewModel properties to update
        LocalizationService.Instance
            .WhenAnyValue(x => x.CurrentLanguage)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => 
            {
                this.RaisePropertyChanged(nameof(SelectedLanguageOption));
                // Update Status Text on Language Change
                StatusText = LocalizationService.Instance[_currentStatusKey];
            });

        SetStatus("StatusReady");

        StartCaptureCommand = ReactiveCommand.CreateFromTask<CaptureMode>(StartCapture);
        StartCaptureCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        SaveAndCloseCommand = ReactiveCommand.CreateFromTask(SaveAndClose);
        SaveAndCloseCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        ResetToDefaultCommand = ReactiveCommand.CreateFromTask(ResetToDefault);
        ResetToDefaultCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));

        IncreaseThicknessCommand = ReactiveCommand.Create(() => { if (BorderThickness < 9) BorderThickness += 1; });
        IncreaseThicknessCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        DecreaseThicknessCommand = ReactiveCommand.Create(() => { if (BorderThickness > 1) BorderThickness -= 1; });
        DecreaseThicknessCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        
        IncreaseOpacityCommand = ReactiveCommand.Create(() => { if (MaskOpacity < 1.0) MaskOpacity = Math.Min(1.0, MaskOpacity + 0.05); });
        IncreaseOpacityCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        DecreaseOpacityCommand = ReactiveCommand.Create(() => { if (MaskOpacity > 0.05) MaskOpacity = Math.Max(0.05, MaskOpacity - 0.05); });
        DecreaseOpacityCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        
        ChangeColorCommand = ReactiveCommand.Create<Color>(c => BorderColor = c);
        ChangeColorCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        ChangeThemeColorCommand = ReactiveCommand.Create<Color>(c => ThemeColor = c);
        ChangeThemeColorCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        CheckUpdateCommand = ReactiveCommand.CreateFromTask(CheckForUpdates);
        CheckUpdateCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        OpenProjectCommand = ReactiveCommand.Create(() => OpenProjectUrl());
        OpenProjectCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        
        IncreaseWingScaleCommand = ReactiveCommand.Create(() => { if (WingScale < 3.0) WingScale = Math.Round(WingScale + 0.1, 1); });
        IncreaseWingScaleCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        DecreaseWingScaleCommand = ReactiveCommand.Create(() => { if (WingScale > 0.5) WingScale = Math.Round(WingScale - 0.1, 1); });
        DecreaseWingScaleCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        
        IncreaseCornerIconScaleCommand = ReactiveCommand.Create(() => { if (CornerIconScale < 1.0) CornerIconScale = Math.Round(CornerIconScale + 0.1, 1); });
        IncreaseCornerIconScaleCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        DecreaseCornerIconScaleCommand = ReactiveCommand.Create(() => { if (CornerIconScale > 0.4) CornerIconScale = Math.Round(CornerIconScale - 0.1, 1); });
        DecreaseCornerIconScaleCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        
        IncreaseRecordFPSCommand = ReactiveCommand.Create(() => { if (RecordFPS < 60) RecordFPS = Math.Min(60, RecordFPS + 5); });
        IncreaseRecordFPSCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        DecreaseRecordFPSCommand = ReactiveCommand.Create(() => { if (RecordFPS > 5) RecordFPS = Math.Max(5, RecordFPS - 5); });
        DecreaseRecordFPSCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        
        PickAIFolderCommand = ReactiveCommand.CreateFromTask(async () => {
            if (PickFolderAction != null)
            {
                var path = await PickFolderAction();
                if (!string.IsNullOrEmpty(path))
                {
                    AIResourcesDirectory = path;
                }
            }
        });
        PickAIFolderCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        
        // Setup Hotkey Action
        HotkeyService.OnHotkeyPressed = (id) => 
        {
            if (id == ID_SNIP)
            {
                // Must run on UI thread if it involves UI updates
                Avalonia.Threading.Dispatcher.UIThread.Post(() => StartCaptureCommand.Execute(CaptureMode.Normal));
            }
            else if (id == ID_RECORD)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => StartCaptureCommand.Execute(CaptureMode.Record));
            }
        };

        // Combine multiple loading/processing signals for the global Loading Window
        var isAnyProcessing = Observable.CombineLatest(
            FfmpegDownloader.WhenAnyValue(x => x.IsDownloading),
            UpdateService.WhenAnyValue(x => x.IsDownloading),
            AIResourceService.WhenAnyValue(x => x.IsDownloading),
            (a, b, c) => a || b || c
        );

        isAnyProcessing
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(busy => IsProcessing = busy);

        // Unified Progress Handling to prevent flickering
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
                // Find update state from the combined sources
                var updateState = states.FirstOrDefault(s => s.Item1 == "Update");
                bool isUpdateDownloading = updateState.Item2;
                double updateProgress = updateState.Item3;

                var activeModules = Modules.Where(m => m.IsProcessing).ToList();
                int activeCount = activeModules.Count + (isUpdateDownloading ? 1 : 0);

                if (activeCount > 0)
                {
                    double totalProgress = activeModules.Sum(m => m.Progress) + (isUpdateDownloading ? updateProgress : 0);
                    double avgProgress = totalProgress / activeCount;
                    
                    // Update main processing state
                    IsProcessing = true;
                    ProgressValue = avgProgress;
                    IsIndeterminate = false;

                    if (activeCount == 1)
                    {
                        // Specific message
                        if (isUpdateDownloading)
                        {
                             ProcessingText = string.Format(LocalizationService.Instance["UpdateDownloading"], (int)avgProgress);
                        }
                        else
                        {
                             var module = activeModules.First();
                             ProcessingText = $"{module.Name}... {(int)avgProgress}%";
                        }
                    }
                    else
                    {
                        // Generic aggregate message
                        var prefix = LocalizationService.Instance["ComponentDownloadingProgress"].Replace("...", "").Replace("中", "");
                        ProcessingText = $"{prefix} ({activeCount})... {(int)avgProgress}%";
                    }
                    
                    StatusText = ProcessingText;
                }
                else
                {
                    if (IsProcessing)
                    {
                         if (StatusText.Contains("Downloading") || StatusText.Contains("下載"))
                             SetStatus("StatusReady");
                    }
                }
            });

        // Combined Boolean for IsProcessing Visibility
        Observable.CombineLatest(
            FfmpegDownloader.WhenAnyValue(x => x.IsDownloading),
            UpdateService.WhenAnyValue(x => x.IsDownloading),
            AIResourceService.WhenAnyValue(x => x.IsDownloading),
            (a, b, c) => a || b || c
        ).ObserveOn(RxApp.MainThreadScheduler)
         .Subscribe(busy => IsProcessing = busy);

        // Track changes AFTER loading
        this.PropertyChanged += (s, e) =>
        {
            if (!_isDataLoading && 
                e.PropertyName != nameof(StatusText) && 
                e.PropertyName != nameof(IsModified))
            {
                if (!IsModified)
                {
                    IsModified = true;
                    SetStatus("StatusModified");
                }
            }
        };

        // Initialize on UI thread and keep track of the task
        _loadTask = LoadSettingsAsync();
    }

    // Language Selection
    public class LanguageOption
    {
        public string Name { get; set; } = string.Empty;
        public Language Value { get; set; }
    }

    public LanguageOption[] AvailableLanguages { get; } = new[]
    {
        new LanguageOption { Name = "English (US)", Value = Language.English },
        new LanguageOption { Name = "繁體中文 (台灣)", Value = Language.Chinese },
        new LanguageOption { Name = "日本語 (日本)", Value = Language.Japanese }
    };

    public string AIResourcesDirectory
    {
        get => string.IsNullOrEmpty(_settingsService.Settings.AIResourcesDirectory) 
               ? Path.Combine(_settingsService.BaseDataDirectory, "AI") 
               : _settingsService.Settings.AIResourcesDirectory;
        set
        {
            _settingsService.Settings.AIResourcesDirectory = value;
            this.RaisePropertyChanged();
            IsModified = true;
        }
    }

    public LanguageOption SelectedLanguageOption
    {
        get => AvailableLanguages.FirstOrDefault(x => x.Value == LocalizationService.Instance.CurrentLanguage) ?? AvailableLanguages[0];
        set
        {
            if (value != null && LocalizationService.Instance.CurrentLanguage != value.Value)
            {
                // ALWAYS update the service first to ensure UI reflects change immediately
                LocalizationService.Instance.CurrentLanguage = value.Value;
                this.RaisePropertyChanged();
                
                // Propagate to settings and auto-save (if not loading)
                if (!_isDataLoading)
                {
                    _settingsService.Settings.Language = value.Value;
                    IsModified = true;
                    _ = SaveSettingsAsync();
                }
            }
        }
    }





    private bool _runOnStartup;
    public bool RunOnStartup
    {
        get => _runOnStartup;
        set => this.RaiseAndSetIfChanged(ref _runOnStartup, value);
    }

    private bool _autoCheckUpdates;
    public bool AutoCheckUpdates
    {
        get => _autoCheckUpdates;
        set => this.RaiseAndSetIfChanged(ref _autoCheckUpdates, value);
    }

    // Snip Settings
    private double _borderThickness;
    public double BorderThickness
    {
        get => _borderThickness;
        set => this.RaiseAndSetIfChanged(ref _borderThickness, value);
    }

    private double _maskOpacity;
    public double MaskOpacity
    {
        get => _maskOpacity;
        set => this.RaiseAndSetIfChanged(ref _maskOpacity, value);
    }

    private double _wingScale;
    public double WingScale
    {
        get => _wingScale;
        set 
        {
            this.RaiseAndSetIfChanged(ref _wingScale, value);
            this.RaisePropertyChanged(nameof(PreviewWingWidth));
            this.RaisePropertyChanged(nameof(PreviewWingHeight));
            this.RaisePropertyChanged(nameof(PreviewLeftWingMargin));
            this.RaisePropertyChanged(nameof(PreviewRightWingMargin));
        }
    }

    private double _cornerIconScale = 1.0;
    public double CornerIconScale
    {
        get => _cornerIconScale;
        set
        {
            this.RaiseAndSetIfChanged(ref _cornerIconScale, value);
            this.RaisePropertyChanged(nameof(PreviewIconSize));
        }
    }

    public double PreviewIconSize => 28 * CornerIconScale;

    public double PreviewWingWidth => 100 * WingScale * 0.5; // Reduced from 0.8 to 0.5
    public double PreviewWingHeight => 60 * WingScale * 0.5; // Reduced from 0.8 to 0.5
    public Thickness PreviewLeftWingMargin => new Thickness(-PreviewWingWidth, 0, 0, 0);
    public Thickness PreviewRightWingMargin => new Thickness(0, 0, -PreviewWingWidth, 0);
    
    private Color _borderColor;
    public Color BorderColor
    {
        get => _borderColor;
        set => this.RaiseAndSetIfChanged(ref _borderColor, value);
    }

    private Color _themeColor;
    public Color ThemeColor
    {
        get => _themeColor;
        set 
        {
            var old = _themeColor;
            this.RaiseAndSetIfChanged(ref _themeColor, value);
            if (old != value)
            {
                UpdateThemeResources(value);
                this.RaisePropertyChanged(nameof(ThemeDeepColor));
            }
        }
    }

    public Color ThemeDeepColor 
    {
        get
        {
            if (ThemeColor == Color.Parse("#D4AF37")) return Color.Parse("#8B7500");
            if (ThemeColor == Color.Parse("#E0E0E0")) return Color.Parse("#606060");
            return Color.Parse("#900000");
        }
    }

    // Output Settings
    private bool _autoSave;
    public bool AutoSave
    {
        get => _autoSave;
        set => this.RaiseAndSetIfChanged(ref _autoSave, value);
    }
    
    private string _saveDirectory = string.Empty;
    public string SaveDirectory
    {
        get => _saveDirectory;
        set => this.RaiseAndSetIfChanged(ref _saveDirectory, value);
    }
    
    // Control Settings
    private string _snipHotkey = "F1";
    public string SnipHotkey
    {
        get => _snipHotkey;
        set
        {
            this.RaiseAndSetIfChanged(ref _snipHotkey, value);
            HotkeyService.Register(ID_SNIP, value);
            this.RaisePropertyChanged(nameof(SnipTooltip));
        }
    }

    private string _copyHotkey = "Ctrl+C";
    public string CopyHotkey
    {
        get => _copyHotkey;
        set
        {
            this.RaiseAndSetIfChanged(ref _copyHotkey, value);
            this.RaisePropertyChanged(nameof(CopyTooltip));
        }
    }

    private string _pinHotkey = "F3";
    public string PinHotkey
    {
        get => _pinHotkey;
        set
        {
            this.RaiseAndSetIfChanged(ref _pinHotkey, value);
            this.RaisePropertyChanged(nameof(PinTooltip));
        }
    }

    private string _recordHotkey = "F2";
    public string RecordHotkey
    {
        get => _recordHotkey;
        set
        {
            this.RaiseAndSetIfChanged(ref _recordHotkey, value);
            HotkeyService.Register(ID_RECORD, value);
            this.RaisePropertyChanged(nameof(RecordTooltip));
        }
    }

    public string SnipTooltip => $"{LocalizationService.Instance["StartCapture"]} ({SnipHotkey})";
    public string RecordTooltip => $"{LocalizationService.Instance["CaptureModeRecord"]} ({RecordHotkey})";
    public string CopyTooltip => $"{LocalizationService.Instance["TipCopy"]} ({CopyHotkey})";
    public string PinTooltip => $"{LocalizationService.Instance["TipPin"]} ({PinHotkey})";

    // Drawing Tool Hotkeys
    private string _rectangleHotkey = "R";
    public string RectangleHotkey
    {
        get => _rectangleHotkey;
        set => this.RaiseAndSetIfChanged(ref _rectangleHotkey, value);
    }

    private string _ellipseHotkey = "E";
    public string EllipseHotkey
    {
        get => _ellipseHotkey;
        set => this.RaiseAndSetIfChanged(ref _ellipseHotkey, value);
    }

    private string _arrowHotkey = "A";
    public string ArrowHotkey
    {
        get => _arrowHotkey;
        set => this.RaiseAndSetIfChanged(ref _arrowHotkey, value);
    }

    private string _lineHotkey = "L";
    public string LineHotkey
    {
        get => _lineHotkey;
        set => this.RaiseAndSetIfChanged(ref _lineHotkey, value);
    }

    private string _penHotkey = "P";
    public string PenHotkey
    {
        get => _penHotkey;
        set => this.RaiseAndSetIfChanged(ref _penHotkey, value);
    }

    private string _textHotkey = "T";
    public string TextHotkey
    {
        get => _textHotkey;
        set => this.RaiseAndSetIfChanged(ref _textHotkey, value);
    }

    private string _mosaicHotkey = "M";
    public string MosaicHotkey
    {
        get => _mosaicHotkey;
        set => this.RaiseAndSetIfChanged(ref _mosaicHotkey, value);
    }

    private string _blurHotkey = "B";
    public string BlurHotkey
    {
        get => _blurHotkey;
        set => this.RaiseAndSetIfChanged(ref _blurHotkey, value);
    }

    // Action Hotkeys
    private string _undoHotkey = "Ctrl+Z";
    public string UndoHotkey
    {
        get => _undoHotkey;
        set => this.RaiseAndSetIfChanged(ref _undoHotkey, value);
    }

    private string _redoHotkey = "Ctrl+Y";
    public string RedoHotkey
    {
        get => _redoHotkey;
        set => this.RaiseAndSetIfChanged(ref _redoHotkey, value);
    }

    private string _clearHotkey = "Delete";
    public string ClearHotkey
    {
        get => _clearHotkey;
        set => this.RaiseAndSetIfChanged(ref _clearHotkey, value);
    }

    private string _saveHotkey = "Ctrl+S";
    public string SaveHotkey
    {
        get => _saveHotkey;
        set => this.RaiseAndSetIfChanged(ref _saveHotkey, value);
    }

    private string _closeHotkey = "Escape";
    public string CloseHotkey
    {
        get => _closeHotkey;
        set => this.RaiseAndSetIfChanged(ref _closeHotkey, value);
    }

    private string _togglePlaybackHotkey = "Space";
    public string TogglePlaybackHotkey
    {
        get => _togglePlaybackHotkey;
        set => this.RaiseAndSetIfChanged(ref _togglePlaybackHotkey, value);
    }

    private string _toggleToolbarHotkey = "F4";
    public string ToggleToolbarHotkey
    {
        get => _toggleToolbarHotkey;
        set => this.RaiseAndSetIfChanged(ref _toggleToolbarHotkey, value);
    }

    private string _selectionModeHotkey = "S";
    public string SelectionModeHotkey
    {
        get => _selectionModeHotkey;
        set => this.RaiseAndSetIfChanged(ref _selectionModeHotkey, value);
    }

    private string _cropModeHotkey = "C";
    public string CropModeHotkey
    {
        get => _cropModeHotkey;
        set => this.RaiseAndSetIfChanged(ref _cropModeHotkey, value);
    }

    private string _videoSaveDirectory = string.Empty;
    public string VideoSaveDirectory
    {
        get => _videoSaveDirectory;
        set => this.RaiseAndSetIfChanged(ref _videoSaveDirectory, value);
    }

    private string _recordFormat = "gif";
    public string RecordFormat
    {
        get => _recordFormat;
        set => this.RaiseAndSetIfChanged(ref _recordFormat, value);
    }

    private int _recordFPS = 30;
    public int RecordFPS
    {
        get => _recordFPS;
        set => this.RaiseAndSetIfChanged(ref _recordFPS, value);
    }

    private bool _useFixedRecordPath;
    public bool UseFixedRecordPath
    {
        get => _useFixedRecordPath;
        set => this.RaiseAndSetIfChanged(ref _useFixedRecordPath, value);
    }

    private bool _hideSnipPinDecoration = false;
    public bool HideSnipPinDecoration
    {
        get => _hideSnipPinDecoration;
        set => this.RaiseAndSetIfChanged(ref _hideSnipPinDecoration, value);
    }

    private bool _hideSnipPinBorder = false;
    public bool HideSnipPinBorder
    {
        get => _hideSnipPinBorder;
        set => this.RaiseAndSetIfChanged(ref _hideSnipPinBorder, value);
    }

    private bool _hideRecordPinDecoration = false;
    public bool HideRecordPinDecoration
    {
        get => _hideRecordPinDecoration;
        set => this.RaiseAndSetIfChanged(ref _hideRecordPinDecoration, value);
    }

    private bool _hideRecordPinBorder = false;
    public bool HideRecordPinBorder
    {
        get => _hideRecordPinBorder;
        set => this.RaiseAndSetIfChanged(ref _hideRecordPinBorder, value);
    }

    private bool _hideSnipSelectionDecoration = false;
    public bool HideSnipSelectionDecoration
    {
        get => _hideSnipSelectionDecoration;
        set => this.RaiseAndSetIfChanged(ref _hideSnipSelectionDecoration, value);
    }

    private bool _hideSnipSelectionBorder = false;
    public bool HideSnipSelectionBorder
    {
        get => _hideSnipSelectionBorder;
        set => this.RaiseAndSetIfChanged(ref _hideSnipSelectionBorder, value);
    }

    private bool _hideRecordSelectionDecoration = false;
    public bool HideRecordSelectionDecoration
    {
        get => _hideRecordSelectionDecoration;
        set => this.RaiseAndSetIfChanged(ref _hideRecordSelectionDecoration, value);
    }

    private bool _hideRecordSelectionBorder = false;
    public bool HideRecordSelectionBorder
    {
        get => _hideRecordSelectionBorder;
        set => this.RaiseAndSetIfChanged(ref _hideRecordSelectionBorder, value);
    }

    private string _tempDirectory = string.Empty;
    public string TempDirectory
    {
        get => _tempDirectory;
        set => this.RaiseAndSetIfChanged(ref _tempDirectory, value);
    }

    private bool _showSnipCursor = false;
    public bool ShowSnipCursor
    {
        get => _showSnipCursor;
        set => this.RaiseAndSetIfChanged(ref _showSnipCursor, value);
    }

    private bool _showAIScanBox = true;
    public bool ShowAIScanBox
    {
        get => _showAIScanBox;
        set => this.RaiseAndSetIfChanged(ref _showAIScanBox, value);
    }
    
    private bool _enableAI = true;
    public bool EnableAI
    {
        get => _enableAI;
        set 
        {
            this.RaiseAndSetIfChanged(ref _enableAI, value);
            
            // Fix: Only save if we are NOT loading data
            if (!_isDataLoading)
            {
                _settingsService.Settings.EnableAI = value;
                _ = SaveSettingsAsync();
            }
        }
    }

    private int _sam2GridDensity = 8;
    public int SAM2GridDensity
    {
        get => _sam2GridDensity;
        set => this.RaiseAndSetIfChanged(ref _sam2GridDensity, value);
    }

    private int _sam2MaxObjects = 20;
    public int SAM2MaxObjects
    {
        get => _sam2MaxObjects;
        set => this.RaiseAndSetIfChanged(ref _sam2MaxObjects, value);
    }

    private int _sam2MinObjectSize = 20;
    public int SAM2MinObjectSize
    {
        get => _sam2MinObjectSize;
        set => this.RaiseAndSetIfChanged(ref _sam2MinObjectSize, value);
    }



    private bool _showRecordCursor = true;
    public bool ShowRecordCursor
    {
        get => _showRecordCursor;
        set => this.RaiseAndSetIfChanged(ref _showRecordCursor, value);
    }

    public string[] AvailableRecordFormats { get; } = { "mp4", "mkv", "gif", "webm", "mov" };

    public async Task SelectVideoPath()
    {
        if (PickFolderAction != null)
        {
            var path = await PickFolderAction();
            if (!string.IsNullOrEmpty(path)) VideoSaveDirectory = path;
        }
    }

    public async Task SelectSavePath()
    {
        if (PickFolderAction != null)
        {
            var path = await PickFolderAction();
            if (!string.IsNullOrEmpty(path)) SaveDirectory = path;
        }
    }

    public async Task SelectTempPath()
    {
        if (PickFolderAction != null)
        {
            var path = await PickFolderAction();
            if (!string.IsNullOrEmpty(path)) TempDirectory = path;
        }
    }

    public async Task LoadSettingsAsync()
    {
        await _settingsService.LoadAsync();
        var s = _settingsService.Settings;
        
        // Map settings to properties (ViewModel -> View)
        RunOnStartup = s.RunOnStartup;
        AutoCheckUpdates = s.AutoCheckUpdates;
        BorderThickness = s.BorderThickness;
        MaskOpacity = s.MaskOpacity;
        WingScale = s.WingScale;
        CornerIconScale = s.CornerIconScale;
        AutoSave = s.AutoSave;
        SnipHotkey = s.SnipHotkey;
        CopyHotkey = s.CopyHotkey;
        PinHotkey = s.PinHotkey;
        HideSnipPinDecoration = s.HideSnipPinDecoration;
        HideSnipPinBorder = s.HideSnipPinBorder;
        HideRecordPinDecoration = s.HideRecordPinDecoration;
        HideRecordPinBorder = s.HideRecordPinBorder;
        HideSnipSelectionDecoration = s.HideSnipSelectionDecoration;
        HideSnipSelectionBorder = s.HideSnipSelectionBorder;
        HideRecordSelectionDecoration = s.HideRecordSelectionDecoration;
        HideRecordSelectionBorder = s.HideRecordSelectionBorder;
        ShowSnipCursor = s.ShowSnipCursor;
        ShowRecordCursor = s.ShowRecordCursor;
        TempDirectory = s.TempDirectory;
        ShowAIScanBox = s.ShowAIScanBox;
        EnableAI = s.EnableAI;
        SAM2GridDensity = s.SAM2GridDensity;
        SAM2MaxObjects = s.SAM2MaxObjects;
        SAM2MinObjectSize = s.SAM2MinObjectSize;
        if (string.IsNullOrEmpty(TempDirectory))
        {
            TempDirectory = Path.Combine(_settingsService.BaseDataDirectory, "Temp");
            try { if (!Directory.Exists(TempDirectory)) Directory.CreateDirectory(TempDirectory); } catch { }
        }
        
        if (Color.TryParse(s.BorderColorHex, out var color))
        {
            BorderColor = color;
        }

        if (Color.TryParse(s.ThemeColorHex, out var themeColor))
        {
            ThemeColor = themeColor;
        }
        else
        {
            ThemeColor = Color.Parse("#E60012");
        }

        VideoSaveDirectory = s.VideoSaveDirectory;
        if (string.IsNullOrEmpty(VideoSaveDirectory))
        {
            VideoSaveDirectory = Path.Combine(_settingsService.BaseDataDirectory, "Recordings");
            try { if (!Directory.Exists(VideoSaveDirectory)) Directory.CreateDirectory(VideoSaveDirectory); } catch { }
        }

        SaveDirectory = s.SaveDirectory;
        if (string.IsNullOrEmpty(SaveDirectory))
        {
            SaveDirectory = Path.Combine(_settingsService.BaseDataDirectory, "Captures");
            try { if (!Directory.Exists(SaveDirectory)) Directory.CreateDirectory(SaveDirectory); } catch { }
        }

        RecordFormat = s.RecordFormat;
        RecordFPS = s.RecordFPS;
        UseFixedRecordPath = s.UseFixedRecordPath;
        RecordHotkey = s.RecordHotkey;

        // Drawing Tools
        RectangleHotkey = s.RectangleHotkey;
        EllipseHotkey = s.EllipseHotkey;
        ArrowHotkey = s.ArrowHotkey;
        LineHotkey = s.LineHotkey;
        PenHotkey = s.PenHotkey;
        TextHotkey = s.TextHotkey;
        MosaicHotkey = s.MosaicHotkey;
        BlurHotkey = s.BlurHotkey;

        // Actions
        UndoHotkey = s.UndoHotkey;
        RedoHotkey = s.RedoHotkey;
        ClearHotkey = s.ClearHotkey;
        SaveHotkey = s.SaveHotkey;
        CloseHotkey = s.CloseHotkey;
        TogglePlaybackHotkey = s.TogglePlaybackHotkey;
        ToggleToolbarHotkey = s.ToggleToolbarHotkey;
        SelectionModeHotkey = s.SelectionModeHotkey;
        CropModeHotkey = s.CropModeHotkey;

        // Load Language
        LocalizationService.Instance.CurrentLanguage = s.Language;

        // Ensure registry is in sync with setting
        StartupService.SetStartup(s.RunOnStartup);

        // Register initial hotkeys
        // Register initial hotkeys
        Avalonia.Threading.Dispatcher.UIThread.Post(() => {
            HotkeyService.Register(ID_SNIP, SnipHotkey);
            HotkeyService.Register(ID_RECORD, RecordHotkey);
        });

        if (AutoCheckUpdates)
        {
            _ = Task.Run(async () => await CheckForUpdates(silent: true));
        }

        // Initialize modules ONLY after settings are loaded to avoid rogue saves with defaults
        InitializeModules();

        _isDataLoading = false;
    }

    public async Task<bool> SaveSettingsAsync()
    {
        // CRITICAL: Block any save attempts while loading to prevent old data from overwriting new files
        if (_isDataLoading) return false;

        if (_loadTask != null && !_loadTask.IsCompleted)
            await _loadTask;

        var s = _settingsService.Settings;
        
        try
        {
            // Map properties to settings (User Input -> Model)
            s.RunOnStartup = RunOnStartup;
            s.AutoCheckUpdates = AutoCheckUpdates;
            s.BorderThickness = BorderThickness;
            s.MaskOpacity = MaskOpacity;
            s.WingScale = WingScale;
            s.CornerIconScale = CornerIconScale;
            s.AutoSave = AutoSave;
            s.SaveDirectory = SaveDirectory;
            s.SnipHotkey = SnipHotkey;
            s.CopyHotkey = CopyHotkey;
            s.PinHotkey = PinHotkey;
            s.RecordHotkey = RecordHotkey;
            s.RectangleHotkey = RectangleHotkey;
            s.EllipseHotkey = EllipseHotkey;
            s.ArrowHotkey = ArrowHotkey;
            s.LineHotkey = LineHotkey;
            s.PenHotkey = PenHotkey;
            s.TextHotkey = TextHotkey;
            s.MosaicHotkey = MosaicHotkey;
            s.BlurHotkey = BlurHotkey;
            s.UndoHotkey = UndoHotkey;
            s.RedoHotkey = RedoHotkey;
            s.ClearHotkey = ClearHotkey;
            s.SaveHotkey = SaveHotkey;
            s.CloseHotkey = CloseHotkey;
            s.TogglePlaybackHotkey = TogglePlaybackHotkey;
            s.ToggleToolbarHotkey = ToggleToolbarHotkey;
            s.SelectionModeHotkey = SelectionModeHotkey;
            s.CropModeHotkey = CropModeHotkey;
            s.BorderColorHex = BorderColor.ToString();
            s.ThemeColorHex = ThemeColor.ToString();
            
            // Sync current state of Singleton services
            s.Language = LocalizationService.Instance.CurrentLanguage;
            s.AIResourcesDirectory = AIResourcesDirectory;
            s.VideoSaveDirectory = VideoSaveDirectory;
            
            s.RecordFormat = RecordFormat;
            s.RecordFPS = RecordFPS;
            s.UseFixedRecordPath = UseFixedRecordPath;
            s.HideSnipPinDecoration = HideSnipPinDecoration;
            s.HideSnipPinBorder = HideSnipPinBorder;
            s.HideRecordPinDecoration = HideRecordPinDecoration;
            s.HideRecordPinBorder = HideRecordPinBorder;
            s.HideSnipSelectionDecoration = HideSnipSelectionDecoration;
            s.HideSnipSelectionBorder = HideSnipSelectionBorder;
            s.HideRecordSelectionDecoration = HideRecordSelectionDecoration;
            s.HideRecordSelectionBorder = HideRecordSelectionBorder;
            s.ShowSnipCursor = ShowSnipCursor;
            s.ShowRecordCursor = ShowRecordCursor;
            s.TempDirectory = TempDirectory;
            s.ShowAIScanBox = ShowAIScanBox;
            s.EnableAI = EnableAI;
            s.SAM2GridDensity = SAM2GridDensity;
            s.SAM2MaxObjects = SAM2MaxObjects;
            s.SAM2MinObjectSize = SAM2MinObjectSize;
            
            await _settingsService.SaveAsync();
            IsModified = false;
            SetStatus("StatusSaved");
            return true;
        }
        catch (Exception ex)
        {
            SetStatus("ErrorSaving");
            System.Diagnostics.Debug.WriteLine($"Error saving settings: {ex}");
            return false;
        }
    }


    private async Task StartCapture(CaptureMode mode = CaptureMode.Normal)
    {
        if (mode == CaptureMode.Record)
        {
            if (!FfmpegDownloader.IsFFmpegAvailable())
            {
                // Ask user for confirmation before downloading
                var msg = LocalizationService.Instance["FFmpegDownloadConfirm"] ?? "FFmpeg is required for recording. Download now?";
                bool confirmed = false;
                
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    var mainWindow = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
                    if (mainWindow != null)
                    {
                        confirmed = await UpdateDialog.ShowDialog(mainWindow, msg, isUpdateAvailable: true);
                    }
                });

                if (!confirmed) return;

                // Trigger download
                await FfmpegDownloader.EnsureFFmpegAsync();
                
                // If still not ready (user cancelled or error), then return
                if (!FfmpegDownloader.IsFFmpegAvailable())
                    return;
            }
        }

        await SaveSettingsAsync(); // Auto-save on action for now
        RequestCaptureAction?.Invoke(mode);
        SetStatus("StatusSnip");
    }
    
    // Command for explicit save (OK button)
    private async Task SaveAndClose()
    {
        await SaveSettingsAsync();
        SetStatus("StatusSaved");
    }

    private async Task ResetToDefault()
    {
        _isDataLoading = true;
        // Reset to AppSettings initial state
        var defaultSettings = new Models.AppSettings();
        
        RunOnStartup = defaultSettings.RunOnStartup;
        AutoCheckUpdates = defaultSettings.AutoCheckUpdates;
        BorderThickness = defaultSettings.BorderThickness;
        MaskOpacity = defaultSettings.MaskOpacity;
        AutoSave = defaultSettings.AutoSave;
        SnipHotkey = defaultSettings.SnipHotkey;
        CopyHotkey = defaultSettings.CopyHotkey;
        PinHotkey = defaultSettings.PinHotkey;
        RecordHotkey = defaultSettings.RecordHotkey;
        
        RectangleHotkey = defaultSettings.RectangleHotkey;
        EllipseHotkey = defaultSettings.EllipseHotkey;
        ArrowHotkey = defaultSettings.ArrowHotkey;
        LineHotkey = defaultSettings.LineHotkey;
        PenHotkey = defaultSettings.PenHotkey;
        TextHotkey = defaultSettings.TextHotkey;
        MosaicHotkey = defaultSettings.MosaicHotkey;
        BlurHotkey = defaultSettings.BlurHotkey;
        
        UndoHotkey = defaultSettings.UndoHotkey;
        RedoHotkey = defaultSettings.RedoHotkey;
        ClearHotkey = defaultSettings.ClearHotkey;
        SaveHotkey = defaultSettings.SaveHotkey;
        CloseHotkey = defaultSettings.CloseHotkey;
        TogglePlaybackHotkey = defaultSettings.TogglePlaybackHotkey;
        ToggleToolbarHotkey = defaultSettings.ToggleToolbarHotkey;
        SelectionModeHotkey = defaultSettings.SelectionModeHotkey;
        CropModeHotkey = defaultSettings.CropModeHotkey;
        HideSnipPinDecoration = false;
        HideSnipPinBorder = false;
        HideRecordPinDecoration = false;
        HideRecordPinBorder = false;
        HideSnipSelectionDecoration = false;
        HideSnipSelectionBorder = false;
        HideRecordSelectionDecoration = false;
        HideRecordSelectionBorder = false;
        ShowSnipCursor = defaultSettings.ShowSnipCursor;
        ShowRecordCursor = defaultSettings.ShowRecordCursor;
        TempDirectory = defaultSettings.TempDirectory;
        
        if (Color.TryParse(defaultSettings.BorderColorHex, out var color))
            BorderColor = color;
            
        if (Color.TryParse(defaultSettings.ThemeColorHex, out var themeColor))
            ThemeColor = themeColor;
        
        // Services.LocalizationService.Instance.CurrentLanguage = defaultSettings.Language; // Keep current language

        _isDataLoading = false;
        SetStatus("StatusReset");
        IsModified = false;
        await SaveSettingsAsync();
    }

    private void UpdateThemeResources(Color accentColor)
    {
        // BABYMETAL Palette calculation or lookup
        Color deepColor;
        if (accentColor == Color.Parse("#D4AF37")) // Gold
            deepColor = Color.Parse("#8B7500");
        else if (accentColor == Color.Parse("#E0E0E0")) // Silver
            deepColor = Color.Parse("#606060");
        else // Red or custom
            deepColor = Color.Parse("#900000");

        // Update Application resources for global switching
        if (Avalonia.Application.Current != null)
        {
            Avalonia.Application.Current.Resources["ThemeAccentColor"] = accentColor;
            Avalonia.Application.Current.Resources["ThemeDeepColor"] = deepColor;
        }
    }

    public async Task CheckForUpdates() => await CheckForUpdates(false);

    private async Task CheckForUpdates(bool silent)
    {
        if (!silent) SetStatus("CheckingUpdate");
        var release = await UpdateService.CheckForUpdateAsync();
        
        if (release != null)
        {
            SetStatus("StatusReady");
            var msg = string.Format(LocalizationService.Instance["UpdateFound"], release.TagName);
            
            bool? result = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var mainWindow = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
                if (mainWindow == null) return false;
                return await UpdateDialog.ShowDialog(mainWindow, msg, isUpdateAvailable: true);
            });

            if (result == true)
            {
                var zipPath = await UpdateService.DownloadUpdateAsync(release);
                if (!string.IsNullOrEmpty(zipPath))
                {
                    var readyMsg = LocalizationService.Instance["UpdateReady"];
                    bool? readyResult = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        var mainWindow = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
                        if (mainWindow == null) return false;
                        return await UpdateDialog.ShowDialog(mainWindow, readyMsg, isUpdateAvailable: true);
                    });

                    if (readyResult == true)
                    {
                        UpdateService.ApplyUpdate(zipPath);
                    }
                    else
                    {
                        // Cleanup if user cancels
                        try
                        {
                            var tempDir = Path.GetDirectoryName(zipPath);
                            if (!string.IsNullOrEmpty(tempDir) && Directory.Exists(tempDir))
                                Directory.Delete(tempDir, true);
                        }
                        catch { /* Ignore error */ }
                    }
                }
                else
                {
                    var errMsg = string.Format(LocalizationService.Instance["UpdateError"], "Download failed");
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () => {
                        var mainWindow = (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
                        if (mainWindow != null) await UpdateDialog.ShowDialog(mainWindow, errMsg, isUpdateAvailable: false);
                    });
                }
            }
        }
        else
        {
            if (!silent)
            {
                SetStatus("StatusReady");
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () => {
                    var mainWindow = (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
                    if (mainWindow != null) await UpdateDialog.ShowDialog(mainWindow, LocalizationService.Instance["NoUpdateFound"], isUpdateAvailable: false);
                });
            }
        }
        }

    private GimmeCapture.Views.Shared.DownloadProgressWindow? _progressWindow;

    private void ShowProgressWindow()
    {
        if (_progressWindow != null) return;

        _progressWindow = new GimmeCapture.Views.Shared.DownloadProgressWindow
        {
            DataContext = this
        };
        _progressWindow.Show();
    }

    private void CloseProgressWindow()
    {
        if (_progressWindow != null)
        {
            _progressWindow.Close();
            _progressWindow = null;
        }
    }

    private void OpenProjectUrl()
    {
        try
        {
            var url = "https://github.com/HouseAlwaysWin/GimmeCapture";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to open project URL: {ex.Message}");
        }
    }

    private void InitializeModules()
    {
        Modules.Clear();
        
        // FFmpeg Module
        var ffmpeg = new ModuleItem("FFmpeg", "ModuleFFmpegDescription")
        {
            IsInstalled = FfmpegDownloader.IsFFmpegAvailable(),
            InstallCommand = ReactiveCommand.CreateFromTask(() => InstallModuleAsync("FFmpeg")),
            CancelCommand = ReactiveCommand.CreateFromTask(() => CancelModuleAsync("FFmpeg")),
            RemoveCommand = ReactiveCommand.CreateFromTask(() => RemoveModuleAsync("FFmpeg"))
        };
        FfmpegDownloader.WhenAnyValue(x => x.DownloadProgress)
            .Subscribe(p => ffmpeg.Progress = p);
            
        // Subscribe to Queue Status for Pending state
        ResourceQueue.ObserveStatus("FFmpeg")
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(status => 
            {
                ffmpeg.IsPending = status == QueueItemStatus.Pending;
                ffmpeg.IsProcessing = status == QueueItemStatus.Downloading;
                if (status == QueueItemStatus.Completed) ffmpeg.IsInstalled = FfmpegDownloader.IsFFmpegAvailable();
            });

        // AI Core Module
        var aiCore = new ModuleItem("AI Core", "ModuleAICoreDescription")
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

        ResourceQueue.ObserveStatus("SAM2")
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(status => 
            {
                sam2.IsPending = status == QueueItemStatus.Pending;
                sam2.IsProcessing = status == QueueItemStatus.Downloading;
                if (status == QueueItemStatus.Completed) sam2.IsInstalled = AIResourceService.IsSAM2Ready(_settingsService.Settings.SelectedSAM2Variant);
            });

        // Linked progress for both (Route progress to whoever is active)
        AIResourceService.WhenAnyValue(x => x.DownloadProgress)
            .Subscribe(p => {
                if (aiCore.IsProcessing) aiCore.Progress = p;
                if (sam2.IsProcessing) sam2.Progress = p;
            });

        ffmpeg.UpdateDescription();
        aiCore.UpdateDescription();
        sam2.UpdateDescription();

        Modules.Add(ffmpeg);
        Modules.Add(aiCore);
        Modules.Add(sam2);
    }

    private async Task InstallModuleAsync(string type)
    {
        if (type == "FFmpeg")
        {
            await ResourceQueue.EnqueueAsync("FFmpeg", (ct) => FfmpegDownloader.EnsureFFmpegAsync(ct));
        }
        else if (type == "AICore")
        {
            await ResourceQueue.EnqueueAsync("AICore", (ct) => AIResourceService.EnsureAICoreAsync(ct));
        }
        else if (type == "SAM2")
        {
             // Dependency handled internally by EnsureSAM2Async
             var variant = _settingsService.Settings.SelectedSAM2Variant;
             await ResourceQueue.EnqueueAsync("SAM2", (ct) => AIResourceService.EnsureSAM2Async(variant, ct));
        }
    }

    private async Task CancelModuleAsync(string type)
    {
        if (ConfirmAction != null)
        {
            var result = await ConfirmAction(
                LocalizationService.Instance["UpdateCheckTitle"], 
                LocalizationService.Instance["ConfirmCancelDownload"]);

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
                LocalizationService.Instance["ConfirmRemoveModule"]) ?? Task.FromResult(false));

            if (!result) return;

            if (type == "FFmpeg")
            {
                FfmpegDownloader.RemoveFFmpeg();
            }
            else if (type == "AICore")
            {
                AIResourceService.RemoveAICoreResources(); 
            }
            else if (type == "SAM2")
            {
                 AIResourceService.RemoveSAM2Resources(_settingsService.Settings.SelectedSAM2Variant);
            }
            
            foreach (var m in Modules)
            {
                if (m.Name == "FFmpeg") m.IsInstalled = FfmpegDownloader.IsFFmpegAvailable();
                if (m.Name == "AI Core") m.IsInstalled = AIResourceService.IsAICoreReady();
                if (m.Name == "SAM2 Model") m.IsInstalled = AIResourceService.IsSAM2Ready(_settingsService.Settings.SelectedSAM2Variant);
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

        // --- Variant Support ---
        public bool HasVariants { get; init; } = false;
        public ObservableCollection<string>? Variants { get; init; }

        private string _selectedVariant = "";
        public string SelectedVariant
        {
            get => _selectedVariant;
            set => this.RaiseAndSetIfChanged(ref _selectedVariant, value);
        }
        // -----------------------

        public ICommand InstallCommand { get; init; } = null!;
        public ICommand CancelCommand { get; set; } = null!;
        public ICommand RemoveCommand { get; init; } = null!;

        private System.Windows.Input.ICommand _mainActionCommand = null!;
        public System.Windows.Input.ICommand MainActionCommand
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
            
            // Subscribe to language changes to update Description dynamically
            LocalizationService.Instance.WhenAnyValue(x => x.CurrentLanguage)
                .Subscribe(_ => UpdateDescription());
            UpdateDescription();
        }
        
        public void UpdateDescription()
        {
            // Update Description and Status Text
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
                    ? LocalizationService.Instance["Remove"] 
                    : LocalizationService.Instance["Install"];
                    
                 MainActionCommand = IsInstalled ? RemoveCommand : InstallCommand;
            }
        }
    }
}
