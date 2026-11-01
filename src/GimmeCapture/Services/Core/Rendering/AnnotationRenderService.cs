using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using GimmeCapture.Models;
using SkiaSharp;

namespace GimmeCapture.Services.Core.Rendering;

public sealed class AnnotationRenderService
{
    public static AnnotationRenderService Shared { get; } = new();

    private AnnotationRenderService() { }

    /// <summary>
    /// The live mosaic/blur preview of <paramref name="annotation"/> over <paramref name="snapshot"/>. Only the region
    /// the effect reads, padded as the blur needs, is copied out of the picture: each redaction on screen used to keep a
    /// Skia copy of the whole picture, 32 MB at 4K, for as long as it was shown. Throws ObjectDisposedException for a
    /// picture already disposed, which a caller with a preview drawn before can keep.
    /// </summary>
    public WriteableBitmap? RenderAnnotationPreview(Bitmap? snapshot, Annotation annotation, double referenceWidth, double referenceHeight)
    {
        if (snapshot == null
            || MeasurePreview(snapshot.PixelSize.Width, snapshot.PixelSize.Height, annotation, referenceWidth, referenceHeight)
                is not { } region)
        {
            return null;
        }

        using var working = new SKBitmap(region.Padded.Width, region.Padded.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        if (!TryCopyRegion(snapshot, region.Padded, working))
            return null;

        using var preview = FinishPreview(working, region, annotation);
        return ConvertSkBitmapToWriteableBitmap(preview);
    }

    public SKBitmap? RenderAnnotationPreviewToSkBitmap(SKBitmap snapshot, Annotation annotation, double referenceWidth, double referenceHeight)
    {
        if (MeasurePreview(snapshot.Width, snapshot.Height, annotation, referenceWidth, referenceHeight) is not { } region)
            return null;

        using var working = new SKBitmap(region.Padded.Width, region.Padded.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        CopyRect(snapshot, region.Padded.Left, region.Padded.Top, working, 0, 0, region.Padded.Width, region.Padded.Height);
        return FinishPreview(working, region, annotation);
    }

    // A preview's region of the picture, in pixels: the annotation's, and that padded as far as the effect reads.
    private readonly record struct PreviewRegion(SKRectI Roi, SKRectI Padded, float UniformScale);

    private static PreviewRegion? MeasurePreview(
        int snapshotWidth, int snapshotHeight, Annotation annotation, double referenceWidth, double referenceHeight)
    {
        if (annotation.Type != AnnotationType.Mosaic && annotation.Type != AnnotationType.Blur)
            return null;

        var logicalRect = GetLogicalRect(annotation);
        if (logicalRect.Width <= 0 || logicalRect.Height <= 0)
            return null;

        var roi = CreatePixelRect(logicalRect, referenceWidth, referenceHeight, snapshotWidth, snapshotHeight);
        if (roi.Width <= 0 || roi.Height <= 0)
            return null;

        double scaleX = snapshotWidth / referenceWidth;
        double scaleY = snapshotHeight / referenceHeight;
        float uniformScale = (float)Math.Min(scaleX, scaleY);

        // Operate on just the padded ROI instead of a full-snapshot copy (which is ~8 MB at 1080p and
        // was allocated on every live-drag frame). The margin matches the blur's own neighbor-sampling
        // padding, so the effect sees the same surrounding pixels and the output is identical to
        // processing the whole snapshot; mosaic needs no margin (cells are self-contained).
        int pad = 0;
        if (annotation.Type == AnnotationType.Blur)
        {
            float blurRadius = Math.Max(1f, annotation.EffectSettings.BlurRadius * uniformScale);
            pad = Math.Max(2, (int)Math.Ceiling(blurRadius * 3f));
        }

        var padded = new SKRectI(
            Math.Max(0, roi.Left - pad),
            Math.Max(0, roi.Top - pad),
            Math.Min(snapshotWidth, roi.Right + pad),
            Math.Min(snapshotHeight, roi.Bottom + pad));
        if (padded.Width <= 0 || padded.Height <= 0)
            return null;

        return new PreviewRegion(roi, padded, uniformScale);
    }

    // Applies the effect to the padded region copied into working, and cuts the annotation's own region out of it.
    private static SKBitmap FinishPreview(SKBitmap working, PreviewRegion region, Annotation annotation)
    {
        var roi = region.Roi;
        // ROI expressed in the cropped working buffer's coordinate space.
        var workingRoi = new SKRect(
            roi.Left - region.Padded.Left, roi.Top - region.Padded.Top, roi.Right - region.Padded.Left, roi.Bottom - region.Padded.Top);
        ApplyRedactionEffect(working, annotation, workingRoi, region.UniformScale);

        var preview = new SKBitmap(roi.Width, roi.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(preview))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(working, workingRoi, new SKRect(0, 0, roi.Width, roi.Height));
            canvas.Flush();
        }

        return preview;
    }

    public void RenderAnnotationsToBitmap(SKBitmap bitmap, IEnumerable<Annotation> annotations, double referenceWidth, double referenceHeight, float targetWidth, float targetHeight)
    {
        if (referenceWidth <= 0 || referenceHeight <= 0)
            return;

        using var canvas = new SKCanvas(bitmap);
        foreach (var annotation in annotations)
        {
            RenderAnnotation(canvas, bitmap, annotation, referenceWidth, referenceHeight, targetWidth, targetHeight);
        }
        canvas.Flush();
    }

    private void RenderAnnotation(SKCanvas canvas, SKBitmap bitmap, Annotation ann, double refW, double refH, float targetW, float targetH)
    {
        float scaleX = targetW / (float)refW;
        float scaleY = targetH / (float)refH;
        float uniformScale = Math.Min(scaleX, scaleY);

        using var paint = new SKPaint
        {
            Color = new SKColor(ann.Color.R, ann.Color.G, ann.Color.B, ann.Color.A),
            StrokeWidth = (float)(ann.Thickness * uniformScale),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round
        };

        switch (ann.Type)
        {
            case AnnotationType.Rectangle:
            case AnnotationType.Ellipse:
            {
                var rect = ScaleRect(GetLogicalRect(ann), scaleX, scaleY);
                if (ann.IsFilled)
                {
                    using var fill = new SKPaint { Color = paint.Color, Style = SKPaintStyle.Fill, IsAntialias = true };
                    if (ann.Type == AnnotationType.Rectangle) canvas.DrawRect(rect, fill);
                    else canvas.DrawOval(rect, fill);
                }
                if (ann.Type == AnnotationType.Rectangle) canvas.DrawRect(rect, paint);
                else canvas.DrawOval(rect, paint);
                break;
            }
            case AnnotationType.Highlighter:
            {
                var rect = ScaleRect(GetLogicalRect(ann), scaleX, scaleY);
                using var hl = new SKPaint
                {
                    Color = paint.Color.WithAlpha((byte)(paint.Color.Alpha * 0.35f)),
                    Style = SKPaintStyle.Fill,
                    IsAntialias = true
                };
                canvas.DrawRect(rect, hl);
                break;
            }
            case AnnotationType.Step:
            {
                var rect = ScaleRect(GetLogicalRect(ann), scaleX, scaleY);
                DrawStep(canvas, ann, rect, paint, uniformScale);
                break;
            }
            case AnnotationType.Line:
                canvas.DrawLine(ScalePoint(ann.StartPoint, scaleX, scaleY), ScalePoint(ann.EndPoint, scaleX, scaleY), paint);
                break;
            case AnnotationType.Arrow:
                DrawArrow(canvas, ScalePoint(ann.StartPoint, scaleX, scaleY), ScalePoint(ann.EndPoint, scaleX, scaleY), paint, uniformScale);
                break;
            case AnnotationType.Text:
                DrawText(canvas, ann, ann.StartPoint, paint, scaleX, scaleY, uniformScale);
                break;
            case AnnotationType.Callout:
                DrawCallout(canvas, ann, paint, scaleX, scaleY, uniformScale);
                break;
            case AnnotationType.Pen:
                DrawPen(canvas, ann, paint, scaleX, scaleY);
                break;
            case AnnotationType.Mosaic:
            case AnnotationType.Blur:
            {
                var rect = ScaleRect(GetLogicalRect(ann), scaleX, scaleY);
                ApplyRedactionEffect(bitmap, ann, rect, uniformScale);
                break;
            }
        }
    }

    private static void DrawPen(SKCanvas canvas, Annotation ann, SKPaint paint, float scaleX, float scaleY)
    {
        if (!ann.Points.AsValueEnumerable().Any())
            return;

        using var path = new SKPath();
        var first = ann.Points[0];
        path.MoveTo((float)(first.X * scaleX), (float)(first.Y * scaleY));
        foreach (var point in ann.Points.AsValueEnumerable().Skip(1))
        {
            path.LineTo((float)(point.X * scaleX), (float)(point.Y * scaleY));
        }
        canvas.DrawPath(path, paint);
    }

    private static void DrawText(SKCanvas canvas, Annotation ann, Point textOrigin, SKPaint paint, float scaleX, float scaleY, float uniformScale)
    {
        paint.Style = SKPaintStyle.Fill;

        var weight = ann.IsBold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal;
        var slant = ann.IsItalic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright;
        var typeface = TypefaceCache.Get(ann.FontFamily.Name, weight, slant); // cached + shared; do not dispose
        using var font = new SKFont(typeface, (float)(ann.FontSize * uniformScale));

        var origin = new SKPoint((float)(textOrigin.X * scaleX), (float)(textOrigin.Y * scaleY));
        canvas.DrawText(ann.Text ?? string.Empty, origin, SKTextAlign.Left, font, paint);
    }

    // A Callout = a text label anchored at EndPoint plus a leader line (with a small
    // arrow head) pointing from the label toward StartPoint (the annotated target).
    // It reuses the existing arrow + text primitives rather than introducing new geometry.
    private static void DrawCallout(SKCanvas canvas, Annotation ann, SKPaint paint, float scaleX, float scaleY, float uniformScale)
    {
        var target = ScalePoint(ann.StartPoint, scaleX, scaleY);
        var label = ScalePoint(ann.EndPoint, scaleX, scaleY);

        // Leader: stroke + arrow head land on the target (StartPoint).
        using var leaderPaint = new SKPaint
        {
            Color = paint.Color,
            StrokeWidth = paint.StrokeWidth,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round
        };
        DrawArrow(canvas, label, target, leaderPaint, uniformScale);

        // Label: render the text at the anchor (EndPoint), reusing the text primitive.
        DrawText(canvas, ann, ann.EndPoint, paint, scaleX, scaleY, uniformScale);
    }

    private static void DrawArrow(SKCanvas canvas, SKPoint start, SKPoint end, SKPaint paint, float uniformScale)
    {
        canvas.DrawLine(start, end, paint);

        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var len = MathF.Sqrt((dx * dx) + (dy * dy));
        if (len <= 0.001f)
            return;

        var ux = dx / len;
        var uy = dy / len;
        var px = -uy;
        var py = ux;

        var headLength = Math.Clamp((8f * uniformScale) + (paint.StrokeWidth * 1.4f), 8f * uniformScale, 18f * uniformScale);
        var halfWidth = headLength * 0.36f;
        var notchDepth = headLength * 0.38f;

        var left = new SKPoint(end.X - (ux * headLength) + (px * halfWidth), end.Y - (uy * headLength) + (py * halfWidth));
        var right = new SKPoint(end.X - (ux * headLength) - (px * halfWidth), end.Y - (uy * headLength) - (py * halfWidth));
        var notch = new SKPoint(end.X - (ux * notchDepth), end.Y - (uy * notchDepth));

        using var path = new SKPath();
        path.MoveTo(end);
        path.LineTo(left);
        path.LineTo(notch);
        path.LineTo(right);
        path.Close();

        using var fillPaint = new SKPaint
        {
            Color = paint.Color,
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };
        canvas.DrawPath(path, fillPaint);
    }

    private static void DrawStep(SKCanvas canvas, Annotation ann, SKRect rect, SKPaint paint, float uniformScale)
    {
        float cx = rect.MidX;
        float cy = rect.MidY;
        // Radius from the drawn area; fall back to a sensible default for a click without drag.
        float radius = Math.Max(rect.Width, rect.Height) / 2f;
        if (radius < 8f * uniformScale)
        {
            radius = 14f * uniformScale;
        }

        using var fill = new SKPaint { Color = paint.Color, Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawCircle(cx, cy, radius, fill);

        string label = ann.StepNumber.ToString();
        using var textPaint = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Fill };
        using var font = new SKFont(SKTypeface.Default, radius * 1.1f);
        float textWidth = font.MeasureText(label);
        var metrics = font.Metrics;
        float baseline = cy - (metrics.Ascent + metrics.Descent) / 2f;
        canvas.DrawText(label, cx - textWidth / 2f, baseline, font, textPaint);
    }

    private static void ApplyRedactionEffect(SKBitmap target, Annotation annotation, SKRect rect, float uniformScale)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            return;

        switch (annotation.Type)
        {
            case AnnotationType.Mosaic:
                ApplyMosaic(target, rect, annotation.EffectSettings, uniformScale);
                break;
            case AnnotationType.Blur:
                ApplyBlur(target, rect, annotation.EffectSettings, uniformScale);
                break;
        }
    }

