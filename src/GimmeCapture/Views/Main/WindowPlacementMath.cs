using System;
using Avalonia;

namespace GimmeCapture.Views.Main;

/// <summary>Where a window with the system's own frame goes on a screen. Pure, so it needs no window to be tested.</summary>
internal static class WindowPlacementMath
{
    // What the system's frame adds around a window's content, in logical pixels: the title bar and the borders.
    private const double FrameWidth = 16;
    private const double FrameHeight = 40;

    /// <summary>
    /// The content size that fits the working area, as close to <paramref name="preferred"/> as it allows and never under
    /// <paramref name="minimum"/>, and the position in physical pixels that centres the window there. On a screen too small
    /// for the minimum the window starts at the top left, so that its title bar can still be reached.
    /// </summary>
    internal static (Size Size, PixelPoint Position) FitCentred(
        Size preferred, Size minimum, PixelRect workingArea, double scaling)
    {
        double width = Math.Max(minimum.Width, Math.Min(preferred.Width, (workingArea.Width / scaling) - FrameWidth));
        double height = Math.Max(minimum.Height, Math.Min(preferred.Height, (workingArea.Height / scaling) - FrameHeight));

        int x = workingArea.X + (int)((workingArea.Width - ((width + FrameWidth) * scaling)) / 2);
        int y = workingArea.Y + (int)((workingArea.Height - ((height + FrameHeight) * scaling)) / 2);
        return (new Size(width, height), new PixelPoint(Math.Max(workingArea.X, x), Math.Max(workingArea.Y, y)));
    }
}
