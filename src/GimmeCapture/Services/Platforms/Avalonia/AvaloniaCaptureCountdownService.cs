using System;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Views.Main;

namespace GimmeCapture.Services.Platforms.Desktop;

/// <summary>
/// The capture-delay countdown: one small topmost window, shown while a delayed capture counts down and closed when it
/// fires or is cancelled. The app's bootstrapper owns it, so a delayed capture counts down in a launch to the tray too.
/// The main window owned it before, and a launch to the tray builds that window only once the user opens it.
/// </summary>
internal sealed class AvaloniaCaptureCountdownService
{
    private readonly Action _cancel;
    private CaptureCountdownWindow? _window;

    /// <param name="cancel">Cancels the countdown, as Escape in its window does.</param>
    public AvaloniaCaptureCountdownService(Action cancel)
    {
        ArgumentNullException.ThrowIfNull(cancel);
        _cancel = cancel;
    }

    /// <summary>Shows the seconds left, opening the window for the first of them. On the UI thread.</summary>
    public Task ShowAsync(int seconds, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _window ??= new CaptureCountdownWindow(_cancel);
        _window.UpdateCountdown(seconds);
        if (!_window.IsVisible)
        {
            _window.Show();
        }

        return Task.CompletedTask;
    }

    /// <summary>Closes the window, if a countdown opened it. On the UI thread.</summary>
    public void Close()
    {
        if (_window == null)
        {
            return;
        }

        _window.Close();
        _window = null;
    }
}
