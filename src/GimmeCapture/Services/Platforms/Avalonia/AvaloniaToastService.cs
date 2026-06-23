using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Threading;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.ViewModels.Main;
using GimmeCapture.Views.Main;

namespace GimmeCapture.Services.Platforms.Desktop;

// Shows toasts as topmost, capture-excluded ToastWindows stacked at the bottom-right of the
// screen. Newest appears at the bottom; older ones shift up. Self-reflows as toasts open/close.
public sealed class AvaloniaToastService : IToastService
{
    private const int MaxVisible = 4;
    private readonly List<ToastWindow> _toasts = new();

    public void Show(string message, MainWindowViewModel.ToastSeverity severity)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Show(message, severity));
            return;
        }

        // Cap the number of simultaneous toasts; drop the oldest.
        while (_toasts.Count >= MaxVisible)
        {
            _toasts[0].Close();
        }

        var toast = new ToastWindow(message, severity);
        toast.ReadyForLayout += (_, _) => Reflow();
        toast.Closed += (_, _) =>
        {
            _toasts.Remove(toast);
            Reflow();
        };

        _toasts.Add(toast);
        toast.Show();
    }

    private void Reflow()
    {
        if (_toasts.Count == 0)
        {
            return;
        }

        var screen = _toasts[0].Screens?.Primary
            ?? (_toasts[0].Screens is { All.Count: > 0 } s ? s.All[0] : null);
        if (screen == null)
        {
            return;
        }

        var wa = screen.WorkingArea;
        double scale = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
        int margin = (int)(16 * scale);
        int spacing = (int)(8 * scale);

        // Stack upward from the bottom-right; newest (last added) sits lowest.
        int y = wa.Bottom - margin;
        for (int i = _toasts.Count - 1; i >= 0; i--)
        {
            var toast = _toasts[i];
            if (toast.Bounds.Width <= 0 || toast.Bounds.Height <= 0)
            {
                continue; // not measured yet; will reflow on its ReadyForLayout
            }

            int w = (int)(toast.Bounds.Width * scale);
            int h = (int)(toast.Bounds.Height * scale);
            int x = wa.Right - margin - w;
            y -= h;
            toast.Position = new PixelPoint(Math.Max(wa.X, x), Math.Max(wa.Y, y));
            y -= spacing;
        }
    }
}