    private static void ApplyMosaic(SKBitmap target, SKRect rect, AnnotationEffectSettings settings, float uniformScale)
    {
        int left = Math.Clamp((int)Math.Floor(rect.Left), 0, Math.Max(0, target.Width - 1));
        int top = Math.Clamp((int)Math.Floor(rect.Top), 0, Math.Max(0, target.Height - 1));
        int right = Math.Clamp((int)Math.Ceiling(rect.Right), left + 1, target.Width);
        int bottom = Math.Clamp((int)Math.Ceiling(rect.Bottom), top + 1, target.Height);
        int cellSize = Math.Max(2, (int)Math.Round(settings.MosaicCellSize * uniformScale));

        // Each cell reads and writes its own disjoint region, and AverageCellColor reads
        // the whole cell before Fill writes it back, so we can sample directly from the
        // target without a full-bitmap working copy.
        for (int y = top; y < bottom; y += cellSize)
        {
            for (int x = left; x < right; x += cellSize)
            {
                int cellRight = Math.Min(right, x + cellSize);
                int cellBottom = Math.Min(bottom, y + cellSize);
                uint sample = AverageCellColor(target, x, y, cellRight, cellBottom);

                for (int yy = y; yy < cellBottom; yy++)
                {
                    GetPixelRowUIntSpan(target, yy).Slice(x, cellRight - x).Fill(sample);
                }
            }
        }
    }

