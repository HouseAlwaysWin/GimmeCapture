using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Avalonia.Threading;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Media;
using SkiaSharp;

namespace GimmeCapture.ViewModels.Main;

// Manual scrolling capture: after the region is selected the overlay hides and the user
// scrolls the target window by hand. To survive fast scrolling we capture the region like
// a video — a background producer grabs frames back-to-back (~20-60fps) into a small
// bounded queue while a background consumer stitches them. Because frames are captured
// close together they always overlap, even during a fast flick. F6 finishes (opens the
// long pin); Esc cancels.
public partial class SnipWindowViewModel
{
    private SKBitmap? _manualAccumulated;
    private SKBitmap? _manualPrevFrame;
    private bool _manualScrollActive;
    private bool _manualFinishing;
    private int _manualMaxHeight;
    private int _manualMinOverlap;
    private int _manualIgnoreRight;

    // Row index within _manualAccumulated where the current viewport (_manualPrevFrame) top
    // sits. Tracked like a panorama's absolute position so scrolling back over already-captured
    // content neither duplicates nor scrambles the strip — only genuinely new extents grow it.
    private int _manualViewTop;

    // True after a step too large to trust (a fast flick): the running position is no longer
    // known, so we stop stitching and wait. When scrolling slows, the next trackable frame
    // re-localizes against the accumulated strip (or re-anchors) and clears this.
    private bool _manualDesynced;

    private CancellationTokenSource? _manualCts;
    private Task? _manualPipelineTask;
    private Channel<SKBitmap>? _manualFrameChannel;

    private const int ManualMinNewRows = 2;
    // Fraction of overlap rows allowed to differ. Higher because rows are matched by
    // pixel-similarity (not byte-exact), so a chat re-rendering a few rows per frame
    // (Discord, GPU-composited apps) still stitches.
    private const double ManualRowMismatchTolerance = 0.35;
    // Bounded capture queue: a few frames of slack so a slow stitch (Append on a tall strip)
    // doesn't stall capture, without unbounded memory growth.
    private const int ManualQueueCapacity = 8;
    // A single step moving more than this fraction of the frame height means the frames barely
    // overlapped (the user flicked fast): we can't trust the alignment, so mark the session
    // desynced and skip it rather than stitch at a wrong offset — but we keep advancing the
    // frame anchor and re-localize when scrolling slows, so it never freezes.
    private const double ManualMaxStepFraction = 0.7;

    /// <summary>True while a manual scrolling-capture session is running (overlay hidden).</summary>
    public bool IsManualScrollActive => _manualScrollActive;

    /// <summary>Show the on-screen hint (capture-excluded) with the localized instruction.</summary>
    public Action? ShowScrollingHintAction { get; set; }

    /// <summary>Update the hint with the current captured height (physical px).</summary>
    public Action<int>? UpdateScrollingHintAction { get; set; }

    /// <summary>Hide/close the hint window.</summary>
    public Action? HideScrollingHintAction { get; set; }

    private async Task StartManualScrollCaptureAsync()
    {
        if (_manualScrollActive)
        {
            return;
        }

        await _captureVisibilityCoordinator.HideAndWaitForCaptureAsync(HideAction ?? (() => { }));
        // Give the overlay time to disappear and the target to repaint before the first frame.
        await Task.Delay(150);

        double scaling = VisualScaling <= 0 ? 1.0 : VisualScaling;
        int physH = (int)(SelectionRect.Height * scaling);
        _manualMinOverlap = Math.Max(8, physH / 10);
        _manualIgnoreRight = (int)(18 * scaling);
        _manualMaxHeight = Math.Max(physH, physH * 40);

        try
        {
            _manualAccumulated = await _captureService.CaptureScreenAsync(SelectionRect, ScreenOffset, VisualScaling, false);
        }
        catch (Exception ex)
        {
            AppLog.Error("ManualScroll.FirstFrame", ex);
            CloseAction?.Invoke();
            return;
        }

        _manualPrevFrame = _manualAccumulated.Copy();
        _manualViewTop = 0;
        _manualDesynced = false;
        _manualFinishing = false;
        _manualScrollActive = true;

        // Finish (F6) / cancel (Esc) are handled by the snip key hook (SnipWindow.Win32.cs),
        // which receives keys even while the overlay is hidden — no separate global hotkey needed.

        ShowScrollingHintAction?.Invoke();
        UpdateScrollingHintAction?.Invoke(_manualAccumulated.Height);

        // Fields above are published on the UI thread before the background tasks start, so the
        // consumer/producer observe initialised state. From here on the accumulated/prev/viewTop
        // are owned solely by the consumer until FinishManualScrollCapture awaits the pipeline.
        _manualCts = new CancellationTokenSource();
        _manualFrameChannel = Channel.CreateBounded<SKBitmap>(new BoundedChannelOptions(ManualQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });

        CancellationToken token = _manualCts.Token;
        Task producer = Task.Run(() => ManualProducerLoopAsync(token));
        Task consumer = Task.Run(() => ManualConsumerLoopAsync(token));
        _manualPipelineTask = Task.WhenAll(producer, consumer);
    }

