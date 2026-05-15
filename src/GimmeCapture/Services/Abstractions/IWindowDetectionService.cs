using System;
using System.Collections.Generic;
using Avalonia;

namespace GimmeCapture.Services.Abstractions;

public interface IWindowDetectionService
{
    List<Rect> GetVisibleWindowRects(IntPtr? excludeHWnd = null);
    Rect? GetRectAtPoint(Point point, List<Rect> windowRects);
}
