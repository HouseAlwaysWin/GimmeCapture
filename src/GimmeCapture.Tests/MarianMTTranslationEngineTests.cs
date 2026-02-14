using System.Threading;
using System.Threading.Tasks;
using Moq;
using GimmeCapture.Models;
using GimmeCapture.Services.Core;
using GimmeCapture.Services.Translation;
using Xunit;

namespace GimmeCapture.Tests;

public class MarianMTTranslationEngineTests
{
    private readonly Mock<MarianMTService> _mockService;
    private readonly MarianMTTranslationEngine _sut;

    public MarianMTTranslationEngineTests()
    {
        var mockSettings = new Mock<AppSettingsService>();
        var mockPath = new Mock<AIPathService>(mockSettings.Object);
        var mockResolver = new Mock<NativeResolverService>(mockPath.Object);
        var mockDownloader = new Mock<AIModelDownloader>();
        var mockAiResource = new Mock<AIResourceService>(mockSettings.Object, mockPath.Object, mockResolver.Object, mockDownloader.Object);
        _mockService = new Mock<MarianMTService>(mockAiResource.Object);
        _sut = new MarianMTTranslationEngine(_mockService.Object);
    }

    [Fact]
    public async Task TranslateAsync_ShouldDelegateToService()
    {
        // Arrange
        var text = "Hello";
        var source = OCRLanguage.English;
        var target = TranslationLanguage.TraditionalChinese;
        var expected = "你好";

        // IMPORTANT: The return type must match string? because we made TranslateAsync virtual with string? return
        _mockService.Setup(s => s.TranslateAsync(It.IsAny<string>(), It.IsAny<TranslationLanguage>(), It.IsAny<OCRLanguage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)expected);

        // Act
        var result = await _sut.TranslateAsync(text, source, target);

        // Assert
        Assert.Equal(expected, result);
        _mockService.Verify(s => s.TranslateAsync(text, target, source, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TranslateAsync_ShouldHandleNullResult()
    {
        // Arrange
        var text = "Hello";
        _mockService.Setup(s => s.TranslateAsync(It.IsAny<string>(), It.IsAny<TranslationLanguage>(), It.IsAny<OCRLanguage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        // Act
        var result = await _sut.TranslateAsync(text, OCRLanguage.English, TranslationLanguage.English);

        // Assert
        Assert.Equal(text, result);
    }
}
