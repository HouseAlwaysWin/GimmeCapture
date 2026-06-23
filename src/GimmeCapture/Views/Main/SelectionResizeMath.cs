using System;
using Avalonia;

namespace GimmeCapture.Views.Main;

/// <summary>
/// Pure selection/translation-box resize math, extracted from the duplicated inline
/// switches in <c>SnipWindow.Pointer.cs</c> and <c>SnipWindow.Pointer.Translation.cs</c>
/// so it can be unit tested without a UI thread. Behavior-preserving.
/// </summary>
internal static class SelectionResizeMath
{
    /// <summary>
    /// Applies a drag delta in the given direction to a rectangle and returns the raw
    /// x/y/width/height. The result is NOT normalized — width/height may be negative if
    /// the drag crosses the opposite edge; callers apply their own post-processing
    /// (selection normalizes via <see cref="NormalizeRect"/>; translation min-clamps).
    /// </summary>
    public static (double X, double Y, double Width, double Height) ApplyResizeDelta(
        Rect original, ResizeDirection direction, double deltaX, double deltaY)
    {
        double x = original.X;
        double y = original.Y;
        double w = original.Width;
        double h = original.Height;

        switch (direction)
        {
            case ResizeDirection.TopLeft:
                x += deltaX; y += deltaY; w -= deltaX; h -= deltaY; break;
            case ResizeDirection.TopRight:
                y += deltaY; w += deltaX; h -= deltaY; break;
            case ResizeDirection.BottomLeft:
                x += deltaX; w -= deltaX; h += deltaY; break;
            case ResizeDirection.BottomRight:
                w += deltaX; h += deltaY; break;
            case ResizeDirection.Top:
                y += deltaY; h -= deltaY; break;
            case ResizeDirection.Bottom:
                h += deltaY; break;
            case ResizeDirection.Left:
                x += deltaX; w -= deltaX; break;
            case ResizeDirection.Right:
                w += deltaX; break;
        }

        return (x, y, w, h);
    }

    /// <summary>
    /// Normalizes a rectangle so width and height are non-negative, flipping the origin
    /// when a dimension is negative.
    /// </summary>
    public static Rect NormalizeRect(double x, double y, double w, double h)
    {
        if (w < 0) { x += w; w = Math.Abs(w); }
        if (h < 0) { y += h; h = Math.Abs(h); }
        return new Rect(x, y, w, h);
    }
}
