using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Models;
using SkiaSharp;

namespace GimmeCapture.Services.Core.Media;

/// <summary>
/// The batch-compress scheduling engine, extracted from MainWindowViewModel. Owns the shared cancellation
/// source, the concurrency-capped semaphore, and the in-flight worker count; runs each queued item through
/// <see cref="CompressPipeline"/>. All per-run values are captured on the UI thread by the caller and passed
/// in; the runner mutates only the (bindable) <see cref="CompressQueueItem"/> and calls back for state
/// persistence and batch completion. Lives on the UI thread like the original (no locks) — every state
/// mutation runs on the UI-thread continuation.
/// </summary>
internal sealed class CompressBatchRunner
{
    private CancellationTokenSource? _cts;
    private SemaphoreSlim? _semaphore;
    private int _inFlight;

    /// <summary>Lazily creates the shared batch context (CTS + concurrency-capped semaphore) and returns the
    /// batch cancellation token. A no-op while a batch is already in flight.</summary>
    public CancellationToken EnsureContext(int parallelCount)
    {
        if (_cts == null || _semaphore == null)
        {
            _cts = new CancellationTokenSource();
            _semaphore = new SemaphoreSlim(Math.Clamp(parallelCount, 1, 8));
        }
        return _cts.Token;
    }

    public void Cancel() => _cts?.Cancel();

    /// <summary>Launches one item worker (fire-and-forget; tracked via the in-flight count). The concurrency
    /// cap is enforced by the shared semaphore. <paramref name="persistItemState"/> mirrors the item's state
    /// to disk; <paramref name="onAllDone"/> runs (worker continuation) when the last worker finishes and the
    /// batch context is torn down.</summary>
    public void Start(
        CompressQueueItem item, CompressSettingsSnapshot snap, string ext, string outputFolder, bool appendDate,
        IProgress<double> progress, IProgress<double> resumeProgress,
        IReadOnlyList<(double Start, double End, double Speed)> keptRuns, Action<SKBitmap, double>? burnInComposite,
        Action<CompressQueueItem> persistItemState, Action onAllDone)
    {
        _inFlight++;
        _ = RunItemAsync(item, snap, ext, outputFolder, appendDate, progress, resumeProgress, keptRuns,
            burnInComposite, persistItemState, onAllDone);
    }

    private async Task RunItemAsync(
        CompressQueueItem item, CompressSettingsSnapshot snap, string ext, string outputFolder, bool appendDate,
        IProgress<double> progress, IProgress<double> resumeProgress,
        IReadOnlyList<(double Start, double End, double Speed)> keptRuns, Action<SKBitmap, double>? burnInComposite,
        Action<CompressQueueItem> persistItemState, Action onAllDone)
    {
        SemaphoreSlim semaphore = _semaphore!;
        CancellationToken token = item.Cts!.Token;
        try
        {
            try
            {
                await semaphore.WaitAsync(token);
            }
            catch (OperationCanceledException)
            {
                item.Status = CompressQueueStatus.Cancelled;
                CompressSegmentStore.Clear(item.Path); // cancelled: discard any resume chunks
                return;
            }

            try
            {
                token.ThrowIfCancellationRequested();
                if (!File.Exists(item.Path))
                {
                    item.Status = CompressQueueStatus.Failed;
                    return;
                }

                item.Gate = new ManualResetEventSlim(true);
                item.Status = CompressQueueStatus.Running;
                persistItemState(item); // clear any stale "paused" marker now that it's actually encoding

                double duration = await CompressPipeline.ProbeInputDurationAsync(item.Path);

                // Re-clamp the captured kept runs against the freshly probed duration (whole file when empty).
                var runs = keptRuns
                    .Select(r => (Start: Math.Clamp(r.Start, 0, duration), End: Math.Clamp(r.End, 0, duration),
                        Speed: r.Speed > 0 ? r.Speed : 1.0))
                    .Where(r => r.End > r.Start + 0.001)
                    .OrderBy(r => r.Start)
                    .ToList();
                if (runs.Count == 0 && duration > 0)
                {
                    runs.Add((0, duration, 1.0));
                }
                // Output span honors per-run speed (a 2× run is half as long); drives the target-size bitrate.
                double outputDuration = runs.Sum(r => (r.End - r.Start) / (r.Speed > 0 ? r.Speed : 1.0));
                bool multiSegment = runs.Count > 1;
                bool hasSpeed = runs.Any(r => Math.Abs(r.Speed - 1.0) > 0.001);
                VideoEditCrop? crop = item.Crop;

                // Resumable segmented CRF only handles one contiguous, full-speed, un-cropped, un-annotated span
                // into a plain container; anything else (target-size / multi-segment / speed / crop / burn-in /
                // GIF / WebM) uses the whole-file path below.
                bool gifWebm = ext.Equals(".gif", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".webm", StringComparison.OrdinalIgnoreCase);
                item.SupportsResume = !snap.UseTargetSize && duration > 0 && !multiSegment && !hasSpeed && crop == null
                    && burnInComposite == null && !gifWebm;
                int targetKbps = 0;
                if (snap.UseTargetSize)
                {
                    if (duration <= 0 || outputDuration <= 0)
                    {
                        item.Status = CompressQueueStatus.Failed;
                        return;
                    }
                    targetKbps = CompressEncodeMath.ComputeTargetVideoBitrateKbps((double)snap.TargetSizeMB, outputDuration);
                }

                string outputPath = CompressOutputPath.BuildBatchOutputPath(
                    item.Path, item.OutputName, outputFolder, ext, appendDate, DateTime.Now);
                try
                {
                    string? finalDir = Path.GetDirectoryName(outputPath);
                    if (!string.IsNullOrEmpty(finalDir))
                    {
                        Directory.CreateDirectory(finalDir); // includes any user-typed subfolder
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Error("Compress.QueueOutDir", ex);
                }

                bool ok = await CompressPipeline.EncodeOneFileAsync(
                    item.Path, outputPath, snap, targetKbps, duration, progress, token, item.Gate, item.Rotation,
                    resumeProgress, runs, crop, burnInComposite);
                if (ok)
                {
                    item.OutputPath = outputPath; // record the produced file for the 開啟資料夾 button
                }
                item.Status = ok ? CompressQueueStatus.Done : CompressQueueStatus.Failed;
                if (ok)
                {
                    persistItemState(item); // remember completion so a resumed batch skips this file
                }
            }
            catch (OperationCanceledException)
            {
                item.Status = CompressQueueStatus.Cancelled;
                CompressSegmentStore.Clear(item.Path); // cancelled: discard any resume chunks
            }
            catch (Exception ex)
            {
                AppLog.Error("Compress.QueueItem", ex);
                item.Status = CompressQueueStatus.Failed;
            }
            finally
            {
                item.Gate?.Dispose();
                item.Gate = null;
                semaphore.Release();
            }
        }
        finally
        {
            item.Cts?.Dispose();
            item.Cts = null;
            OnWorkerDone(onAllDone);
        }
    }

    // Runs on the UI thread (worker continuation). When the last worker finishes, tears down the batch context.
    private void OnWorkerDone(Action onAllDone)
    {
        _inFlight--;
        if (_inFlight > 0)
        {
            return;
        }

        _inFlight = 0;
        _semaphore?.Dispose();
        _semaphore = null;
        _cts?.Dispose();
        _cts = null;
        onAllDone();
    }
}
