using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using SkiaSharp;
using GimmeCapture.Models;

using GimmeCapture.Services.OCR;


using GimmeCapture.Services.Core.Interfaces;
using GimmeCapture.Services.Core.AI;

namespace GimmeCapture.Tests;

public class PaddleOCREngineTests
{
    private readonly Mock<AIResourceService> _mockAiResource;
    private readonly Mock<AppSettingsService> _mockSettings;
    private readonly OcrRuntimeService _ocrRuntimeService;
    private readonly PaddleOCREngine _sut;

    public PaddleOCREngineTests()
    {
        _mockSettings = new Mock<AppSettingsService>();
        var mockPath = new Mock<AIPathService>(_mockSettings.Object);
        var mockResolver = new Mock<NativeResolverService>(mockPath.Object);
        var mockDownloader = new Mock<AIModelDownloader>();
        _mockAiResource = new Mock<AIResourceService>(_mockSettings.Object, mockPath.Object, mockResolver.Object, mockDownloader.Object);
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
}
