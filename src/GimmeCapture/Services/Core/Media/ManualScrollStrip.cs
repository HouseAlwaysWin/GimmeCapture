using System;
using SkiaSharp;

namespace GimmeCapture.Services.Core.Media;

/// <summary>
/// The growing accumulated strip for manual scrolling capture, kept in one place so a long capture stays
/// O(total rows) instead of O(n²). Previously each stitched frame re-sampled the whole strip's row
/// signatures AND full-copied the whole strip into a new, one-row-taller bitmap — both O(strip height)
/// per frame, so a tall capture cost O(n²).
/// <para>
/// Here the strip pixels live in one bitmap with spare capacity on both ends: append/prepend blit only the
/// new rows and extend a cached row-signature buffer in lockstep, so <see cref="Align"/> never re-samples
/// the strip and growth is amortised O(1) per row (the buffer doubles when a side is exhausted). The final
/// strip is produced with a single crop-copy in <see cref="ToLogicalBitmap"/>.
/// </para>
/// <para>
/// Behaviourally identical to the old per-frame <c>ScrollStitcher.AlignFrameToStrip</c> +
/// <c>Append</c>/<c>Prepend</c> chain: the cached signatures are byte-for-byte what
/// <c>ComputeRowSignatures</c> would produce on the assembled strip, and the accumulated pixels match the
/// iterative append/prepend (proven by <c>ManualScrollStripTests</c>). Not thread-safe — the scrolling
/// consumer owns it on a single thread.
/// </para>
/// </summary>
internal sealed class ManualScrollStrip : IDisposable
{
    private readonly int _width;
    private readonly int _ignoreRight;
    private readonly int _sampleCount;
    private readonly int _stride;      // bytes per signature row (_sampleCount * 4)
    private readonly int[] _columns;   // sampled column x-positions (fixed for the session)

    // Pixels: allocated height = _capacity; the logical strip occupies rows [_top, _top + _height).
    private SKBitmap _buffer;
    // Row signatures aligned to buffer rows: buffer row k at _sig[k * _stride ..]. Valid for the logical rows.
    private byte[] _sig;
    private int _capacity;
    private int _top;
    private int _height;

    /// <summary>Seeds the strip from the first captured frame. <paramref name="ignoreRightColumns"/> is fixed
    /// for the session (it defines the signature sampling) and must match every subsequent <see cref="Align"/>.</summary>
    public ManualScrollStrip(SKBitmap seed, int ignoreRightColumns)
    {
        ArgumentNullException.ThrowIfNull(seed);

        _width = seed.Width;
        _ignoreRight = Math.Max(0, ignoreRightColumns);
        _columns = ScrollStitcher.BuildSampleColumns(_width, _ignoreRight, out _sampleCount);
        _stride = _sampleCount * 4;

        _height = Math.Max(0, seed.Height);
        // Generous room on both ends so early scrolling (either direction) doesn't immediately realloc.
        _capacity = Math.Max(1, _height * 3);
        _top = _height; // one strip-height of head room for upward scrolls
        _buffer = new SKBitmap(new SKImageInfo(Math.Max(1, _width), _capacity, SKColorType.Bgra8888, SKAlphaType.Premul));
        _sig = new byte[Math.Max(1, _capacity * _stride)];

        if (_height > 0)
        {
            BlitRows(seed, 0, _top, _height);
            SampleInto(seed, 0, _top, _height);
        }
    }

    public int Width => _width;

    public int Height => _height;

    /// <summary>
    /// Aligns <paramref name="frame"/> against the current strip using the cached signatures. Same contract
    /// as <c>ScrollStitcher.AlignFrameToStrip</c>: <c>Offset &lt; 0</c> => frame overhangs the top,
    /// <c>Offset + frame.Height &gt; Height</c> => overhangs the bottom, otherwise it lies inside the strip.
    /// </summary>
    public ScrollStitcher.FrameAlignment Align(
        SKBitmap frame,
        int minOverlapRows,
        double maxRowMismatchRatio,
        int searchMargin,
        out double bestScore,
        out double ambiguityGap,
        out int sampleCount)
        => ScrollStitcher.AlignCore(
            _sig, _top * _stride, _height, _sampleCount, frame,
            out bestScore, out ambiguityGap, out sampleCount,
            minOverlapRows, _ignoreRight, maxRowMismatchRatio, searchMargin);

    /// <summary>Appends the bottom <paramref name="newRows"/> rows of <paramref name="frame"/> (new content below).</summary>
    public void Append(SKBitmap frame, int newRows)
    {
        ArgumentNullException.ThrowIfNull(frame);
        newRows = Math.Clamp(newRows, 0, frame.Height);
        if (newRows <= 0)
        {
            return;
        }

        EnsureRoom(topRoom: 0, bottomRoom: newRows);
        int destRow = _top + _height;
        int srcTop = frame.Height - newRows;
        BlitRows(frame, srcTop, destRow, newRows);
        SampleInto(frame, srcTop, destRow, newRows);
        _height += newRows;
    }

    /// <summary>Prepends the top <paramref name="newRows"/> rows of <paramref name="frame"/> (new content above).</summary>
    public void Prepend(SKBitmap frame, int newRows)
    {
        ArgumentNullException.ThrowIfNull(frame);
        newRows = Math.Clamp(newRows, 0, frame.Height);
        if (newRows <= 0)
        {
            return;
        }

        EnsureRoom(topRoom: newRows, bottomRoom: 0);
        int destRow = _top - newRows;
        BlitRows(frame, 0, destRow, newRows);
        SampleInto(frame, 0, destRow, newRows);
        _top -= newRows;
        _height += newRows;
    }

