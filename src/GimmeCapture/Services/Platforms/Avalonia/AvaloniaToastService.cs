using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Interop;
using GimmeCapture.ViewModels.Main;
using GimmeCapture.Views.Main;

namespace GimmeCapture.Services.Platforms.Desktop;

// Shows toasts as topmost, capture-excluded ToastWindows stacked at the bottom-right of the
// screen. Newest appears at the bottom; older ones shift up. Self-reflows as toasts open/close.
public sealed class AvaloniaToastService : IToastService
{
    private const int MaxVisible = 4;
    private readonly List<ToastWindow> _toasts = new();

    // Where the current stack of toasts lives, fixed when the stack opens. Null means "no stack, or nowhere
    // better than the primary screen".
    private PixelPoint? _stackAnchor;

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

        if (_toasts.Count == 0)
        {
            // Anchor the stack to the pointer when it opens, and keep that anchor for the stack's whole life:
            // a "copied to clipboard" confirmation is only a confirmation if it appears where the user is
            // looking, and re-resolving on every reflow would make a stack hop monitors mid-read.
            _stackAnchor = Win32Helpers.TryGetCursorPosition();
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
            _stackAnchor = null;
            return;
        }

        var screen = ResolveStackScreen(_toasts[0].Screens);
        if (screen == null)
        {
            return;
        }

        var wa = screen.WorkingArea;
        double scale = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
        int margin = (int)(16 * scale);
        int spacing = (int)(8 * scale);

        // Stack downward from the top-center; oldest at the top.
        int y = wa.Y + margin;
        for (int i = 0; i < _toasts.Count; i++)
        {
            var toast = _toasts[i];
            if (toast.Bounds.Width <= 0 || toast.Bounds.Height <= 0)
            {
                continue; // not measured yet; will reflow on its ReadyForLayout
            }

            int w = (int)(toast.Bounds.Width * scale);
            int h = (int)(toast.Bounds.Height * scale);
            int x = wa.X + ((wa.Width - w) / 2);
            toast.Position = new PixelPoint(Math.Max(wa.X, x), Math.Max(wa.Y, y));
            y += h + spacing;
        }
    }

    /// <summary>
    /// The screen the stack belongs on: the one holding <see cref="_stackAnchor"/>, falling back to the primary.
    /// Toasts used to be pinned to the primary screen unconditionally, which on a multi-monitor desktop put every
    /// confirmation on a monitor the user might not be looking at — a notification nobody sees is not one.
    /// </summary>
    private Screen? ResolveStackScreen(Screens? screens)
    {
        if (screens == null)
        {
            return null;
        }

        if (_stackAnchor is { } anchor && screens.ScreenFromPoint(anchor) is { } anchored)
        {
            return anchored;
        }

        return screens.Primary ?? (screens.All.Count > 0 ? screens.All[0] : null);
    }
}
