using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;

namespace GimmeCapture.Services.Platforms.Avalonia;

/// <summary>
/// Follows which of the app's windows are shown and says whether any is, stand-ins aside: the tray's host window, 1x1 and
/// transparent, is shown all the time the app sits in the tray. <see cref="Services.Core.Infrastructure.UiThreadStallMonitor"/>
/// beats slowly while none is (<see cref="Services.Core.Infrastructure.UiStallWatchdog.TrayBeat"/>). A minimized window
/// counts as shown. Built and used on the UI thread.
/// </summary>
internal sealed class AppWindowsOnScreen : IDisposable
{
    private readonly HashSet<Window> _shown = new();
    private readonly Func<Window, bool> _isStandIn;
    private readonly Action<bool> _report;
    private readonly IDisposable _visibility;
    private readonly IDisposable _closed;
    private bool _anyShown;

    /// <param name="isStandIn">A window whose being shown says nothing about the user, the tray's host.</param>
    /// <param name="report">Told at once, then each time the answer changes.</param>
    internal AppWindowsOnScreen(Func<Window, bool> isStandIn, Action<bool> report)
    {
        _isStandIn = isStandIn;
        _report = report;
        // Every window, from Show and Hide; Close as well, which a window may get through without its visibility changing.
        _visibility = Visual.IsVisibleProperty.Changed.AddClassHandler<Window>((window, _) => Update(window));
        _closed = Window.WindowClosedEvent.AddClassHandler<Window>((window, _) => Forget(window));
        _report(false);
    }

    private void Update(Window window)
    {
        if (window.IsVisible && !_isStandIn(window))
        {
            _shown.Add(window);
        }
        else
        {
            _shown.Remove(window);
        }

        Report();
    }

    private void Forget(Window window)
    {
        _shown.Remove(window);
        Report();
    }

    private void Report()
    {
        bool anyShown = _shown.Count > 0;
        if (anyShown != _anyShown)
        {
            _anyShown = anyShown;
            _report(anyShown);
        }
    }

    public void Dispose()
    {
        _visibility.Dispose();
        _closed.Dispose();
        _shown.Clear();
    }
}
