using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Avalonia.Threading;
using GimmeCapture.Models;
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
    // The seed (a plain bitmap) BEFORE any frame has stitched. Axis detection and its 90° rotations run on
    // this. On the first stitch it is handed to _manualStitcher (below) and nulled.
    private SKBitmap? _manualAccumulated;
    // Places each frame and grows the strip once the axis is known (ManualScrollStitcher: follows the motion
    // frame to frame and keeps an app's fixed bars out of the strip).
    private ManualScrollStitcher? _manualStitcher;
    // The last frame while the axis is still being detected; the stitcher keeps its own after that.
    private SKBitmap? _manualPrevFrame;
    private bool _manualScrollActive;
    private bool _manualFinishing;

    // Active stitch-space parameters for the locked scroll axis. The strip is always stitched
    // along its vertical axis (its "height"); for a horizontal scroll each frame is rotated 90°
    // into this space (see _manualHorizontal). The active set below is chosen from the per-axis
    // candidates once the scroll direction is detected.
    private int _manualMaxHeight;
    private int _manualMinOverlap;
    private int _manualIgnoreRight;

    // Per-axis candidate parameters, precomputed at session start. Vertical: scroll axis = region
    // height, cross-axis right-edge exclusion = region width. Horizontal: frames are rotated 90° CCW
    // into stitch space, so scroll axis = region width, cross-axis = region height.
    private int _manualMaxHeightV, _manualMaxHeightH;
    private int _manualMinOverlapV, _manualMinOverlapH;
    private int _manualIgnoreRightV, _manualIgnoreRightH;

    // Rows next to each edge (physical px, either axis) kept out of the strip until the content has scrolled past
    // them: where apps float a date pill or a "jump to present" bar (ManualScrollStitcher.EdgeGuardRows).
    private int _manualEdgeGuard;

    // null = scroll axis not yet detected; false = vertical; true = horizontal. While null, each
    // frame is tested against both hypotheses and the axis locks once _manualAxisDetector settles it
    // (see ManualConsumerLoopAsync). Once horizontal, the accumulated strip and every
    // incoming frame live in rotated stitch space, and the final strip is rotated back on finish.
    private bool? _manualHorizontal;
    private ManualScrollAxisDetector? _manualAxisDetector;

    private CancellationTokenSource? _manualCts;
    private Task? _manualPipelineTask;
    private Channel<SKBitmap>? _manualFrameChannel;

    // Fraction of overlap rows allowed to differ. Higher because rows are matched by
    // pixel-similarity (not byte-exact), so a chat re-rendering a few rows per frame
    // (Discord, GPU-composited apps) still stitches.
    private const double ManualRowMismatchTolerance = 0.35;
    // Bounded capture queue: a few frames of slack so a slow stitch (Append on a tall strip)
    // doesn't stall capture, without unbounded memory growth.
    private const int ManualQueueCapacity = 8;

    /// <summary>True while a manual scrolling-capture session is running (overlay hidden).</summary>
    public bool IsManualScrollActive => _manualScrollActive;

    /// <summary>Show the on-screen hint (capture-excluded) with the localized instruction.</summary>
    public Action? ShowScrollingHintAction { get; set; }

    /// <summary>Update the hint with the current captured height (physical px).</summary>
    public Action<int>? UpdateScrollingHintAction { get; set; }

    /// <summary>
    /// Signals the hint window that stitching has lost/regained track of the content (true = a run of
    /// frames failed to align — typically after scrolling too fast past the strip's edge; false = a frame
    /// matched again). Lets the user recover mid-capture by scrolling back, instead of discovering a
    /// stub image only after finishing.
    /// </summary>
    public Action<bool>? UpdateScrollingStallAction { get; set; }

    // ~25 captured frames/second: signal a stall after ~1.2s of consecutive misses. Shorter would flash
    // on transient mismatches (mid-animation frames); longer leaves the user scrolling into the void.
    private const int ManualStallSignalFrames = 30;
    private int _manualAlignFailStreak;
    private bool _manualStallSignaled;

    /// <summary>
    /// Publishes the latest downscaled thumbnail of the growing strip to the live preview window. Posted
    /// from the consumer thread (throttled) so the user can watch stitching progress and catch a
    /// misplacement/stall the moment it happens, instead of only in the finished pin.
    /// </summary>
    public Action<Avalonia.Media.Imaging.Bitmap>? UpdateScrollingPreviewAction { get; set; }

    // Live-preview repaint budget: ~6 thumbnails/second. Frequent enough to look live, rare enough that the
    // full-strip downscale + BGRA copy never competes with stitching for the consumer thread.
    private static readonly TimeSpan ManualPreviewInterval = TimeSpan.FromMilliseconds(160);
    private readonly Stopwatch _manualPreviewThrottle = new();
    // Physical-pixel budget for each rendered thumbnail. Width ≈ the preview window's narrow display width at
    // 2× DPI (kept crisp); height generous so the whole strip stays in one image until it is very long.
    private const int ManualPreviewMaxWidthPx = 420;
    private const int ManualPreviewMaxHeightPx = 2000;

    /// <summary>Hide/close the hint window.</summary>
    public Action? HideScrollingHintAction { get; set; }

    // Renders a throttled, downscaled thumbnail of the current strip and posts it to the preview window.
    // Runs on the consumer thread (the strip's sole owner); the SKBitmap->Bitmap conversion is a plain pixel
    // copy with no UI-thread affinity, so only the final assignment is marshalled onto the UI thread.
    private void PublishManualScrollPreview()
    {
        ManualScrollStitcher? strip = _manualStitcher;
        if (strip == null || UpdateScrollingPreviewAction == null)
        {
            return;
        }

        // Throttle to the repaint budget. Reset() (not Restart()) at session start leaves the watch stopped,
        // so the first grow publishes immediately; afterwards it fires at most once per interval.
        if (_manualPreviewThrottle.IsRunning && _manualPreviewThrottle.Elapsed < ManualPreviewInterval)
        {
            return;
        }
        _manualPreviewThrottle.Restart();

        SKBitmap? scaled = null;
        try
        {
            scaled = strip.RenderScaledPreview(ManualPreviewMaxWidthPx, ManualPreviewMaxHeightPx);
            if (GimmeCapture.ViewModels.Floating.FloatingBitmapConversionHelper
                    .TryCreateDetachedBitmapFromSkBitmap(scaled, out var bitmap, out _)
                && bitmap != null)
            {
                Dispatcher.UIThread.Post(() => UpdateScrollingPreviewAction?.Invoke(bitmap));
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("ManualScroll.Preview", ex);
        }
        finally
        {
            scaled?.Dispose();
        }
    }

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
        int physW = (int)(SelectionRect.Width * scaling);
        int ignoreBand = (int)(180 * scaling);
        int ignoreMin = (int)(24 * scaling);
        _manualEdgeGuard = (int)(48 * scaling);

        // Vertical hypothesis: scroll axis = height, cross-axis (right-edge exclusion) = width.
        // The exclusion drops the dynamic right-side region (Discord's hover reaction toolbar,
        // "已讀 N" read-receipts, scrollbar) from matching, so the same content still matches while
        // those overlays come and go — otherwise it fails to align and gets re-appended.
        _manualMinOverlapV = Math.Max(8, physH / 10);
        _manualIgnoreRightV = Math.Clamp(ignoreBand, ignoreMin, Math.Max(ignoreMin, physW * 35 / 100));
        _manualMaxHeightV = Math.Max(physH, physH * 40);

        // Horizontal hypothesis (frames rotated 90° CCW into stitch space): scroll axis = width,
        // cross-axis = height. After CCW rotation the region's bottom edge — where a horizontal
        // scrollbar sits — maps to the stitch-space right edge. Unlike the vertical case (a wide
        // dynamic right strip: scrollbar + hover toolbar + read-receipts), the only dynamic element
        // along a horizontal scroll's bottom is the thin scrollbar, so exclude just a thin band.
        // Excluding a large fraction here would discard most of each column's vertical profile —
        // the exact signal the rotated matcher needs — and horizontal/text content already has
        // weaker per-column distinctiveness than per-row.
        _manualMinOverlapH = Math.Max(8, physW / 10);
        _manualIgnoreRightH = Math.Clamp((int)(32 * scaling), 8, Math.Max(8, physH / 8));
        _manualMaxHeightH = Math.Max(physW, physW * 40);

        // Direction is read once here (before the background tasks start) from the user's persisted
        // choice. Auto leaves _manualHorizontal undecided so DetectAxisAndGrow picks the axis from
        // the first real scroll; Vertical/Horizontal lock the axis up front.
        ScrollingCaptureDirection chosenDirection = _settings?.ScrollingCaptureDirection ?? ScrollingCaptureDirection.Auto;
        _manualAxisDetector = null; // only Auto has one (below)
        switch (chosenDirection)
        {
            case ScrollingCaptureDirection.Vertical:
                // Locked vertical: vertical param set active, accumulated stays in screen space.
                _manualHorizontal = false;
                _manualMinOverlap = _manualMinOverlapV;
                _manualIgnoreRight = _manualIgnoreRightV;
                _manualMaxHeight = _manualMaxHeightV;
                break;
            case ScrollingCaptureDirection.Horizontal:
                // Locked horizontal: horizontal param set active. GrowAlongLockedAxis rotates each
                // incoming frame CCW and expects the accumulated already in rotated stitch space, so
                // the first accumulated is rotated below before _manualPrevFrame is taken.
                _manualHorizontal = true;
                _manualMinOverlap = _manualMinOverlapH;
                _manualIgnoreRight = _manualIgnoreRightH;
                _manualMaxHeight = _manualMaxHeightH;
                break;
            default:
                // Auto: start undecided with the vertical set active (the zero-rotation default).
                _manualHorizontal = null;
                _manualMinOverlap = _manualMinOverlapV;
                _manualIgnoreRight = _manualIgnoreRightV;
                _manualMaxHeight = _manualMaxHeightV;
                _manualAxisDetector = new ManualScrollAxisDetector(
                    _manualMinOverlapV, _manualIgnoreRightV, _manualMinOverlapH, _manualIgnoreRightH, ManualRowMismatchTolerance);
                break;
        }

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

        if (_manualHorizontal == true)
        {
            // Move the first accumulated into rotated stitch space so the locked-horizontal consumer
            // (and the CW rotate-back in the finish path) operate on consistently rotated data.
            SKBitmap rotated = ScrollStitcher.RotateCcw90(_manualAccumulated);
            _manualAccumulated.Dispose();
            _manualAccumulated = rotated;
        }

        _manualPrevFrame = _manualAccumulated.Copy();
        _manualFinishing = false;
        _manualScrollActive = true;
        // Hand the action/close keys over to the session-scoped registrations made below.
        SyncUnfocusedOverlayHotkeys();
        // Scrolling capture is inherently live — every strip is a fresh grab of the scrolled target.
        SyncSurfaceActivity();
        _manualAlignFailStreak = 0;
        _manualStallSignaled = false;
        _manualPreviewThrottle.Reset(); // not started => the first grow publishes a thumbnail immediately

        // DIAGNOSTIC (temporary): records the resolved direction, region/strip dims and the active
        // match params so a captured log can show whether forcing is applied and how alignment behaves.
        AppLog.Information(
            "ManualScroll.Start",
            $"dir={chosenDirection} horiz={_manualHorizontal} physW={physW} physH={physH} " +
            $"strip={_manualAccumulated.Width}x{_manualAccumulated.Height} minOverlap={_manualMinOverlap} ignoreRight={_manualIgnoreRight}");

        // Register finish (Pin key) / cancel (Close key) as temporary GLOBAL hotkeys for the
        // session. RegisterHotKey delivers WM_HOTKEY to a hidden message window whichever of the
        // user's windows has focus; routed via HandleGlobalHotkey. Not over a window run as
        // administrator: Windows withholds WM_HOTKEY there as it does the hook (see ElevatedLaunch).
        // The snip key hook + buttons remain as redundant fallbacks.
        _services?.Hotkeys.Register(HotkeyIds.ScrollingCaptureFinish, ActiveActionHotkey);
        _services?.Hotkeys.Register(HotkeyIds.ScrollingCaptureCancel, CloseHotkey);

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

    // Stitches frames in order off the UI thread. Owns _manualAccumulated/_manualPrevFrame
    // for the duration of the session.
    private async Task ManualConsumerLoopAsync(CancellationToken token)
    {
        try
        {
            await foreach (SKBitmap frame in _manualFrameChannel!.Reader.ReadAllAsync(token))
            {
                try
                {
                    // Valid state = the stitcher, or before the first stitch the seed bitmap plus an anchor
                    // (ApplyStitchFrame hands both over on the first stitch).
                    if (_manualStitcher == null && (_manualPrevFrame == null || _manualAccumulated == null))
                    {
                        frame.Dispose();
                        continue;
                    }

                    // While the scroll axis is unknown, test the frame against both hypotheses and
                    // lock to whichever first shows real movement; afterwards just grow along the
                    // locked axis. Either way the frame's ownership moves into _manualPrevFrame.
                    bool grew = _manualHorizontal == null
                        ? DetectAxisAndGrow(frame)
                        : GrowAlongLockedAxis(frame);

                    if (grew)
                    {
                        int newHeight = _manualStitcher!.Height; // grew => the stitcher has started
                        Dispatcher.UIThread.Post(() => UpdateScrollingHintAction?.Invoke(newHeight));
                        PublishManualScrollPreview();

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
                    if (!ReferenceEquals(frame, _manualPrevFrame) && _manualStitcher?.Holds(frame) != true)
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

    // Hands a stitch-space frame to the stitcher, which places it where the motion followed frame to frame
    // says it is and grows the strip by what it adds (ManualScrollStitcher). A frame that cannot be placed (a
    // fast flick past the strip's edge, a transient mismatch) is skipped; the motion keeps following the view,
    // so capture resumes as soon as it meets the strip again. The frame's ownership moves into the stitcher.
    // Returns whether the strip grew.
    private bool ApplyStitchFrame(SKBitmap stitchFrame)
    {
        // Start the stitcher on the first stitch. Axis detection + its 90° rotations run before this on the
        // plain _manualAccumulated bitmap and _manualPrevFrame; from here the stitcher keeps its own.
        if (_manualStitcher == null)
        {
            _manualStitcher = new ManualScrollStitcher(
                _manualAccumulated!, _manualMinOverlap, _manualIgnoreRight, ManualRowMismatchTolerance, _manualEdgeGuard);
            _manualAccumulated!.Dispose();
            _manualAccumulated = null;
            _manualPrevFrame?.Dispose();
            _manualPrevFrame = null;
        }

        ManualScrollStitcher stitcher = _manualStitcher;
        int frameWidth = stitchFrame.Width;
        int frameHeight = stitchFrame.Height;
        ManualScrollStep step = stitcher.Add(stitchFrame);

        if (step.Placed)
        {
            _manualAlignFailStreak = 0;
            if (_manualStallSignaled)
            {
                _manualStallSignaled = false;
                Dispatcher.UIThread.Post(() => UpdateScrollingStallAction?.Invoke(false));
            }
        }
        else
        {
            // A sustained run of misses means the view no longer overlaps the strip (scrolled too fast
            // past its edge) — every further frame is lost content. Warn so the user scrolls back a bit;
            // alignment then recovers on its own and the warning clears above. Logged once per stall.
            _manualAlignFailStreak++;
            if (!_manualStallSignaled && _manualAlignFailStreak >= ManualStallSignalFrames)
            {
                _manualStallSignaled = true;
                AppLog.Information(
                    "ManualScroll.Stalled",
                    $"after {_manualAlignFailStreak} consecutive misses (strip={stitcher.Width}x{stitcher.Height})");
                Dispatcher.UIThread.Post(() => UpdateScrollingStallAction?.Invoke(true));
            }
        }

        // Per-frame placement + WHY a frame was not placed, for chasing a bad stitch. guided = looked for only
        // where the motion says (near=<predicted offset>); tentative = found by searching the whole strip, waiting
        // for the next frame to vouch for it; best=MAX => nothing scored there (weight-starved /
        // uniform frames, or past the strip's edge); best above 0.5 guided or 0.2 unguided => no real overlap;
        // small ambGap (<0.035, unguided only) => rejected as ambiguous. bands = the fixed rows kept out of the
        // strip at the top/bottom. A debug line, so with debug logging off nothing in it is evaluated: as an
        // information line it took the log file's lock and a flush about 25 times a second.
        AppLog.Debug(
            "ManualScroll.Align",
            $"horiz={_manualHorizontal} strip={stitcher.Width}x{stitcher.Height} frame={frameWidth}x{frameHeight} " +
            $"found={step.Placed} tentative={step.Tentative} off={step.Offset} grew={step.Grew} guided={step.Guided} near={step.Predicted} " +
            $"best={DiagnosticScore(step.BestScore, "MAX")} ambGap={DiagnosticScore(step.AmbiguityGap, "none")} " +
            $"bands={stitcher.TopBand}/{stitcher.BottomBand}");

        return step.Grew;
    }

    // An alignment score for the trace above, or `none` when nothing scored.
    private static string DiagnosticScore(double score, string none) =>
        score == double.MaxValue ? none : score.ToString("F3");

    // Scroll axis already locked: rotate the screen-space frame into stitch space when horizontal,
    // then stitch. Consumes (disposes) the original screen frame for the horizontal case.
    private bool GrowAlongLockedAxis(SKBitmap frame)
    {
        if (_manualHorizontal == true)
        {
            SKBitmap rotated = ScrollStitcher.RotateCcw90(frame);
            frame.Dispose();
            return ApplyStitchFrame(rotated);
        }

        return ApplyStitchFrame(frame);
    }

    // Scroll axis unknown: ask the detector, which measures the motion since the first frame along both axes
    // (ManualScrollAxisDetector). Until it settles, just advance the anchor and stay undecided.
    private bool DetectAxisAndGrow(SKBitmap frame)
    {
        bool? horizontal = _manualAxisDetector?.Decide(_manualAccumulated!, frame);
        if (horizontal == null)
        {
            _manualPrevFrame!.Dispose();
            _manualPrevFrame = frame; // anchor advances, still in screen space
            return false;
        }

        if (horizontal == true)
        {
            // Horizontal: switch the whole session into rotated stitch space.
            _manualHorizontal = true;
            _manualMinOverlap = _manualMinOverlapH;
            _manualIgnoreRight = _manualIgnoreRightH;
            _manualMaxHeight = _manualMaxHeightH;

            SKBitmap accRotated = ScrollStitcher.RotateCcw90(_manualAccumulated!);
            _manualAccumulated!.Dispose();
            _manualAccumulated = accRotated;
            SKBitmap frameRotated = ScrollStitcher.RotateCcw90(frame);
            frame.Dispose(); // original screen frame no longer needed
            return ApplyStitchFrame(frameRotated);
        }

        // Vertical: keep screen space.
        _manualHorizontal = false;
        _manualMinOverlap = _manualMinOverlapV;
        _manualIgnoreRight = _manualIgnoreRightV;
        _manualMaxHeight = _manualMaxHeightV;
        return ApplyStitchFrame(frame);
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
        SyncSurfaceActivity();
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

            // Assemble the final strip: from the stitcher once stitching started, else the seed bitmap
            // (session ended before any frame stitched). ToBitmap returns a fresh, owned bitmap.
            ManualScrollStitcher? strip = _manualStitcher;
            SKBitmap? result = strip != null ? strip.ToBitmap() : _manualAccumulated;
            SKBitmap? prev = _manualPrevFrame;
            // Once a capture: how its stitching went, for chasing a bad one without debug logging. As a message: an event
            // name is cut at 120 characters, which took this line's started= and result= in every capture.
            AppLog.Information(
                "ManualScroll.Finish",
                $"cancelled={cancelled} horiz={_manualHorizontal} " +
                $"{strip?.Describe() ?? "stitcher=none"} result={result?.Width ?? 0}x{result?.Height ?? 0}");
            _manualStitcher = null;
            _manualAccumulated = null;
            _manualPrevFrame = null;

            try
            {
                if (!cancelled && result != null && result.Width > 0 && result.Height > 0)
                {
                    if (_manualHorizontal == true)
                    {
                        // Strip is in CCW-rotated stitch space — rotate back (CW) to screen orientation.
                        using SKBitmap output = ScrollStitcher.RotateCw90(result);
                        OpenStitchedPin(output);
                    }
                    else
                    {
                        OpenStitchedPin(result);
                    }
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
                strip?.Dispose();
            }
        }
        finally
        {
            _services?.Hotkeys.Unregister(HotkeyIds.ScrollingCaptureFinish);
            _services?.Hotkeys.Unregister(HotkeyIds.ScrollingCaptureCancel);
            _manualCts?.Dispose();
            _manualCts = null;
            _manualPipelineTask = null;
            _manualFrameChannel = null;
            _manualFinishing = false;
            _manualHorizontal = null;
            _manualAxisDetector = null;
            CloseAction?.Invoke();
        }
    }
}
