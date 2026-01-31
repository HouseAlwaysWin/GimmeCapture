using ReactiveUI;
using Avalonia.Media;
using System.Threading.Tasks;
using System.Reactive;

namespace GimmeCapture.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    private string _statusText = "Ready to Capture";
    public string StatusText
    {
        get => _statusText;
        set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    public System.Action<CaptureMode>? RequestCaptureAction { get; set; }
    
    private readonly Services.AppSettingsService _settingsService;
    public Services.GlobalHotkeyService HotkeyService { get; } = new();

    // Hotkey IDs
    private const int ID_SNIP = 9000;
    private const int ID_COPY = 9001;
    private const int ID_PIN = 9002;

    public enum CaptureMode { Normal, Copy, Pin }

    // Commands
    public ReactiveCommand<CaptureMode, Unit> StartCaptureCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveAndCloseCommand { get; }

    public MainWindowViewModel()
    {
        _settingsService = new Services.AppSettingsService();
        
        StartCaptureCommand = ReactiveCommand.CreateFromTask<CaptureMode>(StartCapture);
        SaveAndCloseCommand = ReactiveCommand.CreateFromTask(SaveAndClose);
        
        // Setup Hotkey Action
        HotkeyService.OnHotkeyPressed = (id) => 
        {
            if (id == ID_SNIP)
            {
                // Must run on UI thread if it involves UI updates
                Avalonia.Threading.Dispatcher.UIThread.Post(() => StartCaptureCommand.Execute(CaptureMode.Normal));
            }
        };

        // Fire and forget load, in real app use async initialization
        Task.Run(async () => await LoadSettingsAsync());
    }

    // General Settings
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

    // Output Settings
    private bool _autoSave;
    public bool AutoSave
    {
        get => _autoSave;
        set => this.RaiseAndSetIfChanged(ref _autoSave, value);
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
        
        if (Color.TryParse(s.BorderColorHex, out var color))
        {
            BorderColor = color;
        }

        // Register initial hotkey (ensure UI thread or safe context? Service handles P/Invoke which is thread-tied usually)
        // Ideally we register on UI thread, but LoadSettingsAsync is background here. 
        // We will dispatch to UI thread to be safe as the handle belongs to UI thread.
        Avalonia.Threading.Dispatcher.UIThread.Post(() => {
            HotkeyService.Register(ID_SNIP, SnipHotkey);
        });
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
        s.SnipHotkey = SnipHotkey;
        s.CopyHotkey = CopyHotkey;
        s.PinHotkey = PinHotkey;
        s.BorderColorHex = BorderColor.ToString();
        
        await _settingsService.SaveAsync();
    }

    private async Task StartCapture(CaptureMode mode = CaptureMode.Normal)
    {
        await SaveSettingsAsync(); // Auto-save on action for now
        RequestCaptureAction?.Invoke(mode);
        StatusText = $"Snip Window Opened ({mode})";
    }
    
    // Command for explicit save (OK button)
    private async Task SaveAndClose()
    {
        await SaveSettingsAsync();
        // Window close logic if needed, or just toast
        StatusText = "Settings Saved";
    }
}
