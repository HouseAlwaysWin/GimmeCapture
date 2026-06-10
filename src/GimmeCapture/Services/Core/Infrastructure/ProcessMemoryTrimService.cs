using System;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace GimmeCapture.Services.Core.Infrastructure;

public static class ProcessMemoryTrimService
{
    private static readonly ProcessMemoryTrimCoordinator Coordinator = new(TrimCore);

    [DllImport("psapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    public static void TrimCurrentProcessWorkingSet()
    {
        Coordinator.TrimNow("runtime");
    }

    internal static Task<bool> ScheduleStartupTrimAsync(
        TimeSpan? delay = null,
        CancellationToken ct = default)
    {
        return Coordinator.ScheduleStartupTrimAsync(
            delay ?? TimeSpan.FromSeconds(2),
            ct);
    }

    private static void TrimCore(string reason)
    {
        try
        {
            var before = CaptureSnapshot();
            Debug.WriteLine($"[MemoryTrim] reason={reason} phase=before {before}");

            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

            using var process = Process.GetCurrentProcess();
            _ = EmptyWorkingSet(process.Handle);
            process.Refresh();

            var after = CaptureSnapshot();
            Debug.WriteLine($"[MemoryTrim] reason={reason} phase=after {after}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MemoryTrim] reason={reason} failed: {ex.Message}");
            // Best-effort memory trim only.
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

internal sealed class ProcessMemoryTrimCoordinator
{
    private static readonly TimeSpan DefaultMinimumTrimInterval = TimeSpan.FromSeconds(5);

    private readonly object _trimGate = new();
    private readonly Action<string> _trimAction;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeSpan _minimumTrimInterval;
    private long _trimVersion;
    private int _startupTrimScheduled;
    private DateTimeOffset? _lastTrimAtUtc;

    internal ProcessMemoryTrimCoordinator(
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
            var now = _utcNow();
            if (_lastTrimAtUtc.HasValue
                && now - _lastTrimAtUtc.Value < _minimumTrimInterval)
            {
                Debug.WriteLine($"[MemoryTrim] reason={reason} skipped; a trim ran recently.");
                return false;
            }

            _trimVersion++;
            _lastTrimAtUtc = now;
            _trimAction(reason);
            return true;
        }
    }

    internal async Task<bool> ScheduleStartupTrimAsync(
        TimeSpan delay,
        CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _startupTrimScheduled, 1, 0) != 0)
        {
            Debug.WriteLine("[MemoryTrim] Startup trim already scheduled; skipping duplicate request.");
            return false;
        }

        long observedTrimVersion;
        lock (_trimGate)
        {
            observedTrimVersion = _trimVersion;
        }

        try
        {
            await _delayAsync(delay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("[MemoryTrim] Startup trim cancelled.");
            return false;
        }

        lock (_trimGate)
        {
            if (_trimVersion != observedTrimVersion)
            {
                Debug.WriteLine("[MemoryTrim] Runtime trim already occurred during startup delay; skipping startup trim.");
                return false;
            }

            _trimVersion++;
            _lastTrimAtUtc = _utcNow();
            _trimAction("startup-delayed");
            return true;
        }
    }
}
