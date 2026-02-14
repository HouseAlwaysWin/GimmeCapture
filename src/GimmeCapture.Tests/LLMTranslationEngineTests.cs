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
    private readonly Mock<HttpMessageHandler> _handlerMock;
    private readonly Mock<AppSettingsService> _settingsServiceMock;
    private readonly LLMTranslationEngine _sut;
    private readonly AppSettings _settings;

    public LLMTranslationEngineTests()
    {
        _handlerMock = new Mock<HttpMessageHandler>();
        
        // Now works because we added protected parameterless constructor
        _settingsServiceMock = new Mock<AppSettingsService>();
        
        _settings = new AppSettings
        {
            OllamaModel = "gemma",
            OllamaApiUrl = "http://localhost:11434/api/generate"
        };
        
        _settingsServiceMock.Setup(s => s.Settings).Returns(_settings);

        var httpClient = new HttpClient(_handlerMock.Object);
        _sut = new LLMTranslationEngine(httpClient, _settingsServiceMock.Object);
    }

    [Fact]
    public async Task TranslateAsync_ShouldSendCorrectPrompt()
    {
        // Arrange
        var text = "Hello";
        var source = OCRLanguage.English;
        var target = TranslationLanguage.TraditionalChinese;

        _handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = System.Net.HttpStatusCode.OK,
                Content = new StringContent(JsonSerializer.Serialize(new { response = "你好" }))
            });

        // Act
        var result = await _sut.TranslateAsync(text, source, target);

        // Assert
        Assert.Equal("你好", result);
        _handlerMock.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req => 
                req.Content != null && 
                req.Content.ReadAsStringAsync().GetAwaiter().GetResult().Contains("expert translator")
            ),
            ItExpr.IsAny<CancellationToken>()
        );
    }

    [Fact]
    public async Task TranslateAsync_ShouldCleanupResultPrefixes()
    {
        // Arrange
        var text = "Hello";
        _handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = System.Net.HttpStatusCode.OK,
                Content = new StringContent(JsonSerializer.Serialize(new { response = "Translation: 你好" }))
            });

        // Act
        var result = await _sut.TranslateAsync(text, OCRLanguage.English, TranslationLanguage.TraditionalChinese);

        // Assert
        Assert.Equal("你好", result);
    }

    [Fact]
    public async Task TranslateAsync_ShouldReturnOriginal_WhenRequestFails()
    {
        // Arrange
        var text = "Hello";
        _handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = System.Net.HttpStatusCode.InternalServerError
            });

        // Act
        var result = await _sut.TranslateAsync(text, OCRLanguage.English, TranslationLanguage.TraditionalChinese);

        // Assert
        Assert.Equal(text, result);
    }
}
