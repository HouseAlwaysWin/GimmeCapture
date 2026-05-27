using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Avalonia;
using GimmeCapture.Models;

namespace GimmeCapture.Services.Core.Rendering;

public static class VideoAnnotationFilterBuilder
{
    public static string BuildFilter(
        IEnumerable<Annotation> annotations,
        double referenceWidth,
        double referenceHeight,
        int targetWidth,
        int targetHeight,
        bool includeOverlayInput,
        bool isOutputGif)
    {
        var redactions = annotations
            .Where(a => a.Type is AnnotationType.Mosaic or AnnotationType.Blur)
            .ToArray();

        var filterParts = new List<string>();
        string currentLabel = "0:v";
        float scaleX = referenceWidth > 0 ? targetWidth / (float)referenceWidth : 1f;
        float scaleY = referenceHeight > 0 ? targetHeight / (float)referenceHeight : 1f;
        float uniformScale = Math.Min(scaleX, scaleY);

        for (int i = 0; i < redactions.Length; i++)
        {
            var annotation = redactions[i];
            var rect = ScaleRect(GetLogicalRect(annotation), scaleX, scaleY, targetWidth, targetHeight);
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                continue;
            }

            string baseLabel = $"vbase{i}";
            string sourceLabel = $"vsrc{i}";
            string effectLabel = $"vfx{i}";
            string nextLabel = $"v{i + 1}";

            filterParts.Add($"[{currentLabel}]split[{baseLabel}][{sourceLabel}]");

            if (annotation.Type == AnnotationType.Blur)
            {
                int radius = Math.Max(2, (int)Math.Round(annotation.EffectSettings.BlurRadius * uniformScale));
                int padding = Math.Max(2, (int)Math.Ceiling(radius * 3.0));
                int expandedX = Math.Max(0, rect.X - padding);
                int expandedY = Math.Max(0, rect.Y - padding);
                int expandedRight = Math.Min(targetWidth, rect.X + rect.Width + padding);
                int expandedBottom = Math.Min(targetHeight, rect.Y + rect.Height + padding);
                int expandedWidth = Math.Max(1, expandedRight - expandedX);
                int expandedHeight = Math.Max(1, expandedBottom - expandedY);
                int innerX = rect.X - expandedX;
                int innerY = rect.Y - expandedY;
                filterParts.Add(
                    $"[{sourceLabel}]crop=w={expandedWidth}:h={expandedHeight}:x={expandedX}:y={expandedY}," +
                    $"boxblur=luma_radius={radius}:luma_power=2:chroma_radius={Math.Max(1, radius / 2)}:chroma_power=2," +
                    $"crop=w={rect.Width}:h={rect.Height}:x={innerX}:y={innerY}[{effectLabel}]");
            }
            else
            {
                int cellSize = Math.Max(2, (int)Math.Round(annotation.EffectSettings.MosaicCellSize * uniformScale));
                int downW = Math.Max(1, rect.Width / cellSize);
                int downH = Math.Max(1, rect.Height / cellSize);
                filterParts.Add(
                    $"[{sourceLabel}]crop=w={rect.Width}:h={rect.Height}:x={rect.X}:y={rect.Y}," +
                    $"scale={downW}:{downH}:flags=neighbor,scale={rect.Width}:{rect.Height}:flags=neighbor[{effectLabel}]");
            }

            filterParts.Add($"[{baseLabel}][{effectLabel}]overlay={rect.X}:{rect.Y}[{nextLabel}]");
            currentLabel = nextLabel;
        }

        if (includeOverlayInput)
        {
            filterParts.Add($"[1:v][{currentLabel}]scale2ref[ovrl][refv]");
            filterParts.Add("[refv][ovrl]overlay=0:0:shortest=1[vcomposite]");
            currentLabel = "vcomposite";
        }

        if (isOutputGif)
        {
            filterParts.Add($"[{currentLabel}]split[s0][s1]");
            filterParts.Add("[s0]palettegen[p]");
            filterParts.Add("[s1][p]paletteuse[outv]");
        }
        else
        {
            filterParts.Add($"[{currentLabel}]null[outv]");
        }

        return string.Join(";", filterParts);
    }

    private static Rect GetLogicalRect(Annotation annotation)
    {
        return new Rect(
            Math.Min(annotation.StartPoint.X, annotation.EndPoint.X),
            Math.Min(annotation.StartPoint.Y, annotation.EndPoint.Y),
            Math.Abs(annotation.EndPoint.X - annotation.StartPoint.X),
            Math.Abs(annotation.EndPoint.Y - annotation.StartPoint.Y));
    }

    private static PixelRect ScaleRect(Rect logicalRect, float scaleX, float scaleY, int targetWidth, int targetHeight)
    {
        int left = Math.Clamp((int)Math.Floor(logicalRect.Left * scaleX), 0, Math.Max(0, targetWidth - 1));
        int top = Math.Clamp((int)Math.Floor(logicalRect.Top * scaleY), 0, Math.Max(0, targetHeight - 1));
        int right = Math.Clamp((int)Math.Ceiling(logicalRect.Right * scaleX), left + 1, targetWidth);
        int bottom = Math.Clamp((int)Math.Ceiling(logicalRect.Bottom * scaleY), top + 1, targetHeight);
        return new PixelRect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }
}