    /// <summary>Produces the final strip as a fresh, exactly logical-height bitmap (one crop-copy).</summary>
    public SKBitmap ToLogicalBitmap()
    {
        var output = new SKBitmap(new SKImageInfo(Math.Max(1, _width), Math.Max(1, _height), SKColorType.Bgra8888, SKAlphaType.Premul));
        if (_height <= 0 || _width <= 0)
        {
            return output;
        }

        using var canvas = new SKCanvas(output);
        var src = new SKRect(0, _top, _width, _top + _height);
        var dst = new SKRect(0, 0, _width, _height);
        canvas.DrawBitmap(_buffer, src, dst);
        return output;
    }

    /// <summary>
    /// Renders the whole logical strip into a fresh, aspect-preserved BGRA bitmap no larger than
    /// <paramref name="maxWidth"/> × <paramref name="maxHeight"/> (never upscaled). Used to drive the live
    /// scrolling-capture preview cheaply — the caller owns and disposes the result. Safe on the consumer
    /// thread (the strip's single owner); does not mutate the strip.
    /// </summary>
    public SKBitmap RenderScaledPreview(int maxWidth, int maxHeight)
    {
        int sw = Math.Max(1, _width);
        int sh = Math.Max(1, _height);
        int capW = Math.Max(1, maxWidth);
        int capH = Math.Max(1, maxHeight);

        // Fit within the cap on both axes, preserving aspect; clamp to 1 so a small strip stays crisp
        // rather than being blown up (the preview window upscales for display if it wants to).
        double scale = Math.Min(Math.Min((double)capW / sw, (double)capH / sh), 1.0);
        int dw = Math.Clamp((int)Math.Round(sw * scale), 1, capW);
        int dh = Math.Clamp((int)Math.Round(sh * scale), 1, capH);

        var info = new SKImageInfo(dw, dh, SKColorType.Bgra8888, SKAlphaType.Premul);
        if (_height <= 0 || _width <= 0)
        {
            return new SKBitmap(info);
        }

        // Crop the logical region (a zero-copy view over the buffer) then downscale with linear sampling —
        // matches the Resize(..., SKFilterMode.Linear) idiom used elsewhere and keeps the thumbnail smooth.
        using var region = new SKBitmap();
        if (_buffer.ExtractSubset(region, new SKRectI(0, _top, _width, _top + _height)))
        {
            SKBitmap? resized = region.Resize(info, new SKSamplingOptions(SKFilterMode.Linear));
            if (resized != null)
            {
                return resized;
            }
        }

        // Fallback (subset/resize unavailable): a direct scaled blit with the canvas default sampling.
        var output = new SKBitmap(info);
        using var canvas = new SKCanvas(output);
        canvas.Clear(SKColors.Black);
        canvas.DrawBitmap(_buffer, new SKRect(0, _top, _width, _top + _height), new SKRect(0, 0, dw, dh));
        return output;
    }

    // Guarantees `topRoom` rows above and `bottomRoom` rows below the logical window. When either side is
    // exhausted, reallocates with at least a strip-height of slack on each side (amortised O(1) per row).
    private void EnsureRoom(int topRoom, int bottomRoom)
    {
        if (_top >= topRoom && _capacity - (_top + _height) >= bottomRoom)
        {
            return;
        }

        int extraTop = Math.Max(topRoom, _height);
        int extraBottom = Math.Max(bottomRoom, _height);
        int newCapacity = _height + extraTop + extraBottom;
        int newTop = extraTop;

        var newBuffer = new SKBitmap(new SKImageInfo(Math.Max(1, _width), newCapacity, SKColorType.Bgra8888, SKAlphaType.Premul));
        var newSig = new byte[Math.Max(1, newCapacity * _stride)];
        if (_height > 0 && _width > 0)
        {
            using (var canvas = new SKCanvas(newBuffer))
            {
                var src = new SKRect(0, _top, _width, _top + _height);
                var dst = new SKRect(0, newTop, _width, newTop + _height);
                canvas.DrawBitmap(_buffer, src, dst);
            }

            if (_stride > 0)
            {
                Array.Copy(_sig, _top * _stride, newSig, newTop * _stride, _height * _stride);
            }
        }

        _buffer.Dispose();
        _buffer = newBuffer;
        _sig = newSig;
        _capacity = newCapacity;
        _top = newTop;
    }

    private void BlitRows(SKBitmap src, int srcRow, int destRow, int rowCount)
    {
        if (_width <= 0 || rowCount <= 0)
        {
            return;
        }

        using var canvas = new SKCanvas(_buffer);
        var s = new SKRect(0, srcRow, _width, srcRow + rowCount);
        var d = new SKRect(0, destRow, _width, destRow + rowCount);
        canvas.DrawBitmap(src, s, d);
    }

    private void SampleInto(SKBitmap src, int srcRow, int destRow, int rowCount)
    {
        if (_stride == 0)
        {
            return;
        }

        ScrollStitcher.SampleRowsInto(src, srcRow, rowCount, _columns, _sampleCount, _sig, destRow * _stride);
    }

    public void Dispose() => _buffer.Dispose();
}
