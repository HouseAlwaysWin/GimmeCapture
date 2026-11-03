using System;
using System.Threading.Tasks;
using SkiaSharp;

namespace GimmeCapture.Services.Abstractions;

/// <summary>
/// U2Net background-removal engine. Interface over <c>BackgroundRemovalService</c> so the pin depends on an abstraction
/// rather than the concrete class. The app has one, shared by every image pin and disposed at exit: a service for each
/// pin built a session of its own, 180 MB more and half a second longer when a second pin's removal came while the first
/// pin's session was still kept warm.
/// </summary>
public interface IBackgroundRemovalService : IDisposable
{
    /// <summary>
    /// Removes the background from <paramref name="source"/>, confined to <paramref name="selectionRect"/>
    /// (in source pixels) when one is given. <paramref name="source"/> stays the caller's to dispose; the
    /// returned bitmap becomes the caller's.
    /// </summary>
    Task<SKBitmap> RemoveBackgroundAsync(SKBitmap source, Avalonia.Rect? selectionRect = null);
}
