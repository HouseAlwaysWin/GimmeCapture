using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GimmeCapture.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _statusText = "Ready to Capture";

    public System.Action? RequestCaptureAction { get; set; }

    [RelayCommand]
    private void StartCapture()
    {
        RequestCaptureAction?.Invoke();
        StatusText = "Snip Window Opened";
    }
}
