using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;

namespace GimmeCapture.Services.Abstractions;

public interface IScreenLayoutService
{
    IReadOnlyList<Rect> BuildRelativeScreenBounds(IReadOnlyList<PixelRect> screenBounds, PixelPoint windowPosition, double renderScaling);

    Rect? TryGetActiveScreenBounds(Screens screens, Window window, Point logicalWindowPoint, double renderScaling);

    bool TryGetUnifiedDesktopPlacement(IReadOnlyList<PixelRect> screenBounds, double unifiedScaling, out PixelPoint windowPosition, out Size logicalSize);
}
