using System;
using System.Threading.Tasks;

namespace GimmeCapture.Services.Abstractions;

/// <summary>
/// U2Net background-removal engine used by the image pin. Interface over <c>BackgroundRemovalService</c> so the
/// pin depends on an abstraction rather than the concrete class.
/// </summary>
public interface IBackgroundRemovalService : IDisposable
{
    /// <summary>True once disposed (e.g. by a global AI unload); a reuse holder must recreate a fresh instance.</summary>
    bool IsDisposed { get; }

    Task<byte[]> RemoveBackgroundAsync(byte[] imageBytes, Avalonia.Rect? selectionRect = null);
}
