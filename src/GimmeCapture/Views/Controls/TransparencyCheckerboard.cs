using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace GimmeCapture.Views.Controls;

/// <summary>
/// The checkerboard behind an image's transparent parts: 10x10 tiles of white with two #CCCCCC 5x5 squares. It was a
/// VisualBrush of two rectangles, which Avalonia records and rasterizes again each time it is drawn: drawn on the CPU
/// ("Use less memory"), filling a 1280x720 pin with it cost about as much as the rest of the frame. This repeats a 10x10
/// bitmap with a Skia shader instead, which costs almost nothing: at whole scales pixel for pixel, at fractional ones
/// smoothed. An ImageBrush of the same tile is no substitute: Avalonia 12 draws its tiles unscaled past 100%.
/// </summary>
public sealed class TransparencyCheckerboard : Control
{
    private const int TileSize = 10;

    private static readonly SKImage Tile = CreateTile();

    public override void Render(DrawingContext context)
    {
        if (Bounds.Width > 0 && Bounds.Height > 0)
        {
            context.Custom(new CheckerboardOperation(new Rect(Bounds.Size)));
        }
    }

    private static SKImage CreateTile()
    {
        using var bitmap = new SKBitmap(TileSize, TileSize, SKColorType.Bgra8888, SKAlphaType.Premul);
        int half = TileSize / 2;
        for (int y = 0; y < TileSize; y++)
        {
            for (int x = 0; x < TileSize; x++)
            {
                bitmap.SetPixel(x, y, (x < half) == (y < half) ? new SKColor(0xCC, 0xCC, 0xCC) : SKColors.White);
            }
        }

        return SKImage.FromBitmap(bitmap);
    }

    private sealed class CheckerboardOperation : ICustomDrawOperation
    {
        private readonly Rect _rect;

        public CheckerboardOperation(Rect rect) => _rect = rect;

        public Rect Bounds => _rect;

        public bool HitTest(Point p) => false;

        public bool Equals(ICustomDrawOperation? other) => other is CheckerboardOperation checkerboard && checkerboard._rect == _rect;

        public void Dispose()
        {
        }

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } skia)
            {
                context.DrawRectangle(Brushes.LightGray, null, _rect);
                return;
            }

            using var lease = skia.Lease();
            var canvas = lease.SkCanvas;
            var matrix = canvas.TotalMatrix;
            bool wholeScale = matrix.SkewX == 0 && matrix.SkewY == 0 && IsWhole(matrix.ScaleX) && IsWhole(matrix.ScaleY);
            using var shader = SKShader.CreateImage(
                Tile,
                SKShaderTileMode.Repeat,
                SKShaderTileMode.Repeat,
                new SKSamplingOptions(wholeScale ? SKFilterMode.Nearest : SKFilterMode.Linear));
            using var paint = new SKPaint
            {
                IsAntialias = true,
                Shader = shader,
                Color = SKColors.White.WithAlpha((byte)(255 * lease.CurrentOpacity)),
            };
            canvas.DrawRect(new SKRect((float)_rect.X, (float)_rect.Y, (float)_rect.Right, (float)_rect.Bottom), paint);
        }

        private static bool IsWhole(float value) => Math.Abs(value - MathF.Round(value)) < 0.001f;
    }
}
