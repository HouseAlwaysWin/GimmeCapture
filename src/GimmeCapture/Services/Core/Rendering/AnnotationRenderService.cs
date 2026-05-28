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

public interface IAnnotationRenderService
{
    WriteableBitmap? RenderAnnotationPreview(Bitmap? snapshot, Annotation annotation, double referenceWidth, double referenceHeight);
    void RenderAnnotationsToBitmap(SKBitmap bitmap, IEnumerable<Annotation> annotations, double referenceWidth, double referenceHeight, float targetWidth, float targetHeight);
}

public sealed class AnnotationRenderService : IAnnotationRenderService
{
    public static AnnotationRenderService Shared { get; } = new();

    private AnnotationRenderService() { }

    public WriteableBitmap? RenderAnnotationPreview(Bitmap? snapshot, Annotation annotation, double referenceWidth, double referenceHeight)
    {
        if (snapshot == null)
            return null;

        using var source = ConvertAvaloniaBitmapToSkBitmap(snapshot);
        if (source == null)
            return null;

        using var preview = RenderAnnotationPreviewToSkBitmap(source, annotation, referenceWidth, referenceHeight);
        if (preview == null)
            return null;

        return ConvertSkBitmapToWriteableBitmap(preview);
    }

    public SKBitmap? RenderAnnotationPreviewToSkBitmap(SKBitmap snapshot, Annotation annotation, double referenceWidth, double referenceHeight)
    {
        if (annotation.Type != AnnotationType.Mosaic && annotation.Type != AnnotationType.Blur)
            return null;

        var logicalRect = GetLogicalRect(annotation);
        if (logicalRect.Width <= 0 || logicalRect.Height <= 0)
            return null;

        var roi = CreatePixelRect(logicalRect, referenceWidth, referenceHeight, snapshot.Width, snapshot.Height);
        if (roi.Width <= 0 || roi.Height <= 0)
            return null;

        double scaleX = snapshot.Width / referenceWidth;
        double scaleY = snapshot.Height / referenceHeight;
        float uniformScale = (float)Math.Min(scaleX, scaleY);

        using var working = snapshot.Copy();
        ApplyRedactionEffect(
            working,
            annotation,
            new SKRect(roi.Left, roi.Top, roi.Right, roi.Bottom),
            uniformScale);

        var preview = new SKBitmap(roi.Width, roi.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(preview))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(working, roi, new SKRect(0, 0, roi.Width, roi.Height));
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
                if (ann.Type == AnnotationType.Rectangle) canvas.DrawRect(rect, paint);
                else canvas.DrawOval(rect, paint);
                break;
            }
            case AnnotationType.Line:
                canvas.DrawLine(ScalePoint(ann.StartPoint, scaleX, scaleY), ScalePoint(ann.EndPoint, scaleX, scaleY), paint);
                break;
            case AnnotationType.Arrow:
                DrawArrow(canvas, ScalePoint(ann.StartPoint, scaleX, scaleY), ScalePoint(ann.EndPoint, scaleX, scaleY), paint, uniformScale);
                break;
            case AnnotationType.Text:
                DrawText(canvas, ann, paint, scaleX, scaleY, uniformScale);
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

    private static void DrawText(SKCanvas canvas, Annotation ann, SKPaint paint, float scaleX, float scaleY, float uniformScale)
    {
        paint.Style = SKPaintStyle.Fill;

        var weight = ann.IsBold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal;
        var slant = ann.IsItalic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright;
        var style = new SKFontStyle(weight, SKFontStyleWidth.Normal, slant);
        using var typeface = SKTypeface.FromFamilyName(ann.FontFamily.Name, style) ?? SKTypeface.Default;
        using var font = new SKFont(typeface, (float)(ann.FontSize * uniformScale));

        var origin = new SKPoint((float)(ann.StartPoint.X * scaleX), (float)(ann.StartPoint.Y * scaleY));
        canvas.DrawText(ann.Text ?? string.Empty, origin, SKTextAlign.Left, font, paint);
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

        using var sourceCopy = target.Copy();
        for (int y = top; y < bottom; y += cellSize)
        {
            for (int x = left; x < right; x += cellSize)
            {
                int cellRight = Math.Min(right, x + cellSize);
                int cellBottom = Math.Min(bottom, y + cellSize);
                uint sample = AverageCellColor(sourceCopy, x, y, cellRight, cellBottom);

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

        using var region = new SKBitmap(expandedWidth, expandedHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        CopyRect(target, expandedLeft, expandedTop, region, 0, 0, expandedWidth, expandedHeight);

        using var blurred = new SKBitmap(expandedWidth, expandedHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(blurred))
        using (var blurPaint = new SKPaint { ImageFilter = CreateClampBlur(blurRadius, blurRadius) })
        {
            canvas.DrawBitmap(region, 0, 0, blurPaint);
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

    private static SKBitmap? ConvertAvaloniaBitmapToSkBitmap(Bitmap bitmap)
    {
        int width = bitmap.PixelSize.Width;
        int height = bitmap.PixelSize.Height;
        if (width <= 0 || height <= 0)
            return null;

        var skBitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        byte[] rented = ArrayPool<byte>.Shared.Rent(width * height * 4);

        try
        {
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(rented, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                bitmap.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), width * height * 4, width * 4);
            }
            finally
            {
                handle.Free();
            }

            unsafe
            {
                byte* srcBase = (byte*)System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(rented, 0);
                byte* dstBase = (byte*)skBitmap.GetPixels();
                for (int row = 0; row < height; row++)
                {
                    Buffer.MemoryCopy(srcBase + (row * width * 4), dstBase + (row * skBitmap.RowBytes), skBitmap.RowBytes, width * 4);
                }
            }

            return skBitmap;
        }
        catch
        {
            skBitmap.Dispose();
            return null;
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
