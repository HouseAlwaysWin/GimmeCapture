using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using GimmeCapture.Services.Abstractions;

namespace GimmeCapture.Services.Platforms.Desktop;

public sealed class AvaloniaWindowLayerService : IWindowLayerService
{
    public IReadOnlyList<Window> LowerTopmostWindowsOfType<TWindow>() where TWindow : Window
    {
        var desktop = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        if (desktop == null)
        {
            return [];
        }

        var lowered = new List<Window>();
        foreach (var window in desktop.Windows.OfType<TWindow>())
        {
            if (!window.Topmost || !window.IsVisible)
            {
                continue;
            }

            window.Topmost = false;
            lowered.Add(window);
        }

        return lowered;
    }

    public void RestoreTopmostWindows(IEnumerable<Window> windows)
    {
        foreach (var window in windows)
        {
            try
            {
                if (window.IsVisible)
                {
                    window.Topmost = true;
                }
            }
            catch
            {
                // Window may already be disposed/closed.
            }
        }
    }
}
