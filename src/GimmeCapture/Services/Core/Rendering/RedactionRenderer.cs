using System;
using System.Collections.Generic;
using Avalonia;
using GimmeCapture.Models;
using SkiaSharp;

namespace GimmeCapture.Services.Core.Rendering;

/// <summary>
/// Burns a clip's <see cref="RedactionTrack"/>s into a single decoded frame at a given source time:
/// samples each track's interpolated box (<see cref="RedactionInterpolator"/>), maps it to frame pixels,
/// and obscures it. Blur/Mosaic reuse the existing <see cref="AnnotationRenderService"/> effect path;
/// SolidBlack fills the box. Called per frame by the video exporter's composite hook.
/// </summary>
public static class RedactionRenderer
{
    /// <summary>
    /// Maps a normalized ([0,1]) box to a pixel rect clamped to the frame bounds. Pure so the
    /// normalized→pixel mapping can be unit-tested without a render surface.
    /// </summary>
    public static SKRect ToPixelRect(RedactionBox box, int frameWidth, int frameHeight)
    {
        float x = (float)(box.X * frameWidth);
        float y = (float)(box.Y * frameHeight);
        float right = (float)((box.X + box.Width) * frameWidth);
        float bottom = (float)((box.Y + box.Height) * frameHeight);

        float left = Math.Clamp(Math.Min(x, right), 0, frameWidth);
        float top = Math.Clamp(Math.Min(y, bottom), 0, frameHeight);
        float r = Math.Clamp(Math.Max(x, right), 0, frameWidth);
        float b = Math.Clamp(Math.Max(y, bottom), 0, frameHeight);
        return new SKRect(left, top, r, b);
    }

    /// <summary>
    /// Obscures every track's tracked object in <paramref name="frame"/> at <paramref name="timeSeconds"/>
    /// (source time). No-op when there are no tracks or none is active at this time.
    /// </summary>
    public static void Render(SKBitmap frame, IReadOnlyList<RedactionTrack>? tracks, double timeSeconds)
    {
        if (frame == null || tracks == null || tracks.Count == 0)
        {
            return;
        }

        int fw = frame.Width;
        int fh = frame.Height;
        List<Annotation>? effectAnnotations = null;

        foreach (RedactionTrack track in tracks)
        {
            RedactionBox? box = RedactionInterpolator.EvaluateAt(track, timeSeconds);
            if (box is null)
            {
                continue;
            }

            SKRect rect = ToPixelRect(box.Value, fw, fh);
            if (rect.Width < 1 || rect.Height < 1)
            {
                continue;
            }

            if (track.Effect == RedactionEffect.SolidBlack)
            {
                using var canvas = new SKCanvas(frame);
                using var paint = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Fill, IsAntialias = false };
                canvas.DrawRect(rect, paint);
            }
            else
            {
                effectAnnotations ??= new List<Annotation>();
                effectAnnotations.Add(new Annotation
                {
                    Type = track.Effect == RedactionEffect.Mosaic ? AnnotationType.Mosaic : AnnotationType.Blur,
                    StartPoint = new Point(rect.Left, rect.Top),
                    EndPoint = new Point(rect.Right, rect.Bottom),
                });
            }
        }

        if (effectAnnotations != null)
        {
            // reference == target size → annotation pixel coords are used 1:1 (no scaling).
            AnnotationRenderService.Shared.RenderAnnotationsToBitmap(frame, effectAnnotations, fw, fh, fw, fh);
        }
    }
}
