using System;
using System.Threading.Tasks;
using SkiaSharp;

namespace GimmeCapture.Services.Abstractions;

/// <summary>
/// U2Net background-removal engine used by the image pin. Interface over <c>BackgroundRemovalService</c> so the
/// pin depends on an abstraction rather than the concrete class.
/// </summary>
public interface IBackgroundRemovalService : IDisposable
{
    /// <summary>True once disposed (e.g. by a global AI unload); a reuse holder must recreate a fresh instance.</summary>
    bool IsDisposed { get; }

    /// <summary>
    /// Removes the background from <paramref name="source"/>, confined to <paramref name="selectionRect"/>
    /// (in source pixels) when one is given. <paramref name="source"/> stays the caller's to dispose; the
    /// returned bitmap becomes the caller's.
    /// </summary>
    Task<SKBitmap> RemoveBackgroundAsync(SKBitmap source, Avalonia.Rect? selectionRect = null);
}
