using System;
using System.IO;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Infrastructure;
using SkiaSharp;

namespace GimmeCapture.Views.Shared;

/// <summary>
/// Lazily-built, cached custom cursors for annotation tools that have no fitting
/// <see cref="StandardCursorType"/>: a pen nib for freehand/highlighter, and a square for mosaic/blur.
/// Rendered once with SkiaSharp (the app's imaging lib) so we don't ship binary cursor assets. Any
/// failure falls back to the crosshair, matching the previous behavior.
/// </summary>
internal static class DrawingToolCursors
{
    private const int Size = 32;
    private static readonly Cursor Fallback = new(StandardCursorType.Cross);
    private static readonly Cursor Ibeam = new(StandardCursorType.Ibeam);

    private static Cursor? _pen;
    private static Cursor? _box;

    /// <summary>Pencil glyph; hotspot at the tip.</summary>
    public static Cursor Pen => _pen ??= Build(DrawPen, hotspotX: 3, hotspotY: 29) ?? Fallback;

    /// <summary>Square-outline glyph; hotspot at the centre.</summary>
    public static Cursor Box => _box ??= Build(DrawBox, hotspotX: 16, hotspotY: 16) ?? Fallback;

    /// <summary>
    /// The cursor for an active annotation tool. Single source of truth shared by the reactive
    /// XAML binding (<c>AnnotationToolToCursorConverter</c>) and the pointer-move cursor logic
    /// (<c>AnnotationInputController</c>): Text = I-beam, Pen/Highlighter = pen, Mosaic/Blur = box,
    /// shape tools = crosshair, None = the default arrow.
    /// </summary>
    public static Cursor ForTool(AnnotationType tool) => tool switch
    {
        AnnotationType.None => Cursor.Default,
        AnnotationType.Text => Ibeam,
        AnnotationType.Pen or AnnotationType.Highlighter => Pen,
        AnnotationType.Mosaic or AnnotationType.Blur => Box,
        _ => Fallback,
    };

    private static Cursor? Build(Action<SKCanvas> draw, int hotspotX, int hotspotY)
    {
        try
        {
            using var surface = SKSurface.Create(new SKImageInfo(Size, Size, SKColorType.Bgra8888, SKAlphaType.Premul));
            SKCanvas canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            draw(canvas);
            canvas.Flush();

            using SKImage image = surface.Snapshot();
            using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var ms = new MemoryStream();
            data.SaveTo(ms);
            ms.Position = 0;

            var bitmap = new Bitmap(ms);
            return new Cursor(bitmap, new PixelPoint(hotspotX, hotspotY));
        }
        catch (Exception ex)
        {
            AppLog.Warning("DrawingToolCursors.Build", ex);
            return null;
        }
    }

    // Pencil: a diagonal body from the tip (bottom-left) to the top-right, drawn white over a dark
    // outline so it reads on any background, with a solid nib at the tip (the hotspot).
    private static void DrawPen(SKCanvas c)
    {
        var tip = new SKPoint(3, 29);
        var top = new SKPoint(26, 6);

        using var outline = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 5.5f, Color = SKColors.Black, IsAntialias = true, StrokeCap = SKStrokeCap.Round };
        using var body = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 3f, Color = SKColors.White, IsAntialias = true, StrokeCap = SKStrokeCap.Round };
        c.DrawLine(tip, top, outline);
        c.DrawLine(tip, top, body);

        using var nib = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.Black, IsAntialias = true };
        c.DrawCircle(tip, 2.4f, nib);
    }

    // Square outline centred in the bitmap; white line over a dark halo. Hotspot at the centre.
    private static void DrawBox(SKCanvas c)
    {
        var rect = new SKRect(7, 7, 25, 25);
        using var halo = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 4f, Color = SKColors.Black, IsAntialias = true };
        using var line = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 2f, Color = SKColors.White, IsAntialias = true };
        c.DrawRect(rect, halo);
        c.DrawRect(rect, line);
    }
}
