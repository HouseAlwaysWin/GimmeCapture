using Avalonia.Media.Imaging;
using GimmeCapture.ViewModels.Main;

namespace GimmeCapture.Services.Abstractions;

/// <summary>
/// Shows brief, prominent floating notifications (toasts) for operation results and errors.
/// Implementations render a topmost, capture-excluded window so toasts are visible even when
/// the main window is minimized to the tray and never appear inside a screenshot/recording.
/// </summary>
public interface IToastService
{
    /// <param name="preview">
    /// Optional thumbnail shown beside the message. A "copied" confirmation that names no image cannot tell you
    /// WHICH image you are about to paste, which is the whole question after a copy — so the copy confirmation
    /// carries a preview of what actually landed on the clipboard. The toast takes ownership and disposes it.
    /// </param>
    void Show(string message, MainWindowViewModel.ToastSeverity severity, Bitmap? preview = null);
}