    private static void ApplyBlur(SKBitmap target, SKRect rect, AnnotationEffectSettings settings, float uniformScale)
    {
        int left = Math.Clamp((int)Math.Floor(rect.Left), 0, Math.Max(0, target.Width - 1));
        int top = Math.Clamp((int)Math.Floor(rect.Top), 0, Math.Max(0, target.Height - 1));
        int right = Math.Clamp((int)Math.Ceiling(rect.Right), left + 1, target.Width);
        int bottom = Math.Clamp((int)Math.Ceiling(rect.Bottom), top + 1, target.Height);
        int width = right - left;
        int height = bottom - top;
        if (width <= 0 || height <= 0)
            return;

        float blurRadius = Math.Max(1f, settings.BlurRadius * uniformScale);
        int padding = Math.Max(2, (int)Math.Ceiling(blurRadius * 3f));
        int expandedLeft = Math.Max(0, left - padding);
        int expandedTop = Math.Max(0, top - padding);
        int expandedRight = Math.Min(target.Width, right + padding);
        int expandedBottom = Math.Min(target.Height, bottom + padding);
        int expandedWidth = expandedRight - expandedLeft;
        int expandedHeight = expandedBottom - expandedTop;
        int innerLeft = left - expandedLeft;
        int innerTop = top - expandedTop;

        // Blur the padded ROI directly out of the target into a single destination buffer. Drawing
        // the sub-rect of `target` straight into `blurred` (instead of first copying it into a
        // separate `region` bitmap) gives identical clamp-blur output with one fewer allocation —
        // this path runs on every pointer move during a live drag, so the saved copy matters.
        using var blurred = new SKBitmap(expandedWidth, expandedHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(blurred))
        using (var blurPaint = new SKPaint { ImageFilter = CreateClampBlur(blurRadius, blurRadius) })
        {
            var src = new SKRect(expandedLeft, expandedTop, expandedRight, expandedBottom);
            var dst = new SKRect(0, 0, expandedWidth, expandedHeight);
            canvas.DrawBitmap(target, src, dst, blurPaint);
        }

        CopyRect(blurred, innerLeft, innerTop, target, left, top, width, height);
    }

