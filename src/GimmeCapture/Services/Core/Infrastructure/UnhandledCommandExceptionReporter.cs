using System;

namespace GimmeCapture.Services.Core.Infrastructure;

/// <summary>
/// Where an exception goes when it escapes a ReactiveUI pipeline nobody is listening to — typically a command that
/// failed and has no <c>ThrownExceptions</c> subscriber of its own.
///
/// ReactiveUI's default for that case breaks into the debugger and rethrows on the UI thread, which in a release
/// build means the app simply vanishes: an auto-save folder that had become read-only turned Ctrl+S into a crash.
/// A failing command should cost the user that one action, not the whole session, so this logs the exception and —
/// once the UI exists — tells the user. Registered in <c>Program.BuildAvaloniaApp</c> through
/// <c>UseReactiveUI(rx =&gt; rx.WithExceptionHandler(...))</c>.
/// </summary>
internal sealed class UnhandledCommandExceptionReporter : IObserver<Exception>
{
    internal static UnhandledCommandExceptionReporter Shared { get; } =
        new(exception => AppLog.Error("Command.Unhandled", exception));

    private readonly Action<Exception> _log;
    private Action<Exception>? _notifyUser;

    internal UnhandledCommandExceptionReporter(Action<Exception> log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Adds the user-facing half once there is a UI to show it in. Until then — and whenever notifying fails — the
    /// exception is still logged: reporting a failure must never become a second one.
    /// </summary>
    internal void AttachUserNotification(Action<Exception> notifyUser)
    {
        _notifyUser = notifyUser;
    }

    public void OnNext(Exception value)
    {
        _log(value);

        try
        {
            _notifyUser?.Invoke(value);
        }
        catch (Exception notifyFailure)
        {
            _log(notifyFailure);
        }
    }

    public void OnError(Exception error) => OnNext(error);

    public void OnCompleted()
    {
    }
}
