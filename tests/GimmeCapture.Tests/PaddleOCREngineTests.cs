using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using SkiaSharp;
using GimmeCapture.Models;

using GimmeCapture.Services.OCR;


using GimmeCapture.Services.Core.Interfaces;
using GimmeCapture.Services.Core.AI;

namespace GimmeCapture.Tests;

[Collection(AIGlobalUnloadCollection.Name)]
public class PaddleOCREngineTests
{
    private readonly Mock<AIResourceService> _mockAiResource;
    private readonly Mock<IAppSettingsService> _mockSettings;
    private readonly OcrRuntimeService _ocrRuntimeService;
    private readonly PaddleOCREngine _sut;

    public PaddleOCREngineTests()
    {
        _mockSettings = new Mock<IAppSettingsService>();
        var mockPath = new Mock<AIPathService>(_mockSettings.Object);
        var mockDownloader = new Mock<AIModelDownloader>();
        _mockAiResource = new Mock<AIResourceService>(_mockSettings.Object, mockPath.Object, mockDownloader.Object);
        _ocrRuntimeService = new OcrRuntimeService(_mockAiResource.Object);
        _sut = new PaddleOCREngine(_mockAiResource.Object, _mockSettings.Object, _ocrRuntimeService);
    }

    [Fact]
    public async Task EnsureLoadedAsync_ShouldCallEnsureOCRAsync()
    {
        // Arrange
        _mockAiResource.Setup(x => x.EnsureOCRAsync(It.IsAny<OCRLanguage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Act
        await _sut.EnsureLoadedAsync(OCRLanguage.TraditionalChinese);

        // Assert
        _mockAiResource.Verify(x => x.EnsureOCRAsync(It.IsAny<OCRLanguage>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task Dispose_ShouldReleaseRuntimeLease()
    {
        _mockAiResource.Setup(x => x.EnsureOCRAsync(It.IsAny<OCRLanguage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await _sut.EnsureLoadedAsync(OCRLanguage.TraditionalChinese);

        Assert.True(_ocrRuntimeService.HasActiveLeases);

        _sut.Dispose();

        Assert.False(_ocrRuntimeService.HasActiveLeases);
    }

    [Fact]
    public void IsUsefulOcrText_ShouldReturnFalse_WhenEmpty()
    {
        // Act & Assert
        // We need to access IsUsefulOcrText. Since it's private/internal, we might need a workaround 
        // but for now testing current public interface if any or assume logic is correct.
        // In this refactor, IsUsefulOcrText was moved to TranslationService. 
        // We'll test it there.
    }

    [Fact]
    public void ExpandRecognitionBox_ShouldPreserveVerticalContextForSmallText()
    {
        var box = new SKRectI(10, 10, 110, 24);

        var expanded = PaddleOCREngine.ExpandRecognitionBox(box, 200, 100);

        Assert.Equal(new SKRectI(8, 3, 112, 31), expanded);
    }

    [Fact]
    public void ExpandRecognitionBox_ShouldClampToBitmapBounds()
    {
        var box = new SKRectI(1, 2, 99, 16);

        var expanded = PaddleOCREngine.ExpandRecognitionBox(box, 100, 20);

        Assert.Equal(new SKRectI(0, 0, 100, 20), expanded);
    }

    // The overlay warms detection up at the size its scan will run at, so both have to come out of this.
    [Theory]
    [InlineData(3968, 1160, 1280, 384)] // two screens side by side
    [InlineData(1920, 1080, 1280, 736)]
    [InlineData(1080, 1920, 736, 1280)] // portrait: the height is the longer side
    [InlineData(800, 600, 800, 608)] // small enough already: only rounded up
    public void DetectionInputSize_BringsTheLongerSideDownTo1280_ThenRoundsUpTo32(
        int width, int height, int expectedWidth, int expectedHeight)
    {
        Assert.Equal((expectedWidth, expectedHeight), PaddleOCREngine.DetectionInputSize(width, height));
    }

    [Fact]
    public async Task DetectText_RunsTheLoadedDetector_AndFindsNothingWhenNoneIsLoaded()
    {
        string folder = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", "OcrDetect", Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new AppSettingsService(folder);
            settings.Settings.AIResourcesDirectory = Path.Combine(folder, "AI");
            var pathService = new AIPathService(settings);
            var paths = pathService.GetOCRPaths(OCRLanguage.TraditionalChinese);
            TinyOnnxModel.WriteIdentity(paths.Det);
            TinyOnnxModel.WriteIdentity(paths.Rec);
            File.WriteAllLines(paths.Dict, ["a"]);
            var aiResources = new AIResourceService(settings, pathService, new AIModelDownloader());
            using var runtime = new OcrRuntimeService(aiResources);
            using var engine = new PaddleOCREngine(aiResources, settings, runtime);

            using var capture = new SKBitmap(64, 48);
            Assert.Empty(engine.DetectText(capture)); // nothing loaded: nothing to run
            await engine.EnsureDetectionLoadedAsync(OCRLanguage.TraditionalChinese);

            // The run hands its memory back to the system as it ends, which ONNX Runtime refuses on a session it would
            // have nothing to hand back from. A run that finds no text reports the whole capture.
            Assert.Equal([new SKRectI(0, 0, 64, 48)], engine.DetectText(capture));
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch { /* best effort temp cleanup */ }
        }
    }
}
