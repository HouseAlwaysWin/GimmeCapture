using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SkiaSharp;

namespace GimmeCapture.Services.Abstractions;

/// <summary>
/// SAM2 interactive-segmentation engine used by the image pin. Interface over <c>SAM2Service</c> so the pin's AI
/// flow can depend on an abstraction (and be exercised with a substitute in tests) rather than the concrete class.
/// </summary>
public interface ISam2Service : IDisposable
{
    string LastIouInfo { get; }
    string ModelVariantName { get; }
    /// <summary>Whether an image is prepared; false after <see cref="InvalidatePreparedImage"/> and a switch of model.</summary>
    bool IsImagePrepared { get; }

    Task InitializeAsync();

    /// <summary>
    /// Prepares <paramref name="original"/> for clicks unless it is prepared already. An invalidation or a dispose while
    /// it runs leaves nothing prepared, and a disposed service prepares nothing.
    /// </summary>
    Task EnsureImagePreparedAsync(SKBitmap original);

    /// <summary>
    /// The mask of <paramref name="points"/> on the prepared image, the caller's to dispose; null when none is prepared.
    /// <paramref name="points"/> may be read after this returns, on another thread: hand over a list nothing changes.
    /// </summary>
    Task<SKBitmap?> GetMaskBitmapAsync(IReadOnlyList<(double X, double Y, bool IsPositive)> points);

    void InvalidatePreparedImage();
}
