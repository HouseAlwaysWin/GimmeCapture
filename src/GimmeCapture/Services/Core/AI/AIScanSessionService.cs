using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using GimmeCapture.Models;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Services.Core.AI;

public sealed class AIScanSessionService : IAIScanSessionService
{
    private readonly IScreenCaptureService _captureService;
    private readonly AIResourceService _aiResourceService;
    private readonly SAM2RuntimeService _sam2RuntimeService;
    private readonly OcrRuntimeService _ocrRuntimeService;
    private readonly IAppSettingsService _settingsService;
    private readonly IOcrEngineFactory _ocrEngineFactory;
    private readonly SAM2Service _sam2Service;

    public AIScanSessionService(
        IScreenCaptureService captureService,
        AIResourceService aiResourceService,
        SAM2RuntimeService sam2RuntimeService,
        OcrRuntimeService ocrRuntimeService,
        IAppSettingsService settingsService,
        IOcrEngineFactory ocrEngineFactory)
    {
        _captureService = captureService ?? throw new ArgumentNullException(nameof(captureService));
        _aiResourceService = aiResourceService ?? throw new ArgumentNullException(nameof(aiResourceService));
        _sam2RuntimeService = sam2RuntimeService ?? throw new ArgumentNullException(nameof(sam2RuntimeService));
        _ocrRuntimeService = ocrRuntimeService ?? throw new ArgumentNullException(nameof(ocrRuntimeService));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _ocrEngineFactory = ocrEngineFactory ?? throw new ArgumentNullException(nameof(ocrEngineFactory));
        _sam2Service = new SAM2Service(_sam2RuntimeService, settingsService);
    }

    public Task WarmUpSam2Async(CancellationToken ct = default)
    {
        return _sam2Service.InitializeAsync();
    }

    public Task<AIScanSessionResult> RunScanAsync(
        AIScanSessionRequest request,
        IProgress<AIScanStage>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunOcrScanAsync(request, progress, ct);
    }

    public void Dispose()
    {
        _sam2Service.Dispose();
    }

    private async Task<AIScanSessionResult> RunOcrScanAsync(
        AIScanSessionRequest request,
        IProgress<AIScanStage>? progress,
        CancellationToken ct)
    {
        _settingsService.Settings.SourceLanguage = request.SourceLanguage;
        bool ready = await _aiResourceService.EnsureOCRAsync(request.SourceLanguage, ct);
        if (!ready)
        {
            return new AIScanSessionResult(Array.Empty<Rect>(), Array.Empty<OcrCandidate>(), false, "StatusOCRNotReady");
        }

        ct.ThrowIfCancellationRequested();
        // Freeze-frame: OCR the caller's pre-grabbed still instead of the live screen — required in freeze mode
        // because the overlay is then opaque (a live grab would capture the frozen image + chrome). The caller
        // owns the pre-captured bitmap, so only dispose one we grabbed ourselves.
        SkiaSharp.SKBitmap bitmap = request.PreCapturedFrame
            ?? await _captureService.CaptureScreenAsync(
                new Rect(0, 0, request.ViewportBounds.Width, request.ViewportBounds.Height),
                request.ScreenOffset,
                request.VisualScaling,
                false);
        bool ownsBitmap = request.PreCapturedFrame is null;

        try
        {
            ct.ThrowIfCancellationRequested();
            using var ocrEngine = _ocrEngineFactory.Create(_aiResourceService, _settingsService, _ocrRuntimeService);
            await ocrEngine.EnsureLoadedAsync(request.SourceLanguage, ct);
            progress?.Report(AIScanStage.DetectingObjects);
            var textBoxes = await Task.Run(() => ocrEngine.DetectText(bitmap), ct);
            // Diagnostic (release-visible): distinguishes "capture failed" (0x0 bitmap) from "OCR found nothing"
            // (0 boxes on a real capture — e.g. a DirectML inference-correctness issue on some Win10 setups) from
            // "OCR works". Pair with OcrRuntime.SessionCreated (GPU vs CPU) to pinpoint a Win10-vs-Win11 discrepancy.
            AppLog.Information($"OcrScan.Detected: {textBoxes.Count} text boxes from a {bitmap.Width}x{bitmap.Height} capture (lang={request.SourceLanguage}, frozen={!ownsBitmap}).");

            double scaleX = bitmap.Width > 0 ? request.ViewportBounds.Width / bitmap.Width : 1;
            double scaleY = bitmap.Height > 0 ? request.ViewportBounds.Height / bitmap.Height : 1;
            var rawLogicalRects = new List<Rect>(textBoxes.Count);
            foreach (var box in textBoxes.AsValueEnumerable())
            {
                var rect = new Rect(
                    box.Left * scaleX,
                    box.Top * scaleY,
                    box.Width * scaleX,
                    box.Height * scaleY);
                if (rect.Width >= 12 && rect.Height >= 8)
                {
                    rawLogicalRects.Add(rect);
                }
            }

            var grouped = OcrCandidateGrouper.Group(rawLogicalRects);
            return new AIScanSessionResult(grouped.RawRects, grouped.AllCandidates);
        }
        finally
        {
            if (ownsBitmap) bitmap.Dispose();
        }
    }
}
