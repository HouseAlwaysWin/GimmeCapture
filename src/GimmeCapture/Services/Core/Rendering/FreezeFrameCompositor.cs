using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using GimmeCapture.Models;
using SkiaSharp;

namespace GimmeCapture.Services.Core.Rendering;

/// <summary>
/// Produces the final screenshot for freeze-frame capture: crops a pre-grabbed full-desktop still (physical
/// pixels, taken BEFORE the overlay appeared) to the on-screen selection and composites annotations on top.
/// The freeze-frame analog of <c>WindowsScreenCaptureService.CaptureScreenWithAnnotationsAsync</c>, but with NO
/// live screen grab. Freeze-frame is screenshot-only (never translation mode), so there is no translation-box
/// compositing here — only annotations via the shared <see cref="AnnotationRenderService"/>.
/// </summary>
public static class FreezeFrameCompositor
{
    /// <summary>
    /// Crops <paramref name="frozen"/> to <paramref name="selectionRect"/> × <paramref name="visualScaling"/>
    /// (the offset is already baked into the full-desktop still) and renders <paramref name="annotations"/> onto
    /// the crop. Returns a new detached bitmap; <paramref name="frozen"/> is not modified or disposed.
    /// </summary>
    public static SKBitmap CropWithAnnotations(SKBitmap frozen, Rect selectionRect, double visualScaling, IEnumerable<Annotation>? annotations)
    {
        ArgumentNullException.ThrowIfNull(frozen);

        // SelectionRect is overlay DIP; the still covers the whole desktop at visualScaling, so the crop is just
        // SelectionRect × scaling (no + ScreenOffset — the offset is baked into the still). Clamp to the still.
        int x = Math.Clamp((int)(selectionRect.X * visualScaling), 0, Math.Max(0, frozen.Width - 1));
        int y = Math.Clamp((int)(selectionRect.Y * visualScaling), 0, Math.Max(0, frozen.Height - 1));
        int w = Math.Clamp((int)(selectionRect.Width * visualScaling), 1, Math.Max(1, frozen.Width - x));
        int h = Math.Clamp((int)(selectionRect.Height * visualScaling), 1, Math.Max(1, frozen.Height - y));

        var cropped = new SKBitmap(w, h);
        using (var canvas = new SKCanvas(cropped))
        {
            canvas.DrawBitmap(frozen, new SKRect(x, y, x + w, y + h), new SKRect(0, 0, w, h));
        }

        // Annotations (incl. Mosaic/Blur, which read the underlying — now frozen — pixels) composite exactly as
        // in the live path: reference = selection DIP, target = cropped physical px (never touches ScreenOffset).
        if (annotations != null && annotations.Any())
        {
            AnnotationRenderService.Shared.RenderAnnotationsToBitmap(
                cropped, annotations, selectionRect.Width, selectionRect.Height, cropped.Width, cropped.Height);
        }

        return cropped;
    }
}
