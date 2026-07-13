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

    Task InitializeAsync();
    Task EnsureImagePreparedAsync(SKBitmap original);
    Task<SKBitmap?> GetMaskBitmapAsync(IReadOnlyList<(double X, double Y, bool IsPositive)> points);
    void InvalidatePreparedImage();
}
