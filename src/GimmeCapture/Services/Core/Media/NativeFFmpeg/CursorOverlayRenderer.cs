using System;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace GimmeCapture.Services.Core.Media.NativeFFmpeg;

/// <summary>
/// Draws a cursor spotlight ring and a click ripple onto each recorded frame (a BGRA
/// <see cref="SKBitmap"/>). Holds the small amount of animation state needed for the ripple, so it is
/// created once per recording session and called per frame from the live encode loop.
/// </summary>
internal sealed class CursorOverlayRenderer : IDisposable
{
    private const int RippleDurationFrames = 12;

    private readonly int _offsetX;
    private readonly int _offsetY;
    private readonly bool _drawRing;
    private readonly bool _drawClicks;

    // Paints are created once per session and reused across frames (only the ripple's colour changes per
    // frame) instead of allocating three SKPaints on every recorded frame.
    private readonly SKPaint? _glowPaint;
    private readonly SKPaint? _ringPaint;
    private readonly SKPaint? _ripplePaint;

    private bool _wasDown;
    private int _rippleFrames;
    private float _rippleX;
    private float _rippleY;

    public CursorOverlayRenderer(int offsetX, int offsetY, bool drawRing, bool drawClicks)
    {
        _offsetX = offsetX;
        _offsetY = offsetY;
        _drawRing = drawRing;
        _drawClicks = drawClicks;

        if (drawRing)
        {
            _glowPaint = new SKPaint { Color = new SKColor(255, 215, 0, 60), IsAntialias = true, Style = SKPaintStyle.Fill };
            _ringPaint = new SKPaint { Color = new SKColor(255, 215, 0, 160), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3 };
        }
        if (drawClicks)
        {
            _ripplePaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3 };
        }
    }

    public void Dispose()
    {
        _glowPaint?.Dispose();
        _ringPaint?.Dispose();
        _ripplePaint?.Dispose();
    }

    /// <summary>Draws the overlay for the current cursor position onto <paramref name="bmp"/> (BGRA).</summary>
    public void Draw(SKBitmap bmp)
    {
        if (!TryGetCursorPos(out int sx, out int sy))
        {
            return;
        }

        // Capture-region-relative coordinates.
        float x = sx - _offsetX;
        float y = sy - _offsetY;

        using var canvas = new SKCanvas(bmp);

        if (_drawClicks)
        {
            bool down = IsLeftButtonDown();
            if (down && !_wasDown)
            {
                _rippleFrames = RippleDurationFrames;
                _rippleX = x;
                _rippleY = y;
            }
            _wasDown = down;

            if (_rippleFrames > 0 && _ripplePaint != null)
            {
                float t = 1f - (_rippleFrames / (float)RippleDurationFrames); // 0 → 1 over the ripple
                float radius = 10f + t * 30f;
                byte alpha = (byte)(170 * (1f - t));
                _ripplePaint.Color = new SKColor(255, 80, 80, alpha);
                canvas.DrawCircle(_rippleX, _rippleY, radius, _ripplePaint);
                _rippleFrames--;
            }
        }

        if (_drawRing && _glowPaint != null && _ringPaint != null
            && x >= -40 && y >= -40 && x <= bmp.Width + 40 && y <= bmp.Height + 40)
        {
            canvas.DrawCircle(x, y, 20, _glowPaint);
            canvas.DrawCircle(x, y, 20, _ringPaint);
        }
    }

    // ── Win32 cursor / button state ──
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private const int VK_LBUTTON = 0x01;

    private static bool TryGetCursorPos(out int x, out int y)
    {
        try
        {
            if (GetCursorPos(out POINT p))
            {
                x = p.X;
                y = p.Y;
                return true;
            }
        }
        catch
        {
            // ignore — overlay is best-effort
        }

        x = 0;
        y = 0;
        return false;
    }

    private static bool IsLeftButtonDown()
    {
        try
        {
            return (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
        }
        catch
        {
            return false;
        }
    }
}
