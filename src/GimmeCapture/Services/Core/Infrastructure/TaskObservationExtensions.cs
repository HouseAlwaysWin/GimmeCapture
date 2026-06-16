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
