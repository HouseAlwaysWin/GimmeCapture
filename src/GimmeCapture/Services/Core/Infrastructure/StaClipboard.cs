using System;
using System.Threading;
using System.Threading.Tasks;

namespace GimmeCapture.Services.Core.Infrastructure;

/// <summary>
/// Runs WinForms/OLE clipboard writes on a dedicated background STA thread, bounded by a timeout, so the
/// synchronous clipboard call can never block the Avalonia UI thread.
///
/// <para>The OLE clipboard write (<c>Clipboard.SetText</c>/<c>SetImage</c>/<c>SetDataObject</c>/
/// <c>SetFileDropList</c>) is synchronous and can wedge for a long time — a large payload racing the Windows
/// clipboard-history listeners, or another app holding the clipboard open, can stall it. Running it on the UI
/// thread froze the whole app (originally observed with long OCR text; the same latency is latent in every
/// image/file clipboard write). This helper hosts the write on a background STA thread and abandons the thread
/// if it overruns the timeout — a wedged write can never block the caller or app exit.</para>
///
/// <para>The <see cref="Action"/> supplied by the caller must issue a flushing write (e.g.
/// <c>SetDataObject(data, copy: true)</c>) so the data persists on the clipboard after the worker thread exits.</para>
/// </summary>
internal static class StaClipboard
{
    /// <summary>
    /// Executes <paramref name="write"/> on a dedicated STA thread, bounded by <paramref name="timeout"/>.
    /// Returns <c>true</c> only if the delegate completed without throwing before the timeout elapsed. A thread
    /// that overruns the timeout, or a delegate that throws, resolves to <c>false</c> (and is logged) rather than
    /// blocking the UI thread. Exceptions are never propagated to the caller.
    /// </summary>
    public static Task<bool> RunAsync(Action write, TimeSpan timeout, string logContext, string threadName = "ClipboardSta")
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                write();
                tcs.TrySetResult(true);
            }
            catch (Exception ex)
            {
                AppLog.Warning($"{logContext}.Sta", ex);
                tcs.TrySetResult(false);
            }
        })
        {
            IsBackground = true,
            Name = threadName,
        };

        // SetApartmentState(STA) is only valid on Windows; callers only reach this on Windows, but guard so a
        // stray non-Windows call degrades to a failed write instead of a PlatformNotSupportedException.
        if (OperatingSystem.IsWindows())
        {
            thread.SetApartmentState(ApartmentState.STA);
        }

        thread.Start();

        return AwaitWithTimeoutAsync(tcs.Task, timeout, logContext);
    }

    private static async Task<bool> AwaitWithTimeoutAsync(Task<bool> task, TimeSpan timeout, string logContext)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
        if (completed != task)
        {
            AppLog.Warning($"{logContext}.Timeout", new TimeoutException($"Clipboard write exceeded {timeout.TotalSeconds:0}s"));
            return false;
        }

        return task.Result;
    }
}
