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

    List<SKRectI> DetectText(SKBitmap bitmap);

    /// <summary>
    /// <see cref="DetectText(SKBitmap)"/> that a cancelled scan leaves undone: inference runs one at a time, and a scan
    /// cancelled while it waited behind another ran its detection anyway once its turn came.
    /// </summary>
    List<SKRectI> DetectText(SKBitmap bitmap, CancellationToken ct) => DetectText(bitmap);
    (string text, float confidence) RecognizeText(SKBitmap bitmap, SKRectI box, CancellationToken ct = default);
}
