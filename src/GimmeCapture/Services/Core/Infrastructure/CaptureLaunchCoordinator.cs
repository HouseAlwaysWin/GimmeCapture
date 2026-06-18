using System;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Models;

namespace GimmeCapture.Services.Core.Infrastructure;

public enum CaptureLaunchResult
{
    Launched,
    Cancelled,
    AlreadyRunning
}

public sealed class CaptureLaunchCoordinator
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private CancellationTokenSource? _countdownCts;
    private int _isRunning;

    public CaptureLaunchCoordinator(Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _delayAsync = delayAsync ?? Task.Delay;
    }

    public bool IsRunning => Volatile.Read(ref _isRunning) != 0;

    public static bool UsesDelay(CaptureMode mode) =>
        mode is CaptureMode.Copy or CaptureMode.Pin or CaptureMode.Record;

    public async Task<CaptureLaunchResult> TryLaunchAsync(
        CaptureMode mode,
        CaptureDelay delay,
        Func<int, CancellationToken, Task> showCountdownAsync,
        Func<Task> launchAsync)
    {
        ArgumentNullException.ThrowIfNull(showCountdownAsync);
        ArgumentNullException.ThrowIfNull(launchAsync);

        int seconds = UsesDelay(mode) ? (int)delay : 0;
        if (seconds <= 0)
        {
            await launchAsync();
            return CaptureLaunchResult.Launched;
        }

        if (Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
        {
            return CaptureLaunchResult.AlreadyRunning;
        }

        using var cts = new CancellationTokenSource();
        _countdownCts = cts;
        try
        {
            for (int remaining = seconds; remaining > 0; remaining--)
            {
                await showCountdownAsync(remaining, cts.Token);
                await _delayAsync(TimeSpan.FromSeconds(1), cts.Token);
            }

            await launchAsync();
            return CaptureLaunchResult.Launched;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return CaptureLaunchResult.Cancelled;
        }
        finally
        {
            _countdownCts = null;
            Volatile.Write(ref _isRunning, 0);
        }
    }

    public void Cancel()
    {
        _countdownCts?.Cancel();
    }
}
