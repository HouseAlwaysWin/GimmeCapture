using GimmeCapture.Models;
using System.Threading;
using System.Threading.Tasks;

namespace GimmeCapture.Tests;

public class MarianMTServiceTests
{
    private readonly Mock<AIResourceService> _mockAiResourceService;
    private readonly MarianMTService _sut;

    public MarianMTServiceTests()
    {
        // We need dummy services for AIResourceService
        var mockSettings = new Mock<AppSettingsService>();
        var mockPath = new Mock<AIPathService>(mockSettings.Object);
        var mockResolver = new Mock<NativeResolverService>(mockPath.Object);
        var mockDownloader = new Mock<AIModelDownloader>();
        _mockAiResourceService = new Mock<AIResourceService>(mockSettings.Object, mockPath.Object, mockResolver.Object, mockDownloader.Object);
        _sut = new MarianMTService(_mockAiResourceService.Object);
    }

    [Fact]
    public void Constructor_ShouldInitializeCorrectly()
    {
        Assert.NotNull(_sut);
    }

    [Fact]
    public async Task EnsureLoadedAsync_ShouldCallEnsureNmtAsync()
    {
        // Arrange
        _mockAiResourceService.Setup(x => x.EnsureNmtAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _mockAiResourceService.Setup(x => x.GetNmtPaths())
            .Returns(("enc", "dec", "tok", "spm", "cfg", "gen"));

        // Act & Assert
        // Since files don't exist, it should throw FileNotFoundException, but but it must have called EnsureNmtAsync
        await Assert.ThrowsAsync<System.IO.FileNotFoundException>(() => _sut.EnsureLoadedAsync());
        
        _mockAiResourceService.Verify(x => x.EnsureNmtAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TranslateAsync_ShouldReturnOriginalText_WhenNotLoaded()
    {
        // Arrange
        var text = "こんにちは";
        
        // Act
        var result = await _sut.TranslateAsync(text, TranslationLanguage.TraditionalChinese);

        // Assert
        Assert.Equal(text, result);
    }
}
