using GimmeCapture.ViewModels.Main;

namespace GimmeCapture.Services.Abstractions;

/// <summary>
/// Shows brief, prominent floating notifications (toasts) for operation results and errors.
/// Implementations render a topmost, capture-excluded window so toasts are visible even when
/// the main window is minimized to the tray and never appear inside a screenshot/recording.
/// </summary>
public interface IToastService
{
    void Show(string message, MainWindowViewModel.ToastSeverity severity);
}
