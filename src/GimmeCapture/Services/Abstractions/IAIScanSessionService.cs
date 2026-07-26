using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.AI;

namespace GimmeCapture.Services.Core.AI;

public enum AIScanStage
{
    DetectingObjects
}

public sealed record AIScanSessionRequest(
    Rect ViewportBounds,
    PixelPoint ScreenOffset,
    double VisualScaling,
    OCRLanguage SourceLanguage,
    // Freeze-frame: OCR this pre-grabbed full-desktop still instead of grabbing the live screen. Required in
    // freeze mode because the overlay is then opaque (a live grab would capture the frozen image + chrome).
    // The service does NOT dispose it (the caller owns it).
    SkiaSharp.SKBitmap? PreCapturedFrame = null);

public sealed record AIScanSessionResult(
    IReadOnlyList<Rect> RawDetectedRects,
    IReadOnlyList<OcrCandidate> Candidates,
    bool IsReady = true,
    string? NotReadyStatusKey = null);

public interface IAIScanSessionService : IDisposable
{
    Task WarmUpSam2Async(CancellationToken ct = default);

    Task<AIScanSessionResult> RunScanAsync(
        AIScanSessionRequest request,
        IProgress<AIScanStage>? progress = null,
        CancellationToken ct = default);
}
