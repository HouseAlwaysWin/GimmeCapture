using System;
using System.Threading;
using System.Threading.Tasks;

namespace GimmeCapture.Services.Core.Media.NativeFFmpeg;

/// <summary>
/// The worker plumbing the native recorder sessions share (<see cref="LibavGdigrabMkvSession"/>, and the WGC window
/// and composite sessions): a start replaces any running worker and runs the session's transcode on a background
/// task, a startup gate reports whether recording began, and a stop or dispose ends the worker. A session supplies the
/// transcode, how long the gate waits for a first frame and what it reports when none comes.
/// </summary>
internal abstract class LibavRecordingSession : IDisposable
{
    private CancellationTokenSource? _cts;
    private Task<bool>? _worker;

    public string? LastErrorMessage { get; private set; }
    public string? LastWarningMessage { get; protected set; }
    public string? SelectedEncoderName { get; protected set; }

    /// <summary>
    /// When true (default) GPU / Media-Foundation encoders (NVENC/QSV/AMF/MF) are tried before the
    /// software libx264/265 fallback. Set false to force software encoding.
    /// </summary>
    public bool PreferHardwareEncoder { get; set; } = true;

    /// <summary>Software-encoder CRF override (1-51); 0 keeps the default 23.</summary>
    public int VideoCrf { get; set; }

    /// <summary>Hardware-encoder target bitrate in bits/sec; 0 uses the automatic clamp.</summary>
    public long VideoBitrate { get; set; }

    /// <summary>How long the startup gate waits for the first encoded frame.</summary>
    protected abstract TimeSpan StartupGateTimeout { get; }

    /// <summary>How long <see cref="Dispose"/> lets a stopping worker finish writing its file.</summary>
    protected abstract TimeSpan DisposeWait { get; }

    /// <summary>Logs a gate timeout with the worker still running, and returns what the start reports.</summary>
    protected abstract bool OnStartupGateTimedOut();

    /// <summary>
    /// Runs <paramref name="transcode"/> on a background task in place of any running worker, and reports whether
    /// recording began: true once the transcode signals its first frame, false when it fails or is stopped before one
    /// (always a false, never a thrown error), and <see cref="OnStartupGateTimedOut"/> when neither happens in time.
    /// </summary>
    protected Task<bool> StartWorker(Action<CancellationToken, TaskCompletionSource<bool>> transcode)
    {
        LastErrorMessage = null;
        LastWarningMessage = null;
        SelectedEncoderName = null;

        try
        {
            _cts?.Cancel();
            _worker?.Wait(TimeSpan.FromMilliseconds(500));
        }
        catch
        {
            // ignore; we are replacing the session
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _worker = null;
        }

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var firstFrameTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _worker = Task.Run(() =>
        {
            try
            {
                transcode(ct, firstFrameTcs);
                LogNative("Worker completed successfully.");
                return true;
            }
            catch (OperationCanceledException)
            {
                firstFrameTcs.TrySetCanceled();
                LogNative("Worker canceled.");
                return false;
            }
            catch (Exception ex)
            {
                LastErrorMessage = ex.Message;
                AppLog.Debug(GetType().Name, $"[{GetType().Name}] {ex}");
                AppLog.Warning("RecordingNative.Worker", ex);
                firstFrameTcs.TrySetException(ex);
                return false;
            }
        }, CancellationToken.None);

        return StartupGateAsync(_worker, firstFrameTcs.Task);
    }

    private async Task<bool> StartupGateAsync(Task<bool> worker, Task<bool> firstFrame)
    {
        // Recording counts as started only once the first frame is encoded, which avoids false "started" states
        // that later produce a 1-frame output.
        var timeout = Task.Delay(StartupGateTimeout);
        var done = await Task.WhenAny(firstFrame, worker, timeout).ConfigureAwait(false);

        // A worker that fails or is stopped faults or cancels the first-frame signal just before it returns false, and
        // the gate can see either finish first. So only a real first frame answers through the signal, and otherwise
        // the worker answers: the start is a false, not the worker's error rethrown. That error is observed here so it
        // does not also surface as an UnobservedTaskException.
        ObserveFaultedTask(firstFrame);

        if (done == firstFrame && firstFrame.IsCompletedSuccessfully)
        {
            LogNative("StartupGate: first frame observed.");
            return firstFrame.Result;
        }

        if (done != timeout)
        {
            LogNative("StartupGate: worker completed before first frame.");
            return await worker.ConfigureAwait(false);
        }

        return OnStartupGateTimedOut();
    }

    private static void ObserveFaultedTask(Task t) =>
        t.ContinueWith(
            static x => { _ = x.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    public async Task StopAsync()
    {
        try
        {
            LogNative("StopAsync requested.");
            _cts?.Cancel();
            if (_worker != null)
            {
                await _worker.ConfigureAwait(false);
            }
        }
        finally
        {
            _worker = null;
            _cts?.Dispose();
            _cts = null;
        }
    }

    public void Dispose()
    {
        try
        {
            _cts?.Cancel();
            _worker?.Wait(DisposeWait);
        }
        catch
        {
            // ignore
        }

        _cts?.Dispose();
        _cts = null;
        _worker = null;
    }

    protected static void LogNative(string message)
    {
        AppLog.Information("RecordingNative", message); // was Debug.WriteLine, which a release build leaves out
    }
}
