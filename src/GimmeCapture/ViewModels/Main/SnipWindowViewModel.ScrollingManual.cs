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
    // The seed / detection-phase strip (a plain bitmap) BEFORE any frame has stitched. Axis detection and
    // its 90° rotations run on this. On the first stitch it is promoted into _manualStrip (below) and nulled.
    private SKBitmap? _manualAccumulated;
    // The growing accumulated strip once stitching starts — keeps a long capture O(n) (cached signatures +
    // geometric-capacity growth) instead of the old O(n²) per-frame full-strip resample + copy.
    private ManualScrollStrip? _manualStrip;
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

    // null = scroll axis not yet detected; false = vertical; true = horizontal. While null, each
    // frame is tested against both hypotheses and the axis locks to whichever first shows real
    // movement (see ManualConsumerLoopAsync). Once horizontal, the accumulated strip and every
    // incoming frame live in rotated stitch space, and the final strip is rotated back on finish.
    private bool? _manualHorizontal;

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

    // Minimum overhang (physical px) the dominant axis must reach before the scroll direction is
    // locked. Because the accumulated strip stays the first frame while undecided, the measured
    // overhang grows as scrolling continues, so this is crossed within a few frames of real motion.
    // Prevents locking on a 1px jitter (hover/reflow/wheel drift) before the real scroll begins.
    private const int ManualAxisLockMinShift = 8;

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

    // Motion-consistency state (see ManualScrollMotionGate): frame-to-frame shift accumulated since the last
    // accepted placement, whether every shift in that run was measurable, the strip offset of the last
    // accepted frame (current strip coords), and how many candidates in a row the gate has vetoed.
    private long _manualMotionAccum;
    private bool _manualMotionValid;
    private int _manualMotionVetoStreak;
    private int _manualLastStripOffset;

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
        ManualScrollStrip? strip = _manualStrip;
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
        ScrollingCaptureDirection chosenDirection = _mainVm?.ScrollingCaptureDirection ?? ScrollingCaptureDirection.Auto;
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
        _manualAlignFailStreak = 0;
        _manualStallSignaled = false;
        _manualMotionAccum = 0;
        _manualMotionValid = true;
        _manualMotionVetoStreak = 0;
        _manualLastStripOffset = 0; // the seed frame sits at strip offset 0
        _manualPreviewThrottle.Reset(); // not started => the first grow publishes a thumbnail immediately

        // DIAGNOSTIC (temporary): records the resolved direction, region/strip dims and the active
        // match params so a captured log can show whether forcing is applied and how alignment behaves.
        AppLog.Information(
            $"ManualScroll.Start dir={chosenDirection} horiz={_manualHorizontal} physW={physW} physH={physH} " +
            $"strip={_manualAccumulated.Width}x{_manualAccumulated.Height} minOverlap={_manualMinOverlap} ignoreRight={_manualIgnoreRight}");

        // Register finish (Pin key) / cancel (Close key) as temporary GLOBAL hotkeys for the
        // session. RegisterHotKey delivers WM_HOTKEY to a hidden message window regardless of
        // focus — and even against elevated foreground windows — unlike the low-level keyboard
        // hook, which is silently blocked there. Routed via HandleGlobalHotkey. The snip key
        // hook + buttons remain as redundant fallbacks.
        _mainVm?.HotkeyService?.Register(HotkeyIds.ScrollingCaptureFinish, ActiveActionHotkey);
        _mainVm?.HotkeyService?.Register(HotkeyIds.ScrollingCaptureCancel, CloseHotkey);

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
                    // Valid state = an anchor plus a strip: the seed bitmap before the first stitch, or the
                    // promoted _manualStrip after it (ApplyStitchFrame nulls _manualAccumulated on promotion).
                    if (_manualPrevFrame == null || (_manualAccumulated == null && _manualStrip == null))
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
                        int newHeight = _manualStrip!.Height; // grew => the strip has been promoted
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

    // Aligns a stitch-space frame against the real edges of the accumulated strip and only ever
    // extends by the validated overlap. We NEVER append content that doesn't overlap the strip, so
    // the same content can never be appended twice (no duplication, no runaway jumps). A frame that
    // doesn't overlap (a fast flick past, or a transient mismatch) is simply skipped; the anchor
    // still advances, so capture resumes as soon as the view overlaps the strip edge again. The
    // frame's ownership always moves into _manualPrevFrame. Returns whether the strip grew.
    private bool ApplyStitchFrame(SKBitmap stitchFrame)
    {
        // Promote the seed bitmap into the growable strip on the first stitch. Axis detection + its 90°
        // rotations run before this on the plain _manualAccumulated bitmap; from here the strip owns the pixels.
        if (_manualStrip == null)
        {
            _manualStrip = new ManualScrollStrip(_manualAccumulated!, _manualIgnoreRight);
            _manualAccumulated!.Dispose();
            _manualAccumulated = null;
        }

        bool grew = false;
        int height = stitchFrame.Height;

        // Motion tracking: consecutive frames (40 ms apart) overlap almost entirely, so their relative shift
        // is cheap and nearly unambiguous. Accumulated since the last ACCEPTED placement, it predicts where
        // the next placement must land — the veto below uses it to reject "excellent-looking" strip matches
        // at physically impossible positions (repeated page content matching far away; see
        // ManualScrollMotionGate). An unmeasurable shift invalidates the prediction until the next accept.
        ScrollStitcher.VerticalShift frameShift = ScrollStitcher.FindVerticalShift(
            _manualPrevFrame!, stitchFrame, _manualMinOverlap, _manualIgnoreRight, ManualRowMismatchTolerance);
        if (frameShift.Found)
        {
            _manualMotionAccum += frameShift.Rows;
        }
        else
        {
            _manualMotionValid = false;
        }

        ScrollStitcher.FrameAlignment align = _manualStrip.Align(
            stitchFrame, _manualMinOverlap, ManualRowMismatchTolerance, height,
            out double diagBestScore, out double diagAmbiguityGap, out int diagSampleCount);

        bool motionVetoed = false;
        if (align.Found && ManualScrollMotionGate.ShouldVeto(
                _manualMotionValid, _manualMotionAccum, _manualLastStripOffset, align.Offset, height))
        {
            // Self-heal: if the prediction keeps vetoing real matches (its own drift), stop trusting it
            // rather than locking the capture out; the strip matcher then decides alone, as before.
            motionVetoed = true;
            _manualMotionVetoStreak++;
            if (_manualMotionVetoStreak >= ManualScrollMotionGate.MaxConsecutiveVetoes)
            {
                _manualMotionValid = false;
            }
        }

        if (align.Found && !motionVetoed)
        {
            int stripH = _manualStrip.Height;
            if (align.Offset < 0)
            {
                // Frame overhangs the top: prepend the new head rows.
                _manualStrip.Prepend(stitchFrame, -align.Offset);
                grew = true;
            }
            else if (align.Offset + height > stripH)
            {
                // Frame overhangs the bottom: append the frame rows past the strip's bottom.
                _manualStrip.Append(stitchFrame, align.Offset + height - stripH);
                grew = true;
            }
            // else: frame lies fully inside the strip (scrolled back) — nothing new.

            // A prepend rebases strip coordinates: the just-placed frame is now at offset 0.
            _manualLastStripOffset = align.Offset < 0 ? 0 : align.Offset;
            _manualMotionAccum = 0;
            _manualMotionValid = true;
            _manualMotionVetoStreak = 0;

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
                    $"ManualScroll.Stalled after {_manualAlignFailStreak} consecutive misses " +
                    $"(strip={_manualStrip.Width}x{_manualStrip.Height})");
                Dispatcher.UIThread.Post(() => UpdateScrollingStallAction?.Invoke(true));
            }
        }

        // DIAGNOSTIC (temporary): per-frame alignment outcome + WHY it was rejected.
        // best=MAX => no offset scored (weight-starved / uniform frames); best>0.5 => frames don't
        // overlap well; small ambGap (<0.035) => rejected as ambiguous.
        string best = diagBestScore == double.MaxValue ? "MAX" : diagBestScore.ToString("F3");
        string ambGap = diagAmbiguityGap == double.MaxValue ? "none" : diagAmbiguityGap.ToString("F3");
        string motion = _manualMotionValid ? _manualMotionAccum.ToString() : "off";
        AppLog.Information(
            $"ManualScroll.Align horiz={_manualHorizontal} strip={_manualStrip.Width}x{_manualStrip.Height} " +
            $"frame={stitchFrame.Width}x{stitchFrame.Height} found={align.Found} off={align.Offset} grew={grew} " +
            $"best={best} ambGap={ambGap} motion={motion} veto={motionVetoed} samples={diagSampleCount}");

        _manualPrevFrame!.Dispose();
        _manualPrevFrame = stitchFrame; // anchor always advances; ownership moves into prev
        return grew;
    }

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

    // Scroll axis unknown: test the frame against both the vertical (screen-space) and horizontal
    // (rotated) hypotheses. Lock to whichever first shows real edge movement (larger overhang wins
    // a rare diagonal nudge). Until something moves, just advance the anchor and stay undecided.
    private bool DetectAxisAndGrow(SKBitmap frame)
    {
        ScrollStitcher.FrameAlignment alignV = ScrollStitcher.AlignFrameToStrip(
            _manualAccumulated!, frame, _manualMinOverlapV, _manualIgnoreRightV, ManualRowMismatchTolerance, frame.Height);
        int growV = OverhangRows(alignV, _manualAccumulated!.Height, frame.Height);

        SKBitmap accRotated = ScrollStitcher.RotateCcw90(_manualAccumulated);
        SKBitmap frameRotated = ScrollStitcher.RotateCcw90(frame);
        ScrollStitcher.FrameAlignment alignH = ScrollStitcher.AlignFrameToStrip(
            accRotated, frameRotated, _manualMinOverlapH, _manualIgnoreRightH, ManualRowMismatchTolerance, frameRotated.Height);
        int growH = OverhangRows(alignH, accRotated.Height, frameRotated.Height);

        int dominant = Math.Max(growV, growH);
        int other = Math.Min(growV, growH);

        // Stay undecided until one axis shows a decisive, dominant movement: it must clear the
        // jitter threshold AND beat the other axis by 2x. A 1px vertical wobble on a horizontal
        // scroll (or vice-versa) must not lock the wrong axis — it would stitch garbage and pin
        // the wrong orientation. The overhang grows against the (still first-frame) strip as the
        // user keeps scrolling, so the real axis becomes unambiguous within a few frames.
        if (dominant < ManualAxisLockMinShift || dominant < other * 2)
        {
            accRotated.Dispose();
            frameRotated.Dispose();
            _manualPrevFrame!.Dispose();
            _manualPrevFrame = frame; // anchor advances, still in screen space
            return false;
        }

        if (growH > growV)
        {
            // Horizontal: switch the whole session into rotated stitch space.
            _manualHorizontal = true;
            _manualMinOverlap = _manualMinOverlapH;
            _manualIgnoreRight = _manualIgnoreRightH;
            _manualMaxHeight = _manualMaxHeightH;

            _manualAccumulated.Dispose();
            _manualAccumulated = accRotated; // keep the rotated accumulated strip
            frame.Dispose();                 // original screen frame no longer needed
            return ApplyStitchFrame(frameRotated);
        }

        // Vertical: keep screen space, drop the rotated probes.
        _manualHorizontal = false;
        _manualMinOverlap = _manualMinOverlapV;
        _manualIgnoreRight = _manualIgnoreRightV;
        _manualMaxHeight = _manualMaxHeightV;
        accRotated.Dispose();
        frameRotated.Dispose();
        return ApplyStitchFrame(frame);
    }

    // New rows a frame contributes past an edge of the strip (top or bottom overhang), else 0.
    private static int OverhangRows(ScrollStitcher.FrameAlignment align, int stripHeight, int frameHeight)
    {
        if (!align.Found)
        {
            return 0;
        }

        if (align.Offset < 0)
        {
            return -align.Offset; // overhangs the top
        }

        if (align.Offset + frameHeight > stripHeight)
        {
            return align.Offset + frameHeight - stripHeight; // overhangs the bottom
        }

        return 0;
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

            // Assemble the final strip: from the growable strip once stitching started, else the seed bitmap
            // (session ended before any frame stitched). ToLogicalBitmap returns a fresh, owned bitmap.
            ManualScrollStrip? strip = _manualStrip;
            SKBitmap? result = strip != null ? strip.ToLogicalBitmap() : _manualAccumulated;
            SKBitmap? prev = _manualPrevFrame;
            _manualStrip = null;
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
            _mainVm?.HotkeyService?.Unregister(HotkeyIds.ScrollingCaptureFinish);
            _mainVm?.HotkeyService?.Unregister(HotkeyIds.ScrollingCaptureCancel);
            _manualCts?.Dispose();
            _manualCts = null;
            _manualPipelineTask = null;
            _manualFrameChannel = null;
            _manualFinishing = false;
            _manualHorizontal = null;
            CloseAction?.Invoke();
        }
    }
}
