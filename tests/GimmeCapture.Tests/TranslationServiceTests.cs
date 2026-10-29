using GimmeCapture.Models;
using GimmeCapture.Services.Core.Interfaces;
using GimmeCapture.Services.OCR;
using SkiaSharp;

namespace GimmeCapture.Tests;

public sealed class TranslationServiceTests : IDisposable
{
    private const string Hello = "Hello world";
    private const string Morning = "Good morning";
    private const string Merged = Hello + "\n" + Morning;
    private const string HelloInChinese = "\u4F60\u597D\u4E16\u754C";
    private const string MorningInChinese = "\u65E9\u5B89";

    private readonly string _baseDir;
    private readonly AppSettingsService _settingsService;
    private readonly Mock<AIResourceService> _aiResources;
    private readonly OcrRuntimeService _ocrRuntimeService;
    private readonly FakeTranslationModelEngine _engine = new();
    private readonly FakeOcrEngine _ocr = new(
        (new SKRectI(10, 10, 150, 30), Hello),
        (new SKRectI(10, 50, 150, 70), Morning));

    public TranslationServiceTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_baseDir);

        _settingsService = new AppSettingsService(_baseDir);
        _settingsService.Settings.AIResourcesDirectory = Path.Combine(_baseDir, "AI");
        _settingsService.Settings.SourceLanguage = OCRLanguage.English;
        _settingsService.Settings.TargetLanguage = TranslationLanguage.TraditionalChinese;

        // Real paths and readiness checks; the downloads OCR's warm-up would start report "not installed" instead.
        _aiResources = new Mock<AIResourceService>(_settingsService, new AIPathService(_settingsService), new AIModelDownloader());
        _aiResources
            .Setup(service => service.EnsureOCRAsync(It.IsAny<OCRLanguage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _ocrRuntimeService = new OcrRuntimeService(_aiResources.Object);
    }

    [Fact]
    public async Task ARejectedTranslationGoesStraightToThePerBlockFallback()
    {
        // The merged text comes back untranslated, which is rejected; each line on its own translates. The rejected
        // text used to be asked for a second time first, with the same text and languages: greedy or fixed-seed
        // sampling gave the same answer back, after a whole generation's wait.
        _engine.Answer = text => text switch
        {
            Hello => HelloInChinese,
            Morning => MorningInChinese,
            _ => text,
        };
        using var sut = CreateService();
        using var bitmap = new SKBitmap(200, 100);

        var (blocks, errorKey) = await sut.AnalyzeAndTranslateAsync(bitmap);

        Assert.Equal(new[] { Merged, Hello, Morning }, _engine.Requests);
        Assert.Equal(new[] { HelloInChinese, MorningInChinese }, blocks.Select(block => block.TranslatedText));
        Assert.Equal(string.Empty, errorKey);
    }

    [Fact]
    public async Task AnAcceptedTranslationIsAskedForOnce()
    {
        _engine.Answer = text => text == Merged ? HelloInChinese + "\n" + MorningInChinese : text;
        using var sut = CreateService();
        using var bitmap = new SKBitmap(200, 100);

        var (blocks, errorKey) = await sut.AnalyzeAndTranslateAsync(bitmap);

        Assert.Equal(new[] { Merged }, _engine.Requests);
        Assert.Single(blocks);
        Assert.Equal(string.Empty, errorKey);
    }

    [Fact]
    public async Task WarmUpAsync_StartsLoadingTheTranslationModel()
    {
        // Alongside OCR, rather than once OCR has read the text, so a first press does not wait for the two in turn.
        InstallSelectedModel();
        using var sut = CreateService();

        await sut.WarmUpAsync();

        Assert.Equal(1, _engine.PreloadCount);
    }

    [Fact]
    public async Task WarmUpAsync_WithNoModelInstalled_LoadsNone()
    {
        using var sut = CreateService();

        await sut.WarmUpAsync();

        Assert.Equal(0, _engine.PreloadCount);
    }

    [Fact]
    public void Dispose_LeavesTheSharedEngineAlone()
    {
        var sut = CreateService();

        sut.Dispose();

        Assert.Equal(0, _engine.DisposeCount);
        Assert.Equal(0, _engine.ReleaseCount);
    }

    private TranslationService CreateService() => new(
        _aiResources.Object,
        _settingsService,
        _ocrRuntimeService,
        Mock.Of<IOcrScriptDetector>(),
        _engine,
        new FakeOcrEngineFactory(_ocr));

    // An empty file where the selected model belongs: enough for the readiness check; the fake engine reads nothing.
    private void InstallSelectedModel()
    {
        string modelPath = _aiResources.Object.GetLlamaModelPathById(_settingsService.Settings.LlamaModelId);
        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
        File.WriteAllBytes(modelPath, Array.Empty<byte>());
    }

    public void Dispose()
    {
        _ocrRuntimeService.Dispose();
        try { Directory.Delete(_baseDir, recursive: true); } catch { /* best effort temp cleanup */ }
    }

    private sealed class FakeOcrEngineFactory : IOcrEngineFactory
    {
        private readonly IOCREngine _engine;

        public FakeOcrEngineFactory(IOCREngine engine) => _engine = engine;

        public IOCREngine Create(
            AIResourceService aiResourceService,
            IAppSettingsService settingsService,
            OcrRuntimeService ocrRuntimeService) => _engine;
    }

    /// <summary>Finds the given boxes and reads each one's text with high confidence.</summary>
    private sealed class FakeOcrEngine : IOCREngine
    {
        private readonly (SKRectI Box, string Text)[] _lines;

        public FakeOcrEngine(params (SKRectI Box, string Text)[] lines) => _lines = lines;

        public Task EnsureLoadedAsync(OCRLanguage lang, CancellationToken ct = default) => Task.CompletedTask;

        public List<SKRectI> DetectText(SKBitmap bitmap) => _lines.Select(line => line.Box).ToList();

        public (string text, float confidence) RecognizeText(SKBitmap bitmap, SKRectI box, CancellationToken ct = default) =>
            (_lines.Single(line => line.Box == box).Text, 0.95f);

        public void Dispose()
        {
        }
    }
}
