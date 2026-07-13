using System;
using System.Threading;
using System.Threading.Tasks;

namespace GimmeCapture.Services.Translation;

public static class TranslationExecutionHelper
{
    public static async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken externalCt,
        TimeSpan timeout,
        Func<T> fallbackFactory,
        string scope)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        linkedCts.CancelAfter(timeout);

        try
        {
            return await action(linkedCts.Token);
        }
        catch (OperationCanceledException) when (externalCt.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine($"[{scope}] Timeout after {timeout.TotalSeconds:F0}s.");
            return fallbackFactory();
        }
        catch (Exception ex)
        {
            GimmeCapture.Services.Core.Infrastructure.AppLog.Error($"TranslationExecution.{scope}", ex);
            return fallbackFactory();
        }
    }
}
