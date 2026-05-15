using GimmeCapture.Models;
using GimmeCapture.Services.Core.AI;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Translation;

namespace GimmeCapture.Tests;

public sealed class TranslationSessionServiceTests : IDisposable
{
    private readonly string _baseDir;
    private readonly AppSettingsService _settingsService;
    private readonly TranslationSessionService _sut;

    public TranslationSessionServiceTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_baseDir);

        _settingsService = new AppSettingsService(_baseDir);
        _settingsService.Settings.AIResourcesDirectory = Path.Combine(_baseDir, "AI");

        var pathService = new AIPathService(_settingsService);
        var resolver = new NativeResolverService(pathService);
        var downloader = new AIModelDownloader();
        var aiResourceService = new AIResourceService(_settingsService, pathService, resolver, downloader);

        _sut = new TranslationSessionService(aiResourceService, _settingsService);
    }

    [Fact]
    public async Task CheckEngineReadyAsync_WhenNoLlamaModel_ReturnsReminderWithoutDownloadPrompt()
    {
        var result = await _sut.CheckEngineReadyAsync(OCRLanguage.Japanese, TranslationLanguage.English);

        Assert.False(result.IsReady);
        Assert.Equal("StatusLlamaModelNotReady", result.ErrorKey);
        Assert.False(result.ShowDownloadPrompt);
        Assert.Equal(OCRLanguage.Japanese, _settingsService.Settings.SourceLanguage);
        Assert.Equal(TranslationLanguage.English, _settingsService.Settings.TargetLanguage);
    }

    [Fact]
    public async Task AwaitWarmupAsync_WhenWarmupHasNotStarted_Completes()
    {
        await _sut.AwaitWarmupAsync();
    }

    public void Dispose()
    {
        _sut.Dispose();

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
}
