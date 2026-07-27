using GimmeCapture.Models;
using GimmeCapture.Services.Core.AI;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Interfaces;
using GimmeCapture.Services.OCR;
using GimmeCapture.Services.Translation;
using SkiaSharp;
using System.Reflection;

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
        var ocrRuntimeService = new OcrRuntimeService(aiResourceService);

        _sut = new TranslationSessionService(
            aiResourceService,
            _settingsService,
            ocrRuntimeService,
            new OcrScriptDetector(aiResourceService));
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

    [Fact]
    public void CancelWarmup_ReleasesOcrResources()
    {
        var translationServiceField = typeof(TranslationSessionService)
            .GetField("_translationService", BindingFlags.Instance | BindingFlags.NonPublic);
        var translationService = Assert.IsType<TranslationService>(translationServiceField?.GetValue(_sut));

        var ocrEngineField = typeof(TranslationService)
            .GetField("_ocrEngine", BindingFlags.Instance | BindingFlags.NonPublic);
        var fakeEngine = new FakeOcrEngine();
        ocrEngineField?.SetValue(translationService, fakeEngine);

        _sut.CancelWarmup();

        Assert.True(fakeEngine.IsDisposed);
        Assert.Null(ocrEngineField?.GetValue(translationService));
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

    private sealed class FakeOcrEngine : IOCREngine
    {
        public bool IsDisposed { get; private set; }

        public Task EnsureLoadedAsync(OCRLanguage lang, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public List<SKRectI> DetectText(SKBitmap bitmap)
        {
            return new List<SKRectI>();
        }

        public (string text, float confidence) RecognizeText(SKBitmap bitmap, SKRectI box, CancellationToken ct = default)
        {
            return (string.Empty, 0f);
        }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }
}
