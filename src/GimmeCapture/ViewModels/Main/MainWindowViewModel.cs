using ReactiveUI;
using Avalonia;
using Avalonia.Media;
using System;
using System.Threading.Tasks;
using Avalonia.Controls.ApplicationLifetimes;
using GimmeCapture.Models;
using GimmeCapture.Views.Dialogs;
using GimmeCapture.Views.Main;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.IO;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core;
using GimmeCapture.Services.Platforms.Windows;

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
    
    private readonly AppSettingsService _settingsService;
    public WindowsGlobalHotkeyService HotkeyService { get; } = new();

    // Hotkey IDs
    private const int ID_SNIP = 9000;
    private const int ID_COPY = 9001;
    private const int ID_PIN = 9002;
    private const int ID_RECORD = 9003;

    public enum CaptureMode { Normal, Copy, Pin, Record }

    public FFmpegDownloaderService FfmpegDownloader { get; } = new();
    public RecordingService RecordingService { get; }
    public UpdateService UpdateService { get; }
    public AIResourceService AIResourceService { get; }
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
        RecordingService = new RecordingService(FfmpegDownloader);
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
        
        /* 
        // --- AI Download Window Management ---
        AIResourceService.WhenAnyValue(x => x.IsDownloading)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(isDownloading => 
            {
                if (isDownloading)
                {
                    AIProgressText = string.Format(LocalizationService.Instance["ComponentDownloadingProgress"], 0);
                    ShowProgressWindow();
                }
                else
                {
                    CloseProgressWindow();
                }
            });

        AIResourceService.WhenAnyValue(x => x.DownloadProgress)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(progress =>
            {
                if (AIResourceService.IsDownloading)
                {
                    AIProgressText = string.Format(LocalizationService.Instance["ComponentDownloadingProgress"], (int)progress);
                }
            });
        */
        
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

        // Progress Feedback
        FfmpegDownloader.WhenAnyValue(x => x.IsDownloading, x => x.DownloadProgress)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(x => {
                var (isDownloading, progress) = x;
                if (isDownloading)
                {
                    StatusText = string.Format(LocalizationService.Instance["ComponentDownloadingProgress"], (int)progress);
                }
                else if (progress >= 100)
                {
                     if (StatusText.Contains(LocalizationService.Instance["ComponentDownloadingProgress"].Split('.')[0]))
                        SetStatus("StatusReady");
                }
            });

        // Update Progress Feedback
        UpdateService.WhenAnyValue(x => x.IsDownloading, x => x.DownloadProgress)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(x => {
                var (isDownloading, progress) = x;
                if (isDownloading)
                {
                    StatusText = string.Format(LocalizationService.Instance["UpdateDownloading"], (int)progress);
                }
            });

        // Track changes AFTER loading
        this.PropertyChanged += (s, e) =>
        {
            if (!_isDataLoading && 
                e.PropertyName != nameof(StatusText) && 
                e.PropertyName != nameof(IsModified) &&
                e.PropertyName != nameof(SelectedLanguageOption))
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
               ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AI") 
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
                LocalizationService.Instance.CurrentLanguage = value.Value;
                this.RaisePropertyChanged();
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
        }
    }

    private string _copyHotkey = "Ctrl+C";
    public string CopyHotkey
    {
        get => _copyHotkey;
        set => this.RaiseAndSetIfChanged(ref _copyHotkey, value);
    }

    private string _pinHotkey = "F3";
    public string PinHotkey
    {
        get => _pinHotkey;
        set => this.RaiseAndSetIfChanged(ref _pinHotkey, value);
    }

    private string _recordHotkey = "F2";
    public string RecordHotkey
    {
        get => _recordHotkey;
        set
        {
            this.RaiseAndSetIfChanged(ref _recordHotkey, value);
            HotkeyService.Register(ID_RECORD, value);
        }
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
        if (string.IsNullOrEmpty(TempDirectory))
        {
            TempDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Temp");
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
            VideoSaveDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Recordings");
            try { if (!Directory.Exists(VideoSaveDirectory)) Directory.CreateDirectory(VideoSaveDirectory); } catch { }
        }

        SaveDirectory = s.SaveDirectory;
        if (string.IsNullOrEmpty(SaveDirectory))
        {
            SaveDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Captures");
            try { if (!Directory.Exists(SaveDirectory)) Directory.CreateDirectory(SaveDirectory); } catch { }
        }

        RecordFormat = s.RecordFormat;
        UseFixedRecordPath = s.UseFixedRecordPath;
        RecordHotkey = s.RecordHotkey;

        // Load Language
        LocalizationService.Instance.CurrentLanguage = s.Language;

        // Ensure registry is in sync with setting
        StartupService.SetStartup(s.RunOnStartup);

        // Register initial hotkeys
        Avalonia.Threading.Dispatcher.UIThread.Post(() => {
            HotkeyService.Register(ID_SNIP, SnipHotkey);
            HotkeyService.Register(ID_RECORD, RecordHotkey);
        });

        if (AutoCheckUpdates)
        {
            _ = Task.Run(async () => await CheckForUpdates(silent: true));
        }

        _isDataLoading = false;

        // Ensure FFmpeg/Updates happen AFTER _isDataLoading is false (or just handle them separately)
        _ = Task.Run(async () => 
        {
            if (!FfmpegDownloader.IsFFmpegAvailable() || !FfmpegDownloader.IsFFplayAvailable())
            {
                await FfmpegDownloader.EnsureFFmpegAsync();
            }
        });
    }

    public async Task<bool> SaveSettingsAsync()
    {
        // Don't save if we haven't even finished loading
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
            s.BorderColorHex = BorderColor.ToString();
            s.ThemeColorHex = ThemeColor.ToString();
            s.Language = LocalizationService.Instance.CurrentLanguage;
            s.VideoSaveDirectory = VideoSaveDirectory;
            s.RecordFormat = RecordFormat;
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
            
            await _settingsService.SaveAsync();
            IsModified = false;

            // Update Windows startup registry
            StartupService.SetStartup(s.RunOnStartup);
            return true;
        }
        catch (Exception ex)
        {
            SetStatus("StatusError"); // We might need an error key
            System.Diagnostics.Debug.WriteLine($"Error in SaveSettingsAsync: {ex.Message}");
            return false;
        }
    }

    private async Task StartCapture(CaptureMode mode = CaptureMode.Normal)
    {
        if (mode == CaptureMode.Record)
        {
            if (!FfmpegDownloader.IsFFmpegAvailable())
            {
                var msg = LocalizationService.Instance["FFmpegNotReady"];
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    var mainWindow = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
                    if (mainWindow != null) await UpdateDialog.ShowDialog(mainWindow, msg, isUpdateAvailable: false);
                });
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
}
