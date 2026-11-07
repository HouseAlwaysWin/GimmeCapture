using System;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Services.Platforms.Desktop;

/// <summary>
/// One window built ahead of the capture that needs it, while the app is idle: a capture that finds one ready skips the
/// build, the <c>window=</c> stage of <c>SnipOpen.Timings</c>, 32-57 ms of a warm open in the user's log (25-33 ms in
/// <c>OverlayPrepareProbeTests</c>), and the window costs 2.9-3.4 MB of private bytes while it waits, unseen. Built
/// a while after an overlay closes and after the idle warm-up, never while an overlay is up. UI thread only.
/// </summary>
internal sealed class PreparedWindow<T>
    where T : class
{
    private readonly Func<T> _build;
    private readonly Func<bool> _overlayOpen;
    private readonly Action<Action, TimeSpan> _schedule;
    private readonly TimeSpan _delay;
    private T? _ready;
    private bool _scheduled;

    /// <param name="build">Builds the window, unseen.</param>
    /// <param name="overlayOpen">Whether an overlay is up: no window is built then, and its close asks again.</param>
    /// <param name="schedule">
    /// Runs an action on the UI thread after a delay, at a priority below input and rendering.
    /// </param>
    /// <param name="delay">How long after it is asked for the window is built.</param>
    internal PreparedWindow(Func<T> build, Func<bool> overlayOpen, Action<Action, TimeSpan> schedule, TimeSpan delay)
    {
        _build = build;
        _overlayOpen = overlayOpen;
        _schedule = schedule;
        _delay = delay;
    }

    internal bool IsReady => _ready != null;

    /// <summary>The window built ahead, now the caller's to show, or null: the caller then builds one.</summary>
    internal T? Take()
    {
        var ready = _ready;
        _ready = null;
        return ready;
    }

    /// <summary>Has the next window built after the delay, unless one is waiting or already due.</summary>
    internal void PrepareSoon()
    {
        if (_scheduled || _ready != null)
        {
            return;
        }

        _scheduled = true;
        _schedule(Prepare, _delay);
    }

    private void Prepare()
    {
        _scheduled = false;
        if (_ready != null || _overlayOpen())
        {
            return;
        }

        try
        {
            _ready = _build();
        }
        catch (Exception ex)
        {
            // The capture builds its window itself then, as it did before there was one ready.
            AppLog.Warning("SnipWindowFactory.PrepareWindow", ex);
        }
    }
}
