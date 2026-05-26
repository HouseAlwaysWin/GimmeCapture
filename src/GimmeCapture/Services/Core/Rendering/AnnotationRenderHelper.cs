using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media.Imaging;
using GimmeCapture.Models;
using SkiaSharp;

namespace GimmeCapture.Services.Core.Rendering;

public static class AnnotationRenderHelper
{
    /// <summary>
    /// Draws a collection of annotations onto an SKCanvas.
    /// This logic scales the annotations from their reference size to the target canvas size.
    /// </summary>
    /// <param name="canvas">The target SKCanvas to draw on.</param>
    /// <param name="annotations">The annotations to draw.</param>
    /// <param name="refW">The reference width (usually DisplayWidth).</param>
    /// <param name="refH">The reference height (usually DisplayHeight).</param>
    /// <param name="targetW">The target canvas width.</param>
    /// <param name="targetH">The target canvas height.</param>
    public static void DrawAnnotationsOnCanvas(SKCanvas canvas, IEnumerable<Annotation> annotations, double refW, double refH, float targetW, float targetH)
    {
        if (refW <= 0 || refH <= 0) return;

        try
        {
            float scaleX = targetW / (float)refW;
            float scaleY = targetH / (float)refH;

            foreach (var ann in annotations)
            {
                using var paint = new SKPaint
                {
                    Color = new SKColor(ann.Color.R, ann.Color.G, ann.Color.B, ann.Color.A),
                    StrokeWidth = (float)(ann.Thickness * scaleX),
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
                        var rect = new SKRect(
                            (float)(Math.Min(ann.StartPoint.X, ann.EndPoint.X) * scaleX),
                            (float)(Math.Min(ann.StartPoint.Y, ann.EndPoint.Y) * scaleY),
                            (float)(Math.Max(ann.StartPoint.X, ann.EndPoint.X) * scaleX),
                            (float)(Math.Max(ann.StartPoint.Y, ann.EndPoint.Y) * scaleY));
                        if (ann.Type == AnnotationType.Rectangle) canvas.DrawRect(rect, paint);
                        else canvas.DrawOval(rect, paint);
                        break;
                    }
                    case AnnotationType.Line:
                        canvas.DrawLine((float)(ann.StartPoint.X * scaleX), (float)(ann.StartPoint.Y * scaleY), (float)(ann.EndPoint.X * scaleX), (float)(ann.EndPoint.Y * scaleY), paint);
                        break;
                    case AnnotationType.Arrow:
                    {
                        float x1 = (float)(ann.StartPoint.X * scaleX);
                        float y1 = (float)(ann.StartPoint.Y * scaleY);
                        float x2 = (float)(ann.EndPoint.X * scaleX);
                        float y2 = (float)(ann.EndPoint.Y * scaleY);
                        canvas.DrawLine(x1, y1, x2, y2, paint);
                        break;
                    }
                    case AnnotationType.Pen:
                        if (ann.Points.AsValueEnumerable().Any())
                        {
                            var pts = ann.Points.AsValueEnumerable().Select(p => new SKPoint((float)(p.X * scaleX), (float)(p.Y * scaleY))).ToArray();
                            canvas.DrawPoints(SKPointMode.Polygon, pts, paint);
                        }
                        break;
                    case AnnotationType.Text:
                    {
                        using var font = new SKFont(SKTypeface.Default, (float)(ann.FontSize * scaleX));
                        using var textPaint = new SKPaint { Color = paint.Color, IsAntialias = true };
                        canvas.DrawText(ann.Text, (float)(ann.StartPoint.X * scaleX), (float)(ann.StartPoint.Y * scaleY + ann.FontSize * scaleY), SKTextAlign.Left, font, textPaint);
                        break;
                    }
                    case AnnotationType.Mosaic:
                    case AnnotationType.Blur:
                    {
                        if (ann.DrawingModeSnapshot == null) break;
                        using var preview = AnnotationRenderService.Shared.RenderAnnotationPreview(ann.DrawingModeSnapshot, ann, refW, refH);
                        if (preview == null) break;
                        using var previewBitmap = ConvertPreviewToSkBitmap(preview);
                        var rect = new SKRect(
                            (float)(Math.Min(ann.StartPoint.X, ann.EndPoint.X) * scaleX),
                            (float)(Math.Min(ann.StartPoint.Y, ann.EndPoint.Y) * scaleY),
                            (float)(Math.Max(ann.StartPoint.X, ann.EndPoint.X) * scaleX),
                            (float)(Math.Max(ann.StartPoint.Y, ann.EndPoint.Y) * scaleY));
                        canvas.DrawBitmap(previewBitmap, rect);
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DrawAnnotations] Error: {ex}");
        }
    }

    private static SKBitmap ConvertPreviewToSkBitmap(WriteableBitmap preview)
    {
        var skBitmap = new SKBitmap(preview.PixelSize.Width, preview.PixelSize.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var locked = preview.Lock();
        unsafe
        {
            byte* srcBase = (byte*)locked.Address;
            byte* dstBase = (byte*)skBitmap.GetPixels();
            for (int row = 0; row < preview.PixelSize.Height; row++)
            {
                Buffer.MemoryCopy(srcBase + (row * locked.RowBytes), dstBase + (row * skBitmap.RowBytes), skBitmap.RowBytes, Math.Min(locked.RowBytes, skBitmap.RowBytes));
            }
        }

        return skBitmap;
    }
}
