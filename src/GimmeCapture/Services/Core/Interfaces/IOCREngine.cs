using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Models;
using SkiaSharp;

namespace GimmeCapture.Services.Core.Interfaces;

public interface IOCREngine : IDisposable
{
    Task EnsureLoadedAsync(OCRLanguage lang, CancellationToken ct = default);

    /// <summary>Loads what <see cref="DetectText"/> needs. An engine with nothing smaller to load loads everything.</summary>
    Task EnsureDetectionLoadedAsync(OCRLanguage lang, CancellationToken ct = default) => EnsureLoadedAsync(lang, ct);

    /// <summary>
    /// Runs detection once on a blank capture of this size, so that a real one at that size is not the one that pays for
    /// setting up. Nothing to do for an engine with no such cost.
    /// </summary>
    void WarmUpDetection(int captureWidth, int captureHeight)
    {
    }

    List<SKRectI> DetectText(SKBitmap bitmap);
    (string text, float confidence) RecognizeText(SKBitmap bitmap, SKRectI box, CancellationToken ct = default);
}
