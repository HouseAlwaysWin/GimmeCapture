using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using GimmeCapture.Services.Abstractions;

namespace GimmeCapture.Services.Platforms.Desktop;

public class AvaloniaScreenLayoutService : IScreenLayoutService
{
    public IReadOnlyList<Rect> BuildRelativeScreenBounds(IReadOnlyList<PixelRect> screenBounds, PixelPoint windowPosition, double renderScaling)
    {
        var result = new List<Rect>(screenBounds.Count);
        foreach (var bounds in screenBounds)
        {
            result.Add(new Rect(
                x: (bounds.X - windowPosition.X) / renderScaling,
                y: (bounds.Y - windowPosition.Y) / renderScaling,
                width: bounds.Width / renderScaling,
                height: bounds.Height / renderScaling));
        }

        return result;
    }

    public Rect? TryGetActiveScreenBounds(Screens screens, Window window, Point logicalWindowPoint, double renderScaling)
    {
        var physicalPoint = window.PointToScreen(logicalWindowPoint);
        var activeScreen = screens.ScreenFromPoint(physicalPoint);
        if (activeScreen == null)
        {
            return null;
        }

        return new Rect(
            x: (activeScreen.Bounds.X - window.Position.X) / renderScaling,
            y: (activeScreen.Bounds.Y - window.Position.Y) / renderScaling,
            width: activeScreen.Bounds.Width / renderScaling,
            height: activeScreen.Bounds.Height / renderScaling);
    }

    public bool TryGetUnifiedDesktopPlacement(IReadOnlyList<PixelRect> screenBounds, double unifiedScaling, out PixelPoint windowPosition, out Size logicalSize)
    {
        if (screenBounds.Count == 0 || unifiedScaling <= 0)
        {
            windowPosition = default;
            logicalSize = default;
            return false;
        }

        int minX = int.MaxValue;
        int minY = int.MaxValue;
        int maxRight = int.MinValue;
        int maxBottom = int.MinValue;

        foreach (var bounds in screenBounds)
        {
            if (bounds.X < minX) minX = bounds.X;
            if (bounds.Y < minY) minY = bounds.Y;
            if (bounds.Right > maxRight) maxRight = bounds.Right;
            if (bounds.Bottom > maxBottom) maxBottom = bounds.Bottom;
        }

        windowPosition = new PixelPoint(minX, minY);
        logicalSize = new Size(
            width: (maxRight - minX) / unifiedScaling,
            height: (maxBottom - minY) / unifiedScaling);
        return true;
    }
}
