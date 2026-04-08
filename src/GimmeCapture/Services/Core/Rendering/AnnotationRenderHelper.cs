using System;
using System.Collections.Generic;
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

        float scaleX = targetW / (float)refW;
        float scaleY = targetH / (float)refH;

        var annotationsArray = annotations.AsValueEnumerable().ToArray();
        System.Diagnostics.Debug.WriteLine($"[DrawAnnotations] Drawing {annotationsArray.Length} annotations. Target size: {targetW}x{targetH}, Ref size: {refW}x{refH}");

        try
        {
            foreach (var ann in annotationsArray)
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
                        var rect = new SKRect(
                            (float)(Math.Min(ann.StartPoint.X, ann.EndPoint.X) * scaleX),
                            (float)(Math.Min(ann.StartPoint.Y, ann.EndPoint.Y) * scaleY),
                            (float)(Math.Max(ann.StartPoint.X, ann.EndPoint.X) * scaleX),
                            (float)(Math.Max(ann.StartPoint.Y, ann.EndPoint.Y) * scaleY));
                        if (ann.Type == AnnotationType.Rectangle) canvas.DrawRect(rect, paint);
                        else canvas.DrawOval(rect, paint);
                        System.Diagnostics.Debug.WriteLine($"[DrawAnnotations] Drew {ann.Type} at {rect}");
                        break;
                    case AnnotationType.Line:
                        canvas.DrawLine((float)(ann.StartPoint.X * scaleX), (float)(ann.StartPoint.Y * scaleY), (float)(ann.EndPoint.X * scaleX), (float)(ann.EndPoint.Y * scaleY), paint);
                        System.Diagnostics.Debug.WriteLine($"[DrawAnnotations] Drew {ann.Type} from {ann.StartPoint} to {ann.EndPoint}");
                        break;
                    case AnnotationType.Arrow:
                        float x1 = (float)(ann.StartPoint.X * scaleX), y1 = (float)(ann.StartPoint.Y * scaleY);
                        float x2 = (float)(ann.EndPoint.X * scaleX), y2 = (float)(ann.EndPoint.Y * scaleY);
                        canvas.DrawLine(x1, y1, x2, y2, paint);
                        var dx = x2 - x1;
                        var dy = y2 - y1;
                        var len = Math.Sqrt((dx * dx) + (dy * dy));
                        if (len > 0.001)
                        {
                            var ux = dx / len;
                            var uy = dy / len;
                            var px = -uy;
                            var py = ux;

                            var headLength = Math.Clamp((8.0 * scaleX) + (ann.Thickness * scaleX * 1.4), 8.0 * scaleX, 18.0 * scaleX);
                            var halfWidth = headLength * 0.36;
                            var notchDepth = headLength * 0.38;

                            var leftX = x2 - (ux * headLength) + (px * halfWidth);
                            var leftY = y2 - (uy * headLength) + (py * halfWidth);
                            var rightX = x2 - (ux * headLength) - (px * halfWidth);
                            var rightY = y2 - (uy * headLength) - (py * halfWidth);
                            var notchX = x2 - (ux * notchDepth);
                            var notchY = y2 - (uy * notchDepth);

                            var path = new SKPath();
                            path.MoveTo(x2, y2);
                            path.LineTo((float)leftX, (float)leftY);
                            path.LineTo((float)notchX, (float)notchY);
                            path.LineTo((float)rightX, (float)rightY);
                            path.Close();
                            paint.Style = SKPaintStyle.Fill;
                            canvas.DrawPath(path, paint);
                        }
                        System.Diagnostics.Debug.WriteLine($"[DrawAnnotations] Drew {ann.Type} from {ann.StartPoint} to {ann.EndPoint}");
                        break;
                    case AnnotationType.Pen:
                        if (ann.Points.AsValueEnumerable().Any())
                        {
                            var pts = ann.Points.AsValueEnumerable().Select(p => new SKPoint((float)(p.X * scaleX), (float)(p.Y * scaleY))).ToArray();
                            canvas.DrawPoints(SKPointMode.Polygon, pts, paint);
                            System.Diagnostics.Debug.WriteLine($"[DrawAnnotations] Drew {ann.Type} with {pts.Length} points.");
                        }
                        break;
                    case AnnotationType.Text:
                        {
                            using var font = new SKFont(SKTypeface.Default, (float)(ann.FontSize * scaleX));
                            using var textPaint = new SKPaint { Color = paint.Color, IsAntialias = true };
                            canvas.DrawText(ann.Text, (float)(ann.StartPoint.X * scaleX), (float)(ann.StartPoint.Y * scaleY + ann.FontSize * scaleY), SKTextAlign.Left, font, textPaint);
                            System.Diagnostics.Debug.WriteLine($"[DrawAnnotations] Drew {ann.Type}: '{ann.Text}' at {ann.StartPoint}");
                        }
                        break;
                }
            }
            canvas.Flush();
            System.Diagnostics.Debug.WriteLine($"[DrawAnnotations] Finished rendering {annotationsArray.Length} annotations to canvas.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DrawAnnotations] Error: {ex}");
        }
    }
}