    private static SKImageFilter CreateClampBlur(float sigmaX, float sigmaY)
        => SKImageFilter.CreateBlur(sigmaX, sigmaY, SKShaderTileMode.Clamp);


    private static uint AverageCellColor(SKBitmap source, int left, int top, int right, int bottom)
    {
        ulong sumR = 0;
        ulong sumG = 0;
        ulong sumB = 0;
        ulong sumA = 0;
        int count = 0;

        for (int y = top; y < bottom; y++)
        {
            var row = GetPixelRowUIntSpan(source, y).Slice(left, right - left);
            foreach (uint pixel in row)
            {
                sumB += (byte)pixel;
                sumG += (byte)(pixel >> 8);
                sumR += (byte)(pixel >> 16);
                sumA += (byte)(pixel >> 24);
                count++;
            }
        }

        if (count == 0)
        {
            return 0;
        }

        return PackBgra(
            (byte)(sumB / (ulong)count),
            (byte)(sumG / (ulong)count),
            (byte)(sumR / (ulong)count),
            (byte)(sumA / (ulong)count));
    }

    private static unsafe Span<uint> GetPixelRowUIntSpan(SKBitmap bitmap, int row)
    {
        byte* rowPtr = (byte*)bitmap.GetPixels() + (row * bitmap.RowBytes);
        return MemoryMarshal.Cast<byte, uint>(new Span<byte>(rowPtr, bitmap.Width * 4));
    }