    // Grabs region frames back-to-back into the bounded channel. WriteAsync blocks (back-pressure)
    // when the queue is full, so no frame is dropped — capture self-paces to the consumer.
    private async Task ManualProducerLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                SKBitmap frame;
                try
                {
                    frame = await _captureService.CaptureScreenAsync(SelectionRect, ScreenOffset, VisualScaling, false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    AppLog.Warning("ManualScroll.Capture", ex);
                    continue;
                }

                try
                {
                    await _manualFrameChannel!.Writer.WriteAsync(frame, token);
                }
                catch (OperationCanceledException)
                {
                    frame.Dispose();
                    break;
                }
                catch (Exception ex)
                {
                    frame.Dispose();
                    AppLog.Warning("ManualScroll.Enqueue", ex);
                    break;
                }
            }
        }
        finally
        {
            _manualFrameChannel?.Writer.TryComplete();
        }
    }

    // Stitches frames in order off the UI thread. Owns _manualAccumulated/_manualPrevFrame/
    // _manualViewTop for the duration of the session.
    private async Task ManualConsumerLoopAsync(CancellationToken token)
    {
        try
        {
            await foreach (SKBitmap frame in _manualFrameChannel!.Reader.ReadAllAsync(token))
            {
                try
                {
                    if (_manualPrevFrame == null || _manualAccumulated == null)
                    {
                        frame.Dispose();
                        continue;
                    }

                    int height = frame.Height;

                    // Signed shift vs the immediately-previous frame: > 0 scrolled down (new
                    // content at bottom), < 0 scrolled up (new content at top).
                    ScrollStitcher.VerticalShift shift = ScrollStitcher.FindVerticalShift(
                        _manualPrevFrame, frame, _manualMinOverlap, _manualIgnoreRight, ManualRowMismatchTolerance);

                    // A trackable step is a confident match that moved no more than the guard:
                    // adjacent frames overlapped enough to trust the measured shift.
                    bool trackable = shift.Found && Math.Abs(shift.Rows) <= ManualMaxStepFraction * height;
                    bool grew = false;

                    if (trackable && !_manualDesynced)
                    {
                        // Fast path: extend the strip by the measured shift.
                        int newViewTop = _manualViewTop + shift.Rows;

                        if (newViewTop + height > _manualAccumulated.Height)
                        {
                            int extra = newViewTop + height - _manualAccumulated.Height;
                            SKBitmap grown = ScrollStitcher.Append(_manualAccumulated, frame, height - extra);
                            _manualAccumulated.Dispose();
                            _manualAccumulated = grown;
                            grew = true;
                        }
                        else if (newViewTop < 0)
                        {
                            int extra = -newViewTop;
                            SKBitmap grown = ScrollStitcher.Prepend(_manualAccumulated, frame, extra);
                            _manualAccumulated.Dispose();
                            _manualAccumulated = grown;
                            newViewTop = 0;
                            grew = true;
                        }
                        // else: scrolled back over already-captured content — track only.

                        _manualViewTop = Math.Clamp(newViewTop, 0, Math.Max(0, _manualAccumulated.Height - height));
                    }
                    else if (trackable) // && _manualDesynced
                    {
                        // Recovering: scrolling has slowed enough to track again. Re-localize the
                        // frame within the strip; if it isn't there (new content past a gap),
                        // re-anchor by appending it fresh. Either way the position is known again.
                        ScrollStitcher.VerticalShift loc = ScrollStitcher.LocateFrameInStrip(
                            _manualAccumulated, frame, _manualIgnoreRight, ManualRowMismatchTolerance, height);

                        if (loc.Found)
                        {
                            _manualViewTop = Math.Clamp(loc.Rows, 0, Math.Max(0, _manualAccumulated.Height - height));
                        }
                        else
                        {
                            SKBitmap grown = ScrollStitcher.Append(_manualAccumulated, frame, 0); // overlap 0 => append whole frame
                            _manualAccumulated.Dispose();
                            _manualAccumulated = grown;
                            _manualViewTop = Math.Max(0, _manualAccumulated.Height - height);
                            grew = true;
                        }

                        _manualDesynced = false;
                    }
                    else
                    {
                        // Untrackable step (fast flick / no overlap): position is unknown. Skip
                        // stitching and wait — but the anchor still advances below so we never freeze.
                        _manualDesynced = true;
                    }

                    _manualPrevFrame.Dispose();
                    _manualPrevFrame = frame; // anchor always advances; ownership moves into prev

                    if (grew)
                    {
                        int newHeight = _manualAccumulated.Height;
                        Dispatcher.UIThread.Post(() => UpdateScrollingHintAction?.Invoke(newHeight));

                        if (newHeight >= _manualMaxHeight)
                        {
                            Dispatcher.UIThread.Post(() => FinishManualScrollCapture(cancelled: false));
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Warning("ManualScroll.Stitch", ex);
                    if (!ReferenceEquals(frame, _manualPrevFrame))
                    {
                        frame.Dispose();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when the session is finished/cancelled.
        }
        finally
        {
            // Dispose any frames still queued (Finish drains again as a backstop).
            if (_manualFrameChannel != null)
            {
                while (_manualFrameChannel.Reader.TryRead(out SKBitmap? leftover))
                {
                    leftover.Dispose();
                }
            }
        }
    }

    internal void FinishManualScrollCapture(bool cancelled)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => FinishManualScrollCapture(cancelled));
            return;
        }

        if (!_manualScrollActive || _manualFinishing)
        {
            return;
        }

        _manualFinishing = true;
        _manualScrollActive = false;
        _manualCts?.Cancel();

        // Fire the async core; it awaits the pipeline (the barrier) before touching the bitmaps.
        _ = FinishManualScrollCaptureCoreAsync(cancelled);
    }

    private async Task FinishManualScrollCaptureCoreAsync(bool cancelled)
    {
        try
        {
            // Wait for the producer and consumer to stop so the accumulated/prev bitmaps are
            // no longer being mutated or disposed on the background thread.
            try
            {
                await (_manualPipelineTask ?? Task.CompletedTask);
            }
            catch (OperationCanceledException)
            {
                // Normal on cancel.
            }
            catch (Exception ex)
            {
                AppLog.Warning("ManualScroll.Pipeline", ex);
            }

            HideScrollingHintAction?.Invoke();

            // Backstop: dispose any frames produced after the consumer's own drain.
            if (_manualFrameChannel != null)
            {
                while (_manualFrameChannel.Reader.TryRead(out SKBitmap? leftover))
                {
                    leftover.Dispose();
                }
            }

            SKBitmap? result = _manualAccumulated;
            SKBitmap? prev = _manualPrevFrame;
            _manualAccumulated = null;
            _manualPrevFrame = null;

            try
            {
                if (!cancelled && result != null && result.Width > 0 && result.Height > 0)
                {
                    OpenStitchedPin(result);
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("ManualScroll.Finish", ex);
            }
            finally
            {
                result?.Dispose();
                prev?.Dispose();
            }
        }
        finally
        {
            _manualCts?.Dispose();
            _manualCts = null;
            _manualPipelineTask = null;
            _manualFrameChannel = null;
            _manualFinishing = false;
            CloseAction?.Invoke();
        }
    }
}
