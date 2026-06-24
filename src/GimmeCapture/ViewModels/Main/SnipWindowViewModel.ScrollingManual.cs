using System;
using System.Collections.Generic;
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
    // Pre-lock holder for the base (first) frame in stitch space. Used while the scroll axis is still
    // being detected; once stitching starts it is moved into _manualSegments and this goes null.
    private SKBitmap? _manualAccumulated;
    private SKBitmap? _manualPrevFrame;
    private bool _manualScrollActive;
    private bool _manualFinishing;

    // The accumulated long image, stored as ordered top-to-bottom pieces rather than one growing
    // bitmap. Each confirmed grow inserts a small sliver (O(newRows) copy) at the front (prepend) or
    // back (append); incoming frames are matched only against a bounded edge window derived from these
    // segments, so per-frame stitching stays O(frame) instead of O(strip). The full image is composited
    // once on finish. _manualTotalHeight is the running sum of segment heights (the strip length).
    private readonly List<SKBitmap> _manualSegments = new();
    private int _manualTotalHeight;

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

        // Start with an empty segment list; the base frame is moved into it lazily once stitching
        // begins (EnsureManualSegmentsInitialized). Dispose any leftovers defensively in case a prior
        // session didn't clean up.
        foreach (SKBitmap leftover in _manualSegments)
        {
            leftover.Dispose();
        }

        _manualSegments.Clear();
        _manualTotalHeight = 0;

        _manualPrevFrame = _manualAccumulated.Copy();
        _manualFinishing = false;
        _manualScrollActive = true;

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
                    // Not yet initialised when both the pre-lock base holder and the segment list are
                    // empty. After stitching starts _manualAccumulated goes null (its content moves into
                    // _manualSegments), so a null accumulated alone is not an error.
                    if (_manualPrevFrame == null || (_manualAccumulated == null && _manualSegments.Count == 0))
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
                        int newHeight = _manualTotalHeight;
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

    // Aligns a stitch-space frame against the real edges of the accumulated strip and only ever
    // extends by the validated overlap. We NEVER append content that doesn't overlap the strip, so
    // the same content can never be appended twice (no duplication, no runaway jumps). A frame that
    // doesn't overlap (a fast flick past, or a transient mismatch) is simply skipped; the anchor
    // still advances, so capture resumes as soon as the view overlaps the strip edge again. The
    // frame's ownership always moves into _manualPrevFrame. Returns whether the strip grew.
    private bool ApplyStitchFrame(SKBitmap stitchFrame)
    {
        EnsureManualSegmentsInitialized();

        bool grew = false;
        int cap = stitchFrame.Height; // bounded matching-window height (one frame's worth)

        // Match against the strip's TOP edge: a bounded window of at most `cap` rows built from the
        // leading segments. O(cap), not O(strip), so the consumer keeps pace however long the capture
        // grows — the fix for the "halfway then stuck" backpressure stall.
        SKBitmap headWin = ScrollStitcher.EdgeWindow(_manualSegments, cap, top: true);
        int headWinH = headWin.Height;

        ScrollStitcher.FrameAlignment alignHead;
        double diagBestScore;
        double diagAmbiguityGap;
        int diagSampleCount;
        int prependRows = 0;
        int appendRows = 0;
        try
        {
            alignHead = ScrollStitcher.AlignFrameToStrip(
                headWin, stitchFrame,
                out diagBestScore, out diagAmbiguityGap, out diagSampleCount,
                _manualMinOverlap, _manualIgnoreRight, ManualRowMismatchTolerance, headWinH);

            if (alignHead.Found && alignHead.Offset < 0)
            {
                prependRows = -alignHead.Offset; // frame overhangs the top: new head rows
            }

            if (_manualTotalHeight > cap)
            {
                // Strip is taller than one window: the top and bottom edges are distinct, so probe the
                // bottom edge separately for an append. A frame can only overhang one edge of a strip
                // taller than itself, so at most one of prepend/append fires.
                SKBitmap tailWin = ScrollStitcher.EdgeWindow(_manualSegments, cap, top: false);
                try
                {
                    ScrollStitcher.FrameAlignment alignTail = ScrollStitcher.AlignFrameToStrip(
                        tailWin, stitchFrame, _manualMinOverlap, _manualIgnoreRight, ManualRowMismatchTolerance, tailWin.Height);
                    if (alignTail.Found && alignTail.Offset + cap > tailWin.Height)
                    {
                        appendRows = alignTail.Offset + cap - tailWin.Height;
                    }
                }
                finally
                {
                    tailWin.Dispose();
                }
            }
            else if (prependRows == 0 && alignHead.Found && alignHead.Offset + cap > headWinH)
            {
                // Whole strip still fits in one window: the head window IS the strip, so the same
                // alignment also reports a bottom overhang.
                appendRows = alignHead.Offset + cap - headWinH;
            }

            if (prependRows > 0)
            {
                _manualSegments.Insert(0, ScrollStitcher.CropRows(stitchFrame, 0, prependRows));
                _manualTotalHeight += prependRows;
                grew = true;
            }
            else if (appendRows > 0)
            {
                _manualSegments.Add(ScrollStitcher.CropRows(stitchFrame, cap - appendRows, appendRows));
                _manualTotalHeight += appendRows;
                grew = true;
            }
            // else: frame lies fully inside the strip (scrolled back) — nothing new.

            // DIAGNOSTIC (temporary): per-frame alignment outcome + WHY it was rejected.
            // best=MAX => no offset scored (weight-starved / uniform frames); best>0.5 => frames don't
            // overlap well; small ambGap (<0.035) => rejected as ambiguous.
            string best = diagBestScore == double.MaxValue ? "MAX" : diagBestScore.ToString("F3");
            string ambGap = diagAmbiguityGap == double.MaxValue ? "none" : diagAmbiguityGap.ToString("F3");
            AppLog.Information(
                $"ManualScroll.Align horiz={_manualHorizontal} total={_manualTotalHeight} window={headWinH} " +
                $"frame={stitchFrame.Width}x{stitchFrame.Height} found={alignHead.Found} off={alignHead.Offset} " +
                $"prepend={prependRows} append={appendRows} grew={grew} best={best} ambGap={ambGap} samples={diagSampleCount}");
        }
        finally
        {
            headWin.Dispose();
        }

        _manualPrevFrame!.Dispose();
        _manualPrevFrame = stitchFrame; // anchor always advances; ownership moves into prev
        return grew;
    }

    // Moves the base (first) frame from the pre-lock holder into the segment list on the first stitch.
    // From then on _manualSegments is the source of truth for the full strip and _manualAccumulated
    // is null. No-op once initialised.
    private void EnsureManualSegmentsInitialized()
    {
        if (_manualSegments.Count > 0 || _manualAccumulated == null)
        {
            return;
        }

        _manualSegments.Add(_manualAccumulated); // move ownership; do not dispose here
        _manualTotalHeight = _manualAccumulated.Height;
        _manualAccumulated = null;
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

            SKBitmap? window = _manualAccumulated;
            SKBitmap? prev = _manualPrevFrame;
            SKBitmap[] segments = _manualSegments.ToArray();
            _manualAccumulated = null;
            _manualPrevFrame = null;
            _manualSegments.Clear();
            _manualTotalHeight = 0;

            // Materialise the long image from its pieces once (O(total)). If stitching never started
            // (axis never locked / no frame consumed) the base frame is still in the pre-lock holder.
            SKBitmap? result = null;
            try
            {
                if (!cancelled)
                {
                    if (segments.Length > 0)
                    {
                        result = ScrollStitcher.Concatenate(segments);
                    }
                    else if (window != null)
                    {
                        result = window.Copy();
                    }
                }

                if (result != null && result.Width > 0 && result.Height > 0)
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
                window?.Dispose();
                foreach (SKBitmap seg in segments)
                {
                    seg.Dispose();
                }

                prev?.Dispose();
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
