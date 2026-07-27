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
    //
    // OWNERSHIP TRANSFERS to the service, which disposes it. It must be a bitmap nobody else can free: a scan
    // outlives the overlay that started it (Esc closes the window while inference is still running), and passing
    // the snip window's own frozen still meant the scan's continuation read an SKBitmap the closing window had
    // already disposed — a native access violation, not a catchable exception.
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
