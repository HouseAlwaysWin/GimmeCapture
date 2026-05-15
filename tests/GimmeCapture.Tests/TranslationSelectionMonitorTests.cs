using Avalonia;
using GimmeCapture.Models;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Translation;
using SkiaSharp;

namespace GimmeCapture.Tests;

public sealed class TranslationSelectionMonitorTests
{
    [Fact]
    public async Task ProcessAsync_WhenPixelsChange_ReturnsUpdatedSelection()
    {
        var captureService = new Mock<IScreenCaptureService>();
        var translationSession = new Mock<ITranslationSessionService>();
        using var bitmap = CreateBitmap(8, 8, new SKColor(255, 0, 0));
        var selection = new UserSelectionRect
        {
            Bounds = new Rect(0, 0, 80, 40)
        };

        captureService
            .Setup(service => service.CaptureScreenAsync(selection.Bounds, It.IsAny<PixelPoint>(), 1.0, false))
            .ReturnsAsync(bitmap.Copy());

        translationSession
            .Setup(service => service.EnsureOcrReadyAsync(OCRLanguage.Japanese, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        translationSession
            .Setup(service => service.AnalyzeAndTranslateAsync(
                It.IsAny<SKBitmap>(),
                1.0,
                OCRLanguage.Japanese,
                TranslationLanguage.English,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((
                new List<TranslatedBlock>
                {
                    new()
                    {
                        OriginalText = "こんにちは",
                        TranslatedText = "hello",
                        InferredFontSize = 18
                    }
                },
                string.Empty));

        var sut = new TranslationSelectionMonitor(captureService.Object, translationSession.Object);

        var updates = await sut.ProcessAsync(
            new TranslationSelectionMonitorRequest(
                new[] { selection },
                new PixelPoint(0, 0),
                1.0,
                OCRLanguage.Japanese,
                TranslationLanguage.English));

        var update = Assert.Single(updates);
        Assert.Same(selection, update.Selection);
        Assert.Equal(TranslationSelectionUpdateKind.Updated, update.Kind);
        Assert.Equal("こんにちは", update.OriginalText);
        Assert.Equal("hello", update.TranslatedText);
        Assert.Equal(18, update.InferredFontSize);
        Assert.NotNull(selection.LastPixels);
        Assert.Equal(8, selection.LastPixelWidth);
        Assert.Equal(8, selection.LastPixelHeight);
    }

    [Fact]
    public async Task ProcessAsync_WhenPixelsUnchanged_SkipsTranslation()
    {
        var captureService = new Mock<IScreenCaptureService>();
        var translationSession = new Mock<ITranslationSessionService>();
        using var bitmap = CreateBitmap(8, 8, new SKColor(0, 255, 0));
        var selection = new UserSelectionRect
        {
            Bounds = new Rect(0, 0, 80, 40),
            LastPixels = bitmap.Bytes.ToArray(),
            LastPixelWidth = 8,
            LastPixelHeight = 8,
            TranslatedText = "cached"
        };

        captureService
            .Setup(service => service.CaptureScreenAsync(selection.Bounds, It.IsAny<PixelPoint>(), 1.0, false))
            .ReturnsAsync(bitmap.Copy());

        translationSession
            .Setup(service => service.EnsureOcrReadyAsync(OCRLanguage.English, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var sut = new TranslationSelectionMonitor(captureService.Object, translationSession.Object);

        var updates = await sut.ProcessAsync(
            new TranslationSelectionMonitorRequest(
                new[] { selection },
                new PixelPoint(0, 0),
                1.0,
                OCRLanguage.English,
                TranslationLanguage.TraditionalChinese));

        Assert.Empty(updates);
        translationSession.Verify(
            service => service.AnalyzeAndTranslateAsync(
                It.IsAny<SKBitmap>(),
                It.IsAny<double>(),
                It.IsAny<OCRLanguage>(),
                It.IsAny<TranslationLanguage>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static SKBitmap CreateBitmap(int width, int height, SKColor color)
    {
        var bitmap = new SKBitmap(width, height, true);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(color);
        return bitmap;
    }
}
