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
    List<SKRectI> DetectText(SKBitmap bitmap);
    (string text, float confidence) RecognizeText(SKBitmap bitmap, SKRectI box, CancellationToken ct = default);
}
