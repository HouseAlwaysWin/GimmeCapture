using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using SkiaSharp;

namespace GimmeCapture.Services.Core.AI;

/// <summary>
/// Greedy per-frame object tracking for redaction using the single-image SAM2: from a seed point it
/// samples the clip at a fixed interval, decodes each sampled frame, runs SAM2 seeded at the previous
/// box's centre, and turns each resulting mask's tight bounds into a normalized <see cref="RedactionKeyframe"/>.
/// The exporter's interpolation fills the gaps between samples. Not true memory-based video tracking —
/// it can drift on fast motion / occlusion — but it reuses the existing image models (no new download).
/// </summary>
public static class Sam2RedactionTracker
{
    /// <summary>
    /// Tracks the object seeded at the normalized point (<paramref name="seedNormX"/>, <paramref name="seedNormY"/>)
    /// from <paramref name="startSeconds"/> to <paramref name="endSeconds"/>, sampling every
    /// <paramref name="intervalSeconds"/>. Returns the keyframes (may be empty if SAM2 finds nothing).
    /// </summary>
    public static async Task<List<RedactionKeyframe>> TrackAsync(
        SAM2Service sam2,
        string videoPath,
        int frameWidth,
        int frameHeight,
        double startSeconds,
        double endSeconds,
        double seedNormX,
        double seedNormY,
        double seedNormWidth,
        double seedNormHeight,
        double intervalSeconds,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sam2);
        var keyframes = new List<RedactionKeyframe>();
        if (frameWidth < 2 || frameHeight < 2 || intervalSeconds <= 0 || endSeconds <= startSeconds)
        {
            return keyframes;
        }

        double cx = Math.Clamp(seedNormX, 0, 1);
        double cy = Math.Clamp(seedNormY, 0, 1);
        // The user's drawn box is a size hint: reject masks far larger than it (a single click on a
        // textured field can otherwise grab the whole background). Generous multiplier so it can grow.
        double seedArea = Math.Max(1e-4, Math.Clamp(seedNormWidth, 0, 1) * Math.Clamp(seedNormHeight, 0, 1));
        double maxArea = Math.Min(0.6, seedArea * 6.0);
        int total = Math.Max(1, (int)Math.Ceiling((endSeconds - startSeconds) / intervalSeconds) + 1);
        int done = 0;

        for (double t = startSeconds; t <= endSeconds + 1e-6; t += intervalSeconds)
        {
            ct.ThrowIfCancellationRequested();

            using SKBitmap? frame = LibavFrameExtractor.GetFrameAt(videoPath, t, frameWidth, frameHeight);
            if (frame == null)
            {
                break; // past the end / decode failed
            }

            await sam2.SetImageAsync(frame).ConfigureAwait(false);

            var points = new List<(double X, double Y, bool IsPositive)>
            {
                (cx * frameWidth, cy * frameHeight, true),
            };
            using SKBitmap? mask = await sam2.GetMaskBitmapAsync(points).ConfigureAwait(false);

            SKRectI? bounds = mask != null ? TightBounds(mask) : null;
            if (bounds is { } b && b.Width > 1 && b.Height > 1)
            {
                double nx = (double)b.Left / frameWidth;
                double ny = (double)b.Top / frameHeight;
                double nw = (double)b.Width / frameWidth;
                double nh = (double)b.Height / frameHeight;

                // Plausibility gate: drop runaway masks (whole-frame grabs) instead of letting them
                // poison the greedy seed; interpolation bridges the skipped sample.
                bool plausible = (nw * nh) <= maxArea && nw <= 0.9 && nh <= 0.9;
                if (plausible)
                {
                    keyframes.Add(new RedactionKeyframe { TimeSeconds = t, X = nx, Y = ny, Width = nw, Height = nh });
                    cx = Math.Clamp(nx + (nw / 2), 0, 1); // greedy: next seed = this box's centre
                    cy = Math.Clamp(ny + (nh / 2), 0, 1);
                }
            }

            done++;
            progress?.Report(Math.Min(1.0, (double)done / total));
        }

        return keyframes;
    }

    /// <summary>
    /// Tight bounding box of the selected (white, &gt;127) region of a SAM2 mask bitmap, or null if empty.
    /// Works for Gray8 or BGRA masks (reads the first byte of each pixel — white is 255 in every channel).
    /// </summary>
    internal static unsafe SKRectI? TightBounds(SKBitmap mask)
    {
        int w = mask.Width;
        int h = mask.Height;
        int bpp = mask.BytesPerPixel;
        int stride = mask.RowBytes;
        byte* basePtr = (byte*)mask.GetPixels();
        if (basePtr == null || w <= 0 || h <= 0 || bpp <= 0)
        {
            return null;
        }

        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
        {
            byte* row = basePtr + (y * stride);
            for (int x = 0; x < w; x++)
            {
                if (row[x * bpp] > 127)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }

        if (maxX < minX || maxY < minY)
        {
            return null;
        }

        return new SKRectI(minX, minY, maxX + 1, maxY + 1);
    }
}
