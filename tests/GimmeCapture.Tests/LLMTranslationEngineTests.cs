using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Moq.Protected;
using GimmeCapture.Models;

using GimmeCapture.Services.Translation;


namespace GimmeCapture.Tests;

public class LLMTranslationEngineTests
{
    private readonly Mock<IOllamaApiClient> _ollamaMock;
    private readonly Mock<AppSettingsService> _settingsServiceMock;
    private readonly Mock<ITranslationCache> _cacheMock;
    private readonly LLMTranslationEngine _sut;
    private readonly AppSettings _settings;

    public LLMTranslationEngineTests()
    {
        _ollamaMock = new Mock<IOllamaApiClient>();
        _settingsServiceMock = new Mock<AppSettingsService>();
        _cacheMock = new Mock<ITranslationCache>();
        
        _settings = new AppSettings
        {
            OllamaModel = "gemma",
            OllamaApiUrl = "http://localhost:11434/api/generate"
        };
        
        _settingsServiceMock.Setup(s => s.Settings).Returns(_settings);
        
        _sut = new LLMTranslationEngine(_ollamaMock.Object, _settingsServiceMock.Object, _cacheMock.Object);
    }

    [Fact]
    public async Task TranslateAsync_ShouldSendCorrectPrompt()
    {
        // Arrange
        var text = "Hello";
        var source = OCRLanguage.English;
        var target = TranslationLanguage.TraditionalChinese;

        _ollamaMock.Setup(x => x.GenerateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(JsonSerializer.Serialize(new { response = "你好" }));

        // Act
        var result = await _sut.TranslateAsync(text, source, target);

        // Assert
        Assert.Equal("你好", result);
        _ollamaMock.Verify(x => x.GenerateAsync(_settings.OllamaModel, It.Is<string>(p => p.Contains("Translate")), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TranslateAsync_ShouldCleanupResultPrefixes()
    {
        // Arrange
        var text = "Hello";
        _ollamaMock.Setup(x => x.GenerateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(JsonSerializer.Serialize(new { response = "Translation: 你好" }));

        // Act
        var result = await _sut.TranslateAsync(text, OCRLanguage.English, TranslationLanguage.TraditionalChinese);

        // Assert
        Assert.Equal("你好", result);
    }

    [Fact]
    public async Task TranslateAsync_ShouldReturnEmpty_WhenRequestFails()
    {
        // Arrange
        var text = "Hello";
        _ollamaMock.Setup(x => x.GenerateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(string.Empty);

        // Act
        var result = await _sut.TranslateAsync(text, OCRLanguage.English, TranslationLanguage.TraditionalChinese);

        // Assert
        Assert.Equal(string.Empty, result);
    }
}
