using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using GimmeCapture.Models;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.OCR;

namespace GimmeCapture.Services.Core.AI;

public sealed class AIScanSessionService : IAIScanSessionService
{
    private readonly IScreenCaptureService _captureService;
    private readonly AIResourceService _aiResourceService;
    private readonly AppSettingsService _settingsService;
    private readonly IOcrEngineFactory _ocrEngineFactory;
    private readonly SAM2Service _sam2Service;

    public AIScanSessionService(
        IScreenCaptureService captureService,
        AIResourceService aiResourceService,
        AppSettingsService settingsService,
        IOcrEngineFactory ocrEngineFactory)
    {
        _captureService = captureService ?? throw new ArgumentNullException(nameof(captureService));
        _aiResourceService = aiResourceService ?? throw new ArgumentNullException(nameof(aiResourceService));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _ocrEngineFactory = ocrEngineFactory ?? throw new ArgumentNullException(nameof(ocrEngineFactory));
        _sam2Service = new SAM2Service(aiResourceService, settingsService);
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

        return request.Engine switch
        {
            AIScanEngine.SAM2 => RunSam2ScanAsync(request, progress, ct),
            _ => RunOcrScanAsync(request, ct)
        };
    }

    public void Dispose()
    {
        _sam2Service.Dispose();
    }

    private async Task<AIScanSessionResult> RunSam2ScanAsync(
        AIScanSessionRequest request,
        IProgress<AIScanStage>? progress,
        CancellationToken ct)
    {
        if (!_aiResourceService.IsSAM2Ready(request.Sam2Variant))
        {
            return new AIScanSessionResult(
                request.Engine,
                Array.Empty<Rect>(),
                false,
                "StatusSAM2NotFound");
        }

        _settingsService.Settings.SelectedSAM2Variant = request.Sam2Variant;
        ct.ThrowIfCancellationRequested();

        using var bitmap = await _captureService.CaptureScreenAsync(
            new Rect(0, 0, request.ViewportBounds.Width, request.ViewportBounds.Height),
            request.ScreenOffset,
            request.VisualScaling,
            false);

        ct.ThrowIfCancellationRequested();
        await _sam2Service.InitializeAsync();

        progress?.Report(AIScanStage.EncodingImage);
        await _sam2Service.SetImageAsync(bitmap);

        progress?.Report(AIScanStage.DetectingObjects);
        var rects = await _sam2Service.AutoDetectObjectsAsync(
            Math.Max(24, request.Sam2GridDensity),
            request.Sam2MaxObjects,
            request.Sam2MinObjectSize,
            ct);

        double scale = request.VisualScaling > 0 ? request.VisualScaling : 1.0;
        double viewportArea = request.ViewportBounds.Width * request.ViewportBounds.Height;
        var logicalRects = rects
            .AsValueEnumerable()
            .Select(rect => new Rect(
                rect.X / scale,
                rect.Y / scale,
                rect.Width / scale,
                rect.Height / scale))
            .Where(rect =>
            {
                double area = rect.Width * rect.Height;
                return rect.Width >= 20
                    && rect.Height >= 20
                    && area < (viewportArea * 0.95);
            })
            .ToList();

        return new AIScanSessionResult(request.Engine, logicalRects);
    }

    private async Task<AIScanSessionResult> RunOcrScanAsync(
        AIScanSessionRequest request,
        CancellationToken ct)
    {
        _settingsService.Settings.SourceLanguage = request.SourceLanguage;
        bool ready = await _aiResourceService.EnsureOCRAsync(request.SourceLanguage, ct);
        if (!ready)
        {
            return new AIScanSessionResult(request.Engine, Array.Empty<Rect>(), false);
        }

        ct.ThrowIfCancellationRequested();
        using var bitmap = await _captureService.CaptureScreenAsync(
            new Rect(0, 0, request.ViewportBounds.Width, request.ViewportBounds.Height),
            request.ScreenOffset,
            request.VisualScaling,
            false);

        ct.ThrowIfCancellationRequested();
        using var ocrEngine = _ocrEngineFactory.Create(_aiResourceService, _settingsService);
        await ocrEngine.EnsureLoadedAsync(request.SourceLanguage, ct);
        var textBoxes = await Task.Run(() => ocrEngine.DetectText(bitmap), ct);

        double scaleX = bitmap.Width > 0 ? request.ViewportBounds.Width / bitmap.Width : 1;
        double scaleY = bitmap.Height > 0 ? request.ViewportBounds.Height / bitmap.Height : 1;
        var logicalRects = textBoxes
            .AsValueEnumerable()
            .Select(box => new Rect(
                box.Left * scaleX,
                box.Top * scaleY,
                box.Width * scaleX,
                box.Height * scaleY))
            .Where(rect => rect.Width >= 12 && rect.Height >= 8)
            .ToList();

        return new AIScanSessionResult(request.Engine, logicalRects);
    }
}
