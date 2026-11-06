using Avalonia;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.AI;
using GimmeCapture.Services.Core.Interfaces;
using SkiaSharp;

namespace GimmeCapture.Tests;

// Two screens read as one capture were detected at a third of their pixels: on two screens of UI text 4% of the lines
// got a box of their own, against 62% read screen by screen (OcrScanScreensProbeTests).
public sealed class OcrScanTilesTests
{
    private static readonly Rect TwoScreens = new(0, 0, 3968, 1160);

    [Fact]
    public void TwoScreensSideBySide_AreReadApart_InTheCapturesPixels()
    {
        var tiles = OcrScanTiles.ForScreens([new Rect(0, 0, 1920, 1080), new Rect(1920, 0, 2048, 1160)], TwoScreens, 3968, 1160);

        Assert.Equal([new SKRectI(0, 0, 1920, 1080), new SKRectI(1920, 0, 3968, 1160)], tiles);
    }

    [Fact]
    public void TheScreens_FollowTheCapturesScale()
    {
        // At 150% the overlay's coordinates are DIPs and the capture is in pixels.
        var tiles = OcrScanTiles.ForScreens(
            [new Rect(0, 0, 1280, 720), new Rect(1280, 0, 1280, 720)], new Rect(0, 0, 2560, 720), 3840, 1080);

        Assert.Equal([new SKRectI(0, 0, 1920, 1080), new SKRectI(1920, 0, 3840, 1080)], tiles);
    }

    [Fact]
    public void AScreenPartlyOutsideTheCapture_IsCutToIt()
    {
        var tiles = OcrScanTiles.ForScreens(
            [new Rect(-100, 0, 1100, 1000), new Rect(1000, -50, 1000, 1050)], new Rect(0, 0, 2000, 1000), 2000, 1000);

        Assert.Equal([new SKRectI(0, 0, 1000, 1000), new SKRectI(1000, 0, 2000, 1000)], tiles);
    }

    [Fact]
    public void ACaptureOnOneScreen_IsReadWhole()
    {
        Assert.Empty(OcrScanTiles.ForScreens(null, TwoScreens, 3968, 1160));
        Assert.Empty(OcrScanTiles.ForScreens([], TwoScreens, 3968, 1160));
        Assert.Empty(OcrScanTiles.ForScreens([new Rect(0, 0, 3968, 1160)], TwoScreens, 3968, 1160));
        // A mirrored display has the bounds of the one it mirrors.
        Assert.Empty(OcrScanTiles.ForScreens([new Rect(0, 0, 1920, 1080), new Rect(0, 0, 1920, 1080)], TwoScreens, 3968, 1160));
        // A screen beside the capture, or only a sliver of one in it, gets no detection of its own.
        Assert.Empty(OcrScanTiles.ForScreens([new Rect(0, 0, 1920, 1080), new Rect(3968, 0, 1920, 1080)], TwoScreens, 3968, 1160));
        Assert.Empty(OcrScanTiles.ForScreens([new Rect(0, 0, 3950, 1160), new Rect(3950, 0, 1920, 1080)], TwoScreens, 3968, 1160));
        Assert.Empty(OcrScanTiles.ForScreens([new Rect(0, 0, 1920, 1080), new Rect(1920, 0, 2048, 1160)], TwoScreens, 0, 0));
    }

    [Fact]
    public void Detect_ReadsEachScreensPart_AndPlacesItsBoxesInTheCapture()
    {
        var engine = new RecordingEngine(new SKRectI(10, 20, 60, 40));
        using var capture = TwoColours(300, 100, splitAt: 100);

        var boxes = OcrScanTiles.Detect(
            engine, capture, [new SKRectI(0, 0, 100, 100), new SKRectI(100, 0, 300, 100)], CancellationToken.None);

        // Each part is the screen's own pixels, not a copy of the capture's corner.
        Assert.Equal([(100, 100, SKColors.Red), (200, 100, SKColors.Blue)], engine.Seen);
        Assert.Equal([new SKRectI(10, 20, 60, 40), new SKRectI(110, 20, 160, 40)], boxes);
    }

    [Fact]
    public void Detect_WithoutParts_ReadsTheWholeCapture()
    {
        var engine = new RecordingEngine(new SKRectI(10, 20, 60, 40));
        using var capture = TwoColours(300, 100, splitAt: 100);

        var boxes = OcrScanTiles.Detect(engine, capture, [], CancellationToken.None);

        Assert.Equal([(300, 100, SKColors.Red)], engine.Seen);
        Assert.Equal([new SKRectI(10, 20, 60, 40)], boxes);
    }

    [Fact]
    public void Detect_StopsBeforeTheNextScreen_OnceCancelled()
    {
        using var cts = new CancellationTokenSource();
        var engine = new RecordingEngine(new SKRectI(10, 20, 60, 40), afterEach: cts.Cancel);
        using var capture = TwoColours(300, 100, splitAt: 100);

        Assert.Throws<OperationCanceledException>(() => OcrScanTiles.Detect(
            engine, capture, [new SKRectI(0, 0, 100, 100), new SKRectI(100, 0, 300, 100)], cts.Token));
        Assert.Single(engine.Seen);
    }

    private static SKBitmap TwoColours(int width, int height, int splitAt)
    {
        var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Blue);
        using var red = new SKPaint { Color = SKColors.Red };
        canvas.DrawRect(new SKRect(0, 0, splitAt, height), red);
        return bitmap;
    }

    // Returns one box for each bitmap it is handed, and keeps each one's size and top-left pixel.
    private sealed class RecordingEngine(SKRectI box, Action? afterEach = null) : IOCREngine
    {
        public List<(int Width, int Height, SKColor TopLeft)> Seen { get; } = [];

        public List<SKRectI> DetectText(SKBitmap bitmap)
        {
            Seen.Add((bitmap.Width, bitmap.Height, bitmap.GetPixel(0, 0)));
            afterEach?.Invoke();
            return [box];
        }

        public Task EnsureLoadedAsync(OCRLanguage lang, CancellationToken ct = default) => Task.CompletedTask;

        public Task EnsureDetectionLoadedAsync(OCRLanguage lang, CancellationToken ct = default) => Task.CompletedTask;

        public (string text, float confidence) RecognizeText(SKBitmap bitmap, SKRectI box, CancellationToken ct = default) => ("", 0f);

        public void Dispose()
        {
        }
    }
}