    private static void CopyRect(SKBitmap source, int sourceX, int sourceY, SKBitmap destination, int destinationX, int destinationY, int width, int height)
    {
        for (int row = 0; row < height; row++)
        {
            GetPixelRowUIntSpan(source, sourceY + row)
                .Slice(sourceX, width)
                .CopyTo(GetPixelRowUIntSpan(destination, destinationY + row).Slice(destinationX, width));
        }
    }

    private static uint PackBgra(byte blue, byte green, byte red, byte alpha)
        => blue | ((uint)green << 8) | ((uint)red << 16) | ((uint)alpha << 24);

    private static SKPoint ScalePoint(Point point, float scaleX, float scaleY)
        => new((float)(point.X * scaleX), (float)(point.Y * scaleY));

    private static SKRect ScaleRect(Rect rect, float scaleX, float scaleY)
        => new((float)(rect.Left * scaleX), (float)(rect.Top * scaleY), (float)(rect.Right * scaleX), (float)(rect.Bottom * scaleY));

    private static Rect GetLogicalRect(Annotation annotation)
    {
        return new Rect(
            Math.Min(annotation.StartPoint.X, annotation.EndPoint.X),
            Math.Min(annotation.StartPoint.Y, annotation.EndPoint.Y),
            Math.Abs(annotation.EndPoint.X - annotation.StartPoint.X),
            Math.Abs(annotation.EndPoint.Y - annotation.StartPoint.Y));
    }

