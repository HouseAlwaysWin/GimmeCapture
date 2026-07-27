using Avalonia;
using GimmeCapture.Models;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.Interfaces;
using SkiaSharp;

namespace GimmeCapture.Tests;

public sealed class AIScanSessionServiceTests : IDisposable
{
    private readonly string _baseDir;

    public AIScanSessionServiceTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_baseDir);
    }

    [Fact]
    public async Task RunScanAsync_WhenOcrDetectsText_ReturnsLogicalRects()
    {
        var captureService = new Mock<IScreenCaptureService>();
        var settingsService = new AppSettingsService(_baseDir);
        var pathService = new AIPathService(settingsService);
        var resolver = new NativeResolverService(pathService);
        var downloader = new Mock<AIModelDownloader>();
        var aiResourceService = new Mock<AIResourceService>(settingsService, pathService, resolver, downloader.Object);
        var sam2RuntimeService = new SAM2RuntimeService(pathService, resolver);
        var ocrRuntimeService = new OcrRuntimeService(aiResourceService.Object);
        var ocrEngineFactory = new Mock<IOcrEngineFactory>();
        var ocrEngine = new FakeOcrEngine(new List<SKRectI> { new(10, 12, 40, 36) });
        using var bitmap = CreateBitmap(100, 50, SKColors.White);

        aiResourceService
            .Setup(service => service.EnsureOCRAsync(OCRLanguage.Japanese, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        captureService
            .Setup(service => service.CaptureScreenAsync(It.IsAny<Rect>(), It.IsAny<PixelPoint>(), 1.0, false))
            .ReturnsAsync(bitmap.Copy());

        ocrEngineFactory
            .Setup(factory => factory.Create(aiResourceService.Object, settingsService, ocrRuntimeService))
            .Returns(ocrEngine);

        using var sut = new AIScanSessionService(
            captureService.Object,
            aiResourceService.Object,
            sam2RuntimeService,
            ocrRuntimeService,
            settingsService,
            ocrEngineFactory.Object);

        var result = await sut.RunScanAsync(new AIScanSessionRequest(
            new Rect(0, 0, 100, 50),
            new PixelPoint(0, 0),
            1.0,
            OCRLanguage.Japanese));

        var rect = Assert.Single(result.RawDetectedRects);
        Assert.True(result.IsReady);
        Assert.Equal(new Rect(10, 12, 30, 24), rect);
        Assert.Equal(2, result.Candidates.Count);
        Assert.Contains(result.Candidates, candidate => candidate.Kind == OcrCandidateKind.Line);
        Assert.Contains(result.Candidates, candidate => candidate.Kind == OcrCandidateKind.Paragraph);
    }

    [Fact]
    public async Task RunScanAsync_TakesOwnershipOfThePreCapturedFrame()
    {
        // A scan outlives the overlay that started it — Esc closes the snip window while inference is still
        // running — so the frame it scans must be one nobody else can free. Sharing the window's own frozen still
        // meant the scan's continuation read an SKBitmap the closing window had already disposed, faulting the
        // process. The service owning (and releasing) the frame is what makes that impossible.
        var captureService = new Mock<IScreenCaptureService>();
        var settingsService = new AppSettingsService(_baseDir);
        var pathService = new AIPathService(settingsService);
        var resolver = new NativeResolverService(pathService);
        var downloader = new Mock<AIModelDownloader>();
        var aiResourceService = new Mock<AIResourceService>(settingsService, pathService, resolver, downloader.Object);
        var sam2RuntimeService = new SAM2RuntimeService(pathService, resolver);
        var ocrRuntimeService = new OcrRuntimeService(aiResourceService.Object);
        var ocrEngineFactory = new Mock<IOcrEngineFactory>();
        var ocrEngine = new FakeOcrEngine(new List<SKRectI> { new(10, 12, 40, 36) });
        var frozenFrame = CreateBitmap(100, 50, SKColors.Black);

        aiResourceService
            .Setup(service => service.EnsureOCRAsync(OCRLanguage.Japanese, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        ocrEngineFactory
            .Setup(factory => factory.Create(aiResourceService.Object, settingsService, ocrRuntimeService))
            .Returns(ocrEngine);

        using var sut = new AIScanSessionService(
            captureService.Object,
            aiResourceService.Object,
            sam2RuntimeService,
            ocrRuntimeService,
            settingsService,
            ocrEngineFactory.Object);

        var result = await sut.RunScanAsync(new AIScanSessionRequest(
            new Rect(0, 0, 100, 50),
            new PixelPoint(0, 0),
            1.0,
            OCRLanguage.Japanese,
            PreCapturedFrame: frozenFrame));

        Assert.True(result.IsReady);
        Assert.Equal(IntPtr.Zero, frozenFrame.Handle);
        // The supplied frame is scanned as-is; no live grab, which in freeze mode would capture the overlay itself.
        captureService.Verify(
            service => service.CaptureScreenAsync(It.IsAny<Rect>(), It.IsAny<PixelPoint>(), It.IsAny<double>(), It.IsAny<bool>()),
            Times.Never);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_baseDir))
            {
                Directory.Delete(_baseDir, true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static SKBitmap CreateBitmap(int width, int height, SKColor color)
    {
        var bitmap = new SKBitmap(width, height, true);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(color);
        return bitmap;
    }

    private sealed class FakeOcrEngine : IOCREngine
    {
        private readonly List<SKRectI> _boxes;

        public FakeOcrEngine(List<SKRectI> boxes)
        {
            _boxes = boxes;
        }

        public Task EnsureLoadedAsync(OCRLanguage lang, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public List<SKRectI> DetectText(SKBitmap bitmap)
        {
            return _boxes;
        }

        public (string text, float confidence) RecognizeText(SKBitmap bitmap, SKRectI box, CancellationToken ct = default)
        {
            return (string.Empty, 0f);
        }

        public void Dispose()
        {
        }
    }
}
