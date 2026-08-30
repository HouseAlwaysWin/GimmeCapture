using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using GimmeCapture.Models;
using GimmeCapture.Services.Abstractions;

namespace GimmeCapture.Views.Main;

internal sealed class NoOpDownloadWindowService : IDownloadWindowService
{
    public void Update(Window owner, object? dataContext, bool isProcessing) { }

    public void Close() { }
}

internal sealed class NoOpSnipWindowFactory : ISnipWindowFactory
{
    public void Open(object mainViewModel, CaptureMode mode) { }

    public object? GetActiveViewModel() => null;
}

internal sealed class NoOpToastService : IToastService
{
    public void Show(
        string message,
        GimmeCapture.ViewModels.Main.MainWindowViewModel.ToastSeverity severity,
        Avalonia.Media.Imaging.Bitmap? preview = null)
    {
        // Ownership still transfers even when nothing is shown, so a design-time toast can't leak the thumbnail.
        preview?.Dispose();
    }
}

internal sealed class NoOpScreenLayoutService : IScreenLayoutService
{
    public IReadOnlyList<Rect> BuildRelativeScreenBounds(IReadOnlyList<PixelRect> screenBounds, PixelPoint windowPosition, double renderScaling) => [];

    public Rect? TryGetActiveScreenBounds(Screens screens, Window window, Point logicalWindowPoint, double renderScaling) => null;

    public bool TryGetUnifiedDesktopPlacement(IReadOnlyList<PixelRect> screenBounds, double unifiedScaling, out PixelPoint windowPosition, out Size logicalSize)
    {
        windowPosition = default;
        logicalSize = default;
        return false;
    }
}

internal sealed class NoOpWindowLayerService : IWindowLayerService
{
    public IReadOnlyList<Window> LowerTopmostWindowsOfType<TWindow>() where TWindow : Window => [];

    public void RestoreTopmostWindows(IEnumerable<Window> windows) { }
}
