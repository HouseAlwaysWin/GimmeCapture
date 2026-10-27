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
    /// Runs a command's <c>Execute()</c> with an error handler at the call site. A bare <c>.Subscribe()</c> has none,
    /// so Rx rethrows the failure from <c>OnError</c>. That used to end the process; since ReactiveUI 26 the command
    /// catches the rethrow, even one that comes later on the output scheduler, and reports it through
    /// <c>ThrownExceptions</c> again: the app survives, but the one failure is reported twice. Handled here, it is
    /// reported once through <c>ThrownExceptions</c> (the command's own handler, or
    /// <see cref="UnhandledCommandExceptionReporter"/>), and this only records that the call site saw it.
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
