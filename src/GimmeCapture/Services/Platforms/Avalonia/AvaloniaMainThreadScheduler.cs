using System;
using System.Reactive.Concurrency;
using Avalonia.Threading;

namespace GimmeCapture.Services.Platforms.Desktop;

/// <summary>
/// ReactiveUI's main-thread scheduler, run as it was before ReactiveUI 26: work scheduled on the UI thread runs at
/// once. ReactiveUI 26's AvaloniaScheduler posts all of it at Background priority, and drops what has not run when
/// its subscription is disposed, so a notification raised on the UI thread just before a window closed never arrived.
/// A recording's "finalize done" was lost that way when the snip overlay closed straight after it, and its progress
/// window stayed on screen, one more with every recording. The app was written against running at once, and the tests
/// still run that way (ImmediateScheduler).
/// </summary>
internal sealed class AvaloniaMainThreadScheduler : LocalScheduler
{
    /// <summary>
    /// How deep work scheduled from running work nests before it is queued instead, so a feedback loop cannot overflow
    /// the stack. The pre-26 scheduler's limit.
    /// </summary>
    internal const int MaxNestedRuns = 32;

    public static AvaloniaMainThreadScheduler Instance { get; } = new(
        static () => Dispatcher.UIThread.CheckAccess(),
        ReactiveUI.Primitives.Reactive.Concurrency.AvaloniaScheduler.Instance);

    private readonly Func<bool> _isOnUiThread;
    private readonly IScheduler _queued;

    // Read and written on the UI thread only.
    private int _nestedRuns;

    /// <param name="isOnUiThread">Whether the calling thread is the UI thread.</param>
    /// <param name="queued">Runs everything else: work from other threads, delayed work, work nested too deep.</param>
    internal AvaloniaMainThreadScheduler(Func<bool> isOnUiThread, IScheduler queued)
    {
        _isOnUiThread = isOnUiThread;
        _queued = queued;
    }

    public override IDisposable Schedule<TState>(TState state, TimeSpan dueTime, Func<IScheduler, TState, IDisposable> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (Scheduler.Normalize(dueTime) == TimeSpan.Zero && _isOnUiThread() && _nestedRuns < MaxNestedRuns)
        {
            _nestedRuns++;
            try
            {
                return action(this, state);
            }
            finally
            {
                _nestedRuns--;
            }
        }

        // Handing the action this scheduler keeps whatever it schedules in turn on the UI thread running at once.
        return _queued.Schedule(state, dueTime, (_, s) => action(this, s));
    }
}
