using ReactiveUI;
using Avalonia.Media;
using System;
using System.Threading.Tasks;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.IO;
using GimmeCapture.Services;

namespace GimmeCapture.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    private string _statusText = "";
    public string StatusText
    {
        get => _statusText;
        set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    private string _currentStatusKey = "StatusReady";

    public void SetStatus(string key)
    {
        _currentStatusKey = key;
        StatusText = Services.LocalizationService.Instance[key];
    }

    public Action<CaptureMode>? RequestCaptureAction { get; set; }
    public Func<Task<string?>>? PickFolderAction { get; set; }
    
    private readonly Services.AppSettingsService _settingsService;
    public Services.GlobalHotkeyService HotkeyService { get; } = new();

    // Hotkey IDs
    private const int ID_SNIP = 9000;
    private const int ID_COPY = 9001;
    private const int ID_PIN = 9002;
    private const int ID_RECORD = 9003;

    public enum CaptureMode { Normal, Copy, Pin, Record }

    public FFmpegDownloaderService FfmpegDownloader { get; } = new();
    public RecordingService RecordingService { get; }
    public UpdateService UpdateService { get; }

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
    
    public Color[] SettingsColors { get; } = new[]
    {
        Color.Parse("#D4AF37"), // Gold
        Color.Parse("#E0E0E0"), // Silver
        Color.Parse("#E60012")  // Red
    };

    public MainWindowViewModel()
    {
        _settingsService = new Services.AppSettingsService();
        RecordingService = new RecordingService(FfmpegDownloader);
        UpdateService = new UpdateService("0.1.0"); // Test Version
        
        // Sync ViewModel with Service using ReactiveUI
        // When Service language changes, notify ViewModel properties to update
        Services.LocalizationService.Instance
            .WhenAnyValue(x => x.CurrentLanguage)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => 
            {
                this.RaisePropertyChanged(nameof(SelectedLanguageOption));
                // Update Status Text on Language Change
                StatusText = Services.LocalizationService.Instance[_currentStatusKey];
            });

        SetStatus("StatusReady");

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
                    StatusText = $"正在下載必要組件... {progress:F0}%";
                }
                else if (progress >= 100)
                {
                     if (StatusText.StartsWith("正在下載"))
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
                    StatusText = string.Format(Services.LocalizationService.Instance["UpdateDownloading"], (int)progress);
                }
            });

        // Fire and forget load, in real app use async initialization
        Task.Run(async () => await LoadSettingsAsync());
    }

    // Language Selection
    public class LanguageOption
    {
        public string Name { get; set; }
        public Services.Language Value { get; set; }
    }

    public LanguageOption[] AvailableLanguages { get; } = new[]
    {
        new LanguageOption { Name = "English (US)", Value = Services.Language.English },
        new LanguageOption { Name = "繁體中文 (台灣)", Value = Services.Language.Chinese },
        new LanguageOption { Name = "日本語 (日本)", Value = Services.Language.Japanese }
    };

    public LanguageOption SelectedLanguageOption
    {
        get => AvailableLanguages.FirstOrDefault(x => x.Value == Services.LocalizationService.Instance.CurrentLanguage) ?? AvailableLanguages[0];
        set
        {
            if (value != null && Services.LocalizationService.Instance.CurrentLanguage != value.Value)
            {
                Services.LocalizationService.Instance.CurrentLanguage = value.Value;
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
            }
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

    private string _recordFormat = "mp4";
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

    private bool _showPinDecoration = true;
    public bool ShowPinDecoration
    {
        get => _showPinDecoration;
        set => this.RaiseAndSetIfChanged(ref _showPinDecoration, value);
    }

    private bool _hidePinBorder = false;
    public bool HidePinBorder
    {
        get => _hidePinBorder;
        set => this.RaiseAndSetIfChanged(ref _hidePinBorder, value);
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

    public async Task LoadSettingsAsync()
    {
        await _settingsService.LoadAsync();
        var s = _settingsService.Settings;
        
        // Map settings to properties (ViewModel -> View)
        RunOnStartup = s.RunOnStartup;
        AutoCheckUpdates = s.AutoCheckUpdates;
        BorderThickness = s.BorderThickness;
        MaskOpacity = s.MaskOpacity;
        AutoSave = s.AutoSave;
        SnipHotkey = s.SnipHotkey;
        CopyHotkey = s.CopyHotkey;
        PinHotkey = s.PinHotkey;
        ShowPinDecoration = s.ShowPinDecoration;
        HidePinBorder = s.HidePinBorder;
        ShowSnipCursor = s.ShowSnipCursor;
        ShowRecordCursor = s.ShowRecordCursor;
        
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
            VideoSaveDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "GimmeCapture");
            if (!Directory.Exists(VideoSaveDirectory)) Directory.CreateDirectory(VideoSaveDirectory);
        }

        SaveDirectory = s.SaveDirectory;
        if (string.IsNullOrEmpty(SaveDirectory))
        {
            SaveDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "GimmeCapture");
            if (!Directory.Exists(SaveDirectory)) Directory.CreateDirectory(SaveDirectory);
        }

        RecordFormat = s.RecordFormat;
        UseFixedRecordPath = s.UseFixedRecordPath;
        RecordHotkey = s.RecordHotkey;

        // Load Language
        Services.LocalizationService.Instance.CurrentLanguage = s.Language;

        // Register initial hotkeys
        Avalonia.Threading.Dispatcher.UIThread.Post(() => {
            HotkeyService.Register(ID_SNIP, SnipHotkey);
            HotkeyService.Register(ID_RECORD, RecordHotkey);
        });

        // Initialize FFmpeg Download if missing either component
        if (!FfmpegDownloader.IsFFmpegAvailable() || !FfmpegDownloader.IsFFplayAvailable())
        {
            await FfmpegDownloader.EnsureFFmpegAsync();
        }
    }

    public async Task SaveSettingsAsync()
    {
        var s = _settingsService.Settings;
        
        // Map properties to settings (User Input -> Model)
        s.RunOnStartup = RunOnStartup;
        s.AutoCheckUpdates = AutoCheckUpdates;
        s.BorderThickness = BorderThickness;
        s.MaskOpacity = MaskOpacity;
        s.AutoSave = AutoSave;
        s.SaveDirectory = SaveDirectory;
        s.SnipHotkey = SnipHotkey;
        s.CopyHotkey = CopyHotkey;
        s.PinHotkey = PinHotkey;
        s.RecordHotkey = RecordHotkey;
        s.BorderColorHex = BorderColor.ToString();
        s.ThemeColorHex = ThemeColor.ToString();
        s.Language = Services.LocalizationService.Instance.CurrentLanguage;
        s.VideoSaveDirectory = VideoSaveDirectory;
        s.RecordFormat = RecordFormat;
        s.UseFixedRecordPath = UseFixedRecordPath;
        s.ShowPinDecoration = ShowPinDecoration;
        s.HidePinBorder = HidePinBorder;
        s.ShowSnipCursor = ShowSnipCursor;
        s.ShowRecordCursor = ShowRecordCursor;
        
        await _settingsService.SaveAsync();
    }

    private async Task StartCapture(CaptureMode mode = CaptureMode.Normal)
    {
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
        ShowPinDecoration = defaultSettings.ShowPinDecoration;
        HidePinBorder = defaultSettings.HidePinBorder;
        ShowSnipCursor = defaultSettings.ShowSnipCursor;
        ShowRecordCursor = defaultSettings.ShowRecordCursor;
        
        if (Color.TryParse(defaultSettings.BorderColorHex, out var color))
            BorderColor = color;
            
        if (Color.TryParse(defaultSettings.ThemeColorHex, out var themeColor))
            ThemeColor = themeColor;
        
        // Services.LocalizationService.Instance.CurrentLanguage = defaultSettings.Language; // Keep current language

        SetStatus("StatusReset");
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

    private async Task CheckForUpdates()
    {
        SetStatus("CheckingUpdate");
        var release = await UpdateService.CheckForUpdateAsync();
        
        if (release != null)
        {
            SetStatus("StatusReady");
            var msg = string.Format(Services.LocalizationService.Instance["UpdateFound"], release.TagName);
            var result = System.Windows.Forms.MessageBox.Show(msg, 
                Services.LocalizationService.Instance["UpdateCheckTitle"], 
                System.Windows.Forms.MessageBoxButtons.YesNo, 
                System.Windows.Forms.MessageBoxIcon.Information);

            if (result == System.Windows.Forms.DialogResult.Yes)
            {
                var zipPath = await UpdateService.DownloadUpdateAsync(release);
                if (!string.IsNullOrEmpty(zipPath))
                {
                    var readyMsg = Services.LocalizationService.Instance["UpdateReady"];
                    var readyResult = System.Windows.Forms.MessageBox.Show(readyMsg, 
                        Services.LocalizationService.Instance["UpdateCheckTitle"], 
                        System.Windows.Forms.MessageBoxButtons.YesNo, 
                        System.Windows.Forms.MessageBoxIcon.Question);

                    if (readyResult == System.Windows.Forms.DialogResult.Yes)
                    {
                        UpdateService.ApplyUpdate(zipPath);
                    }
                }
                else
                {
                    var errMsg = string.Format(Services.LocalizationService.Instance["UpdateError"], "Download failed");
                    System.Windows.Forms.MessageBox.Show(errMsg, Services.LocalizationService.Instance["UpdateCheckTitle"], System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
                }
            }
        }
        else
        {
            SetStatus("StatusReady");
            System.Windows.Forms.MessageBox.Show(Services.LocalizationService.Instance["NoUpdateFound"], 
                Services.LocalizationService.Instance["UpdateCheckTitle"], 
                System.Windows.Forms.MessageBoxButtons.OK, 
                System.Windows.Forms.MessageBoxIcon.Information);
        }
    }
}
