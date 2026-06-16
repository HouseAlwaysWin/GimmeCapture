using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace GimmeCapture.Services.Core.Infrastructure;

public static class ProcessMemoryTrimService
{
    private static readonly IdleMemoryTrimScheduler FullTrimScheduler = new(TrimCore);
    private static readonly IdleMemoryTrimScheduler WorkingSetTrimScheduler = new(TrimWorkingSetCore);

    [DllImport("psapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    public static Task<bool> RequestIdleTrimAsync(
        string reason,
        TimeSpan? delay = null,
        CancellationToken ct = default)
    {
        return FullTrimScheduler.RequestTrimAsync(
            reason,
            delay ?? TimeSpan.FromSeconds(30),
            ct);
    }

    public static Task<bool> RequestIdleWorkingSetTrimAsync(
        string reason,
        TimeSpan? delay = null,
        CancellationToken ct = default)
    {
        return WorkingSetTrimScheduler.RequestTrimAsync(
            reason,
            delay ?? TimeSpan.FromSeconds(5),
            ct);
    }

    public static void NotifyActivity(string reason)
    {
        FullTrimScheduler.NotifyActivity(reason);
        WorkingSetTrimScheduler.NotifyActivity(reason);
    }

    private static void TrimCore(string reason)
    {
        try
        {
            var before = CaptureSnapshot();
            AppLog.Information($"MemoryTrim.Before.{reason}.{before}");

            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

            using var process = Process.GetCurrentProcess();
            _ = EmptyWorkingSet(process.Handle);
            process.Refresh();

            var after = CaptureSnapshot();
            AppLog.Information($"MemoryTrim.After.{reason}.{after}");
        }
        catch (Exception ex)
        {
            AppLog.Warning("MemoryTrim.Run", ex);
            // Best-effort memory trim only.
        }
    }

    private static void TrimWorkingSetCore(string reason)
    {
        try
        {
            var before = CaptureSnapshot();
            AppLog.Information($"MemoryTrim.WorkingSetBefore.{reason}.{before}");

            using var process = Process.GetCurrentProcess();
            _ = EmptyWorkingSet(process.Handle);
            process.Refresh();

            var after = CaptureSnapshot();
            AppLog.Information($"MemoryTrim.WorkingSetAfter.{reason}.{after}");
        }
        catch (Exception ex)
        {
            AppLog.Warning("MemoryTrim.WorkingSetRun", ex);
        }
    }

    private static MemorySnapshot CaptureSnapshot()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return new MemorySnapshot(
            process.WorkingSet64,
            process.PrivateMemorySize64,
            GC.GetTotalMemory(false));
    }

    private readonly record struct MemorySnapshot(
        long WorkingSetBytes,
        long PrivateBytes,
        long ManagedHeapBytes)
    {
        public override string ToString()
        {
            return $"workingSet={FormatBytes(WorkingSetBytes)} " +
                   $"privateBytes={FormatBytes(PrivateBytes)} " +
                   $"managedHeap={FormatBytes(ManagedHeapBytes)}";
        }

        private static string FormatBytes(long bytes)
        {
            const double megabyte = 1024d * 1024d;
            return $"{bytes / megabyte:F1}MB";
        }
    }
}

internal sealed class IdleMemoryTrimScheduler
{
    private static readonly TimeSpan DefaultMinimumTrimInterval = TimeSpan.FromSeconds(5);

    private readonly object _trimGate = new();
    private readonly Action<string> _trimAction;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeSpan _minimumTrimInterval;
    private readonly HashSet<string> _pendingReasons = new(StringComparer.Ordinal);
    private CancellationTokenSource? _pendingTrimCts;
    private long _activityVersion;
    private DateTimeOffset? _lastTrimAtUtc;

    internal IdleMemoryTrimScheduler(
        Action<string> trimAction,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        Func<DateTimeOffset>? utcNow = null,
        TimeSpan? minimumTrimInterval = null)
    {
        _trimAction = trimAction ?? throw new ArgumentNullException(nameof(trimAction));
        _delayAsync = delayAsync ?? Task.Delay;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _minimumTrimInterval = minimumTrimInterval ?? DefaultMinimumTrimInterval;
    }

    internal bool TrimNow(string reason)
    {
        lock (_trimGate)
        {
            CancelPendingTrimLocked(clearReasons: true);
            _activityVersion++;

            var now = _utcNow();
            if (_lastTrimAtUtc.HasValue
                && now - _lastTrimAtUtc.Value < _minimumTrimInterval)
            {
                AppLog.Information("MemoryTrim.Skipped.Recent");
                return false;
            }

            _lastTrimAtUtc = now;
            _trimAction(reason);
            return true;
        }
    }

    internal Task<bool> RequestTrimAsync(
        string reason,
        TimeSpan delay,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A trim reason is required.", nameof(reason));
        }

        CancellationTokenSource requestCts;
        long observedActivityVersion;
        TimeSpan effectiveDelay;
        lock (_trimGate)
        {
            _pendingReasons.Add(reason.Trim());
            CancelPendingTrimLocked(clearReasons: false);
            requestCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _pendingTrimCts = requestCts;
            observedActivityVersion = _activityVersion;
            effectiveDelay = CalculateEffectiveDelay(delay, _utcNow());
        }

        return RunPendingTrimAsync(
            requestCts,
            observedActivityVersion,
            effectiveDelay);
    }

    internal void NotifyActivity(string reason)
    {
        lock (_trimGate)
        {
            _activityVersion++;
            CancelPendingTrimLocked(clearReasons: true);
        }

        AppLog.Information($"MemoryTrim.Activity.{reason}");
    }

    private async Task<bool> RunPendingTrimAsync(
        CancellationTokenSource requestCts,
        long observedActivityVersion,
        TimeSpan delay)
    {
        try
        {
            await _delayAsync(delay, requestCts.Token).ConfigureAwait(false);

            lock (_trimGate)
            {
                if (!ReferenceEquals(_pendingTrimCts, requestCts)
                    || requestCts.IsCancellationRequested
                    || _activityVersion != observedActivityVersion)
                {
                    return false;
                }

                var now = _utcNow();
                if (_lastTrimAtUtc.HasValue
                    && now - _lastTrimAtUtc.Value < _minimumTrimInterval)
                {
                    AppLog.Information("MemoryTrim.IdleSkipped.Recent");
                    return false;
                }

                string combinedReason = string.Join("+", _pendingReasons.OrderBy(static value => value, StringComparer.Ordinal));
                _pendingReasons.Clear();
                _pendingTrimCts = null;
                _lastTrimAtUtc = now;
                _trimAction($"idle:{combinedReason}");
                return true;
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            lock (_trimGate)
            {
                if (ReferenceEquals(_pendingTrimCts, requestCts))
                {
                    _pendingTrimCts = null;
                    _pendingReasons.Clear();
                }
            }

            requestCts.Dispose();
        }
    }

    private TimeSpan CalculateEffectiveDelay(TimeSpan requestedDelay, DateTimeOffset now)
    {
        var normalizedDelay = requestedDelay < TimeSpan.Zero ? TimeSpan.Zero : requestedDelay;
        if (!_lastTrimAtUtc.HasValue)
        {
            return normalizedDelay;
        }

        var remainingInterval = (_lastTrimAtUtc.Value + _minimumTrimInterval) - now;
        return remainingInterval > normalizedDelay ? remainingInterval : normalizedDelay;
    }

    private void CancelPendingTrimLocked(bool clearReasons)
    {
        _pendingTrimCts?.Cancel();
        _pendingTrimCts = null;
        if (clearReasons)
        {
            _pendingReasons.Clear();
        }
    }
}
