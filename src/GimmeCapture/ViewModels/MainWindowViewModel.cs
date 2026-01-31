using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Media;

namespace GimmeCapture.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _statusText = "Ready to Capture";

    public System.Action? RequestCaptureAction { get; set; }

    // General Settings
    [ObservableProperty]
    private bool _runOnStartup = false;

    [ObservableProperty]
    private bool _autoCheckUpdates = true;

    // Snip Settings
    [ObservableProperty]
    private double _borderThickness = 2.0;

    [ObservableProperty]
    private double _maskOpacity = 0.5;
    
    [ObservableProperty]
    private Color _borderColor = Color.Parse("#E60012"); // AccentRed

    // Output Settings
    [ObservableProperty]
    private bool _autoSave = false;

    [RelayCommand]
    private void StartCapture()
    {
        RequestCaptureAction?.Invoke();
        StatusText = "Snip Window Opened";
    }
}
