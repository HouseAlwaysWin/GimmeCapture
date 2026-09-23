using System;
using System.Threading.Tasks;

namespace GimmeCapture.Services.Core.Infrastructure;

public static class TaskObservationExtensions
{
    public static void Forget(this Task task, string operation)
    {
        ArgumentNullException.ThrowIfNull(task);
        _ = ObserveAsync(task, operation);
    }

    /// <summary>
    /// Runs a command's <c>Execute()</c> without letting a failure take the app down. A bare <c>.Subscribe()</c> has
    /// no error handler, so when the command faults, Rx rethrows on the UI thread and the process exits. The failure
    /// is not lost by handling it here: ReactiveUI also reports it through <c>ThrownExceptions</c> (the command's own
    /// handler, or <see cref="UnhandledCommandExceptionReporter"/>), so this only records that the call site saw it.
    /// </summary>
    public static IDisposable SubscribeLoggingErrors<T>(this IObservable<T> execution, string operation = "Command.Execute")
    {
        ArgumentNullException.ThrowIfNull(execution);
        return execution.Subscribe(static _ => { }, exception => AppLog.Warning(operation, exception));
    }

    private static async Task ObserveAsync(Task task, string operation)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is an expected terminal state for background work.
        }
        catch (Exception ex)
        {
            AppLog.Error(operation, ex);
        }
    }
}