    private static SKRectI CreatePixelRect(Rect logicalRect, double logicalWidth, double logicalHeight, int pixelWidth, int pixelHeight)
    {
        if (logicalWidth <= 0 || logicalHeight <= 0)
            return new SKRectI();

        double scaleX = pixelWidth / logicalWidth;
        double scaleY = pixelHeight / logicalHeight;

        int left = Math.Clamp((int)Math.Floor(logicalRect.Left * scaleX), 0, Math.Max(0, pixelWidth - 1));
        int top = Math.Clamp((int)Math.Floor(logicalRect.Top * scaleY), 0, Math.Max(0, pixelHeight - 1));
        int right = Math.Clamp((int)Math.Ceiling(logicalRect.Right * scaleX), left + 1, pixelWidth);
        int bottom = Math.Clamp((int)Math.Ceiling(logicalRect.Bottom * scaleY), top + 1, pixelHeight);
        return new SKRectI(left, top, right, bottom);
    }

    // Copies one region of an Avalonia bitmap into a Skia bitmap of the region's size. False where the bitmap cannot be
    // read (a transcoded one, say), which leaves the preview out; a disposed one throws (see RenderAnnotationPreview).
    private static bool TryCopyRegion(Bitmap bitmap, SKRectI region, SKBitmap destination)
    {
        var sourceRect = new PixelRect(region.Left, region.Top, region.Width, region.Height);
        try
        {
            if (bitmap.Format == PixelFormat.Bgra8888 && bitmap.AlphaFormat == AlphaFormat.Premul)
            {
                // Laid out as Skia's are, so Avalonia copies straight into Skia's pixels, which the GC never moves.
                // The copy used to go through a pooled array that was read after it was unpinned, and the idle memory
                // trim compacts the large-object heap that array lives on: wrong pixels, or an access violation.
                bitmap.CopyPixels(sourceRect, destination.GetPixels(), destination.ByteCount, destination.RowBytes);
            }
            else
            {
                CopyThroughPinnedBuffer(bitmap, sourceRect, destination);
            }

            return true;
        }
        catch (Exception ex) when (ex is not ObjectDisposedException)
        {
            return false;
        }
    }

    // Any other layout is copied byte for byte through a pooled buffer, as all of them were before, pinned until the
    // last row is out of it.
    private static unsafe void CopyThroughPinnedBuffer(Bitmap bitmap, PixelRect sourceRect, SKBitmap destination)
    {
        int rowBytes = sourceRect.Width * 4;
        int byteCount = rowBytes * sourceRect.Height;
        byte[] rented = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            fixed (byte* buffer = rented)
            {
                bitmap.CopyPixels(sourceRect, (IntPtr)buffer, byteCount, rowBytes);

                byte* destinationPixels = (byte*)destination.GetPixels();
                int destinationRowBytes = destination.RowBytes;
                for (int row = 0; row < sourceRect.Height; row++)
                {
                    Buffer.MemoryCopy(
                        buffer + (row * rowBytes),
                        destinationPixels + (row * destinationRowBytes),
                        destinationRowBytes,
                        rowBytes);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static WriteableBitmap ConvertSkBitmapToWriteableBitmap(SKBitmap bitmap)
    {
        var result = new WriteableBitmap(
            new PixelSize(bitmap.Width, bitmap.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        using var locked = result.Lock();
        unsafe
        {
            byte* srcBase = (byte*)bitmap.GetPixels();
            byte* dstBase = (byte*)locked.Address;
            int rows = Math.Min(bitmap.Height, locked.Size.Height);
            int bytesPerRow = Math.Min(bitmap.RowBytes, locked.RowBytes);
            for (int row = 0; row < rows; row++)
            {
                Buffer.MemoryCopy(srcBase + (row * bitmap.RowBytes), dstBase + (row * locked.RowBytes), locked.RowBytes, bytesPerRow);
            }
        }

        return result;
    }
}
