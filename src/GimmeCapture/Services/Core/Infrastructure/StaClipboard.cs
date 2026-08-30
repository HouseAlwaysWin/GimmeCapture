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
/// <see cref="SetDataObjectFlushed"/>) so the data persists on the clipboard after the worker thread exits.</para>
/// </summary>
internal static class StaClipboard
{
    /// <summary>
    /// Time budget for one clipboard write, <see cref="SetDataObjectFlushed"/>'s internal retries included.
    /// Comfortably larger than that retry budget so the timeout never cuts a retry loop short.
    /// </summary>
    public static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(10);

#if WINDOWS
    // WinForms runs its own retry loop around OleSetClipboard/OleFlushClipboard, but its default (10 × 100ms ≈ 1s)
    // is SHORTER than a clipboard manager, Win+V history listener, RDP/VM clipboard sync or an Office add-in can
    // hold the clipboard open. Losing that race throws — and a throw leaves the PREVIOUS clipboard content in
    // place, so the next paste silently yields the previous capture. 30 × 150ms ≈ 4.5s covers realistic holds
    // while staying inside WriteTimeout. (Measured: a rival holding the clipboard for 2s made 4 of 5 writes lose
    // the race at the default budget, and 0 of 5 at this one.)
    private const int RetryTimes = 30;
    private const int RetryDelayMs = 150;

    /// <summary>
    /// Puts <paramref name="data"/> on the clipboard and flushes it (<c>copy: true</c>), so the payload persists
    /// after the worker STA thread exits, retrying for <see cref="RetryTimes"/> × <see cref="RetryDelayMs"/> while
    /// another app holds the clipboard open.
    ///
    /// <para>Throws <see cref="System.Runtime.InteropServices.ExternalException"/> when every attempt loses that
    /// race. Never swallow it: a failed write leaves the previous clipboard content in place, so reporting it as a
    /// success is what makes a paste return the PREVIOUS image.</para>
    ///
    /// <para>Must be called on an STA thread — i.e. from inside <see cref="RunAsync"/>.</para>
    /// </summary>
    public static void SetDataObjectFlushed(System.Windows.Forms.DataObject data)
    {
        System.Windows.Forms.Clipboard.SetDataObject(data, copy: true, RetryTimes, RetryDelayMs);
    }
#endif

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
