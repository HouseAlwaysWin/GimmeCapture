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
    EncodingImage,
    DetectingObjects
}

public sealed record AIScanSessionRequest(
    AIScanEngine Engine,
    Rect ViewportBounds,
    PixelPoint ScreenOffset,
    double VisualScaling,
    SAM2Variant Sam2Variant,
    int Sam2GridDensity,
    int Sam2MaxObjects,
    int Sam2MinObjectSize,
    OCRLanguage SourceLanguage);

public sealed record AIScanSessionResult(
    AIScanEngine Engine,
    IReadOnlyList<Rect> DetectedRects,
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
