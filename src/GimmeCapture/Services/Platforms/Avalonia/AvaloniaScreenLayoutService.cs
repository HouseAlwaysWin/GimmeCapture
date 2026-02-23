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
}
