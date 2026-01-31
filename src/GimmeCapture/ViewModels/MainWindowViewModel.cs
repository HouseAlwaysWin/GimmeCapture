using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Media;
using System.Threading.Tasks;

namespace GimmeCapture.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _statusText = "Ready to Capture";

    public System.Action? RequestCaptureAction { get; set; }

    private readonly Services.AppSettingsService _settingsService;
    public Services.GlobalHotkeyService HotkeyService { get; } = new();

    public MainWindowViewModel()
    {
        _settingsService = new Services.AppSettingsService();
        
        // Setup Hotkey Action
        HotkeyService.OnHotkeyPressed = () => 
        {
            // Must run on UI thread if it involves UI updates
            Avalonia.Threading.Dispatcher.UIThread.Post(() => StartCaptureCommand.Execute(null));
        };

        // Fire and forget load, in real app use async initialization
        Task.Run(async () => await LoadSettingsAsync());
    }

    // General Settings
    [ObservableProperty]
    private bool _runOnStartup;

    [ObservableProperty]
    private bool _autoCheckUpdates;

    // Snip Settings
    [ObservableProperty]
    private double _borderThickness;

    [ObservableProperty]
    private double _maskOpacity;
    
    [ObservableProperty]
    private Color _borderColor; 

    // Output Settings
    [ObservableProperty]
    private bool _autoSave;
    
    // Control Settings
    [ObservableProperty]
    private string _snipHotkey = "F1";

    partial void OnSnipHotkeyChanged(string value)
    {
        HotkeyService.Register(value);
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
        
        if (Color.TryParse(s.BorderColorHex, out var color))
        {
            BorderColor = color;
        }

        // Register initial hotkey (ensure UI thread or safe context? Service handles P/Invoke which is thread-tied usually)
        // Ideally we register on UI thread, but LoadSettingsAsync is background here. 
        // We will dispatch to UI thread to be safe as the handle belongs to UI thread.
        Avalonia.Threading.Dispatcher.UIThread.Post(() => HotkeyService.Register(SnipHotkey));
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
        s.BorderColorHex = BorderColor.ToString();
        
        await _settingsService.SaveAsync();
    }

    [RelayCommand]
    private async Task StartCapture()
    {
        await SaveSettingsAsync(); // Auto-save on action for now
        RequestCaptureAction?.Invoke();
        StatusText = "Snip Window Opened";
    }
    
    // Command for explicit save (OK button)
    [RelayCommand]
    private async Task SaveAndClose()
    {
        await SaveSettingsAsync();
        // Window close logic if needed, or just toast
        StatusText = "Settings Saved";
    }
}
