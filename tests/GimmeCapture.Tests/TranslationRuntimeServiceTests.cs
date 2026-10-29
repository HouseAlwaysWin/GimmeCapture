using GimmeCapture.Models;
using GimmeCapture.Services.OCR;

namespace GimmeCapture.Tests;

/// <summary>
/// The translation engine and cache the app keeps for the whole run, shared by every capture overlay.
/// </summary>
public sealed class TranslationRuntimeServiceTests : IDisposable
{
    private readonly string _baseDir;
    private readonly AppSettingsService _settingsService;
    private readonly AIResourceService _aiResourceService;
    private readonly OcrRuntimeService _ocrRuntimeService;
    private readonly InMemoryTranslationCache _cache = new();
    private readonly List<(FakeTranslationModelEngine Engine, ITranslationCache Cache)> _built = new();
    private readonly TranslationRuntimeService _sut;

    public TranslationRuntimeServiceTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_baseDir);

        _settingsService = new AppSettingsService(_baseDir);
        _settingsService.Settings.AIResourcesDirectory = Path.Combine(_baseDir, "AI");
        _aiResourceService = new AIResourceService(_settingsService, new AIPathService(_settingsService), new AIModelDownloader());
        _ocrRuntimeService = new OcrRuntimeService(_aiResourceService);

        _sut = new TranslationRuntimeService(
            (_, _, cache) =>
            {
                var engine = new FakeTranslationModelEngine();
                _built.Add((engine, cache));
                return engine;
            },
            _cache);
    }

    [Fact]
    public void EveryOverlaysSessionTranslatesThroughOneEngineAndCache()
    {
        // Each overlay used to build an engine and cache of its own, so the next overlay loaded the model again and
        // found none of the translations already made.
        var factory = new TranslationSessionServiceFactory(_sut);

        using var first = factory.Create(_settingsService, _aiResourceService, _ocrRuntimeService, new OcrScriptDetector(_aiResourceService));
        using var second = factory.Create(_settingsService, _aiResourceService, _ocrRuntimeService, new OcrScriptDetector(_aiResourceService));

        var (engine, cache) = Assert.Single(_built);
        Assert.Same(_cache, cache);
        Assert.Same(engine, _sut.GetEngine(_aiResourceService, _settingsService));
    }

    [Fact]
    public void ClosingAnOverlay_LeavesTheEngineForTheNextOne()
    {
        var factory = new TranslationSessionServiceFactory(_sut);
        var session = factory.Create(_settingsService, _aiResourceService, _ocrRuntimeService, new OcrScriptDetector(_aiResourceService));

        session.Dispose();

        var engine = Assert.Single(_built).Engine;
        Assert.Equal(0, engine.DisposeCount);
        Assert.Equal(0, engine.ReleaseCount);
    }

    [Fact]
    public void ReleaseModel_FreesTheEnginesModel()
    {
        // What turning AI off asks for.
        var engine = (FakeTranslationModelEngine)_sut.GetEngine(_aiResourceService, _settingsService);

        _sut.ReleaseModel();

        Assert.Equal(1, engine.ReleaseCount);
    }

    [Fact]
    public void ReleaseModel_BeforeAnyOverlay_DoesNothing()
    {
        _sut.ReleaseModel();

        Assert.Empty(_built);
    }

    [Fact]
    public void RemovingAModule_UnloadsTheModelFirst()
    {
        // A loaded GGUF file is mapped into memory, and Windows will not delete a mapped file.
        var engine = (FakeTranslationModelEngine)_sut.GetEngine(_aiResourceService, _settingsService);

        _aiResourceService.Orchestrator.RequestGlobalUnload();

        // At least once: another test's module removal raises the same app-wide request.
        Assert.True(engine.UnloadCount >= 1);
        Assert.Equal(TimeSpan.FromSeconds(30), engine.LastUnloadTimeout);
    }

    [Fact]
    public void Dispose_DisposesTheEngine_AndStopsListeningForModuleRemoval()
    {
        // At exit, the model outlives every overlay, so nothing else would free it.
        var engine = (FakeTranslationModelEngine)_sut.GetEngine(_aiResourceService, _settingsService);

        _sut.Dispose();
        int unloadsBefore = engine.UnloadCount;
        _aiResourceService.Orchestrator.RequestGlobalUnload();

        Assert.Equal(1, engine.DisposeCount);
        Assert.Equal(unloadsBefore, engine.UnloadCount);
    }

    [Fact]
    public void Dispose_Twice_DisposesTheEngineOnce()
    {
        var engine = (FakeTranslationModelEngine)_sut.GetEngine(_aiResourceService, _settingsService);

        _sut.Dispose();
        _sut.Dispose();

        Assert.Equal(1, engine.DisposeCount);
    }

    [Fact]
    public void AfterDispose_NoEngineIsBuilt()
    {
        // Past exit nothing would ever free a model a new engine loaded.
        _sut.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _sut.GetEngine(_aiResourceService, _settingsService));
        Assert.Empty(_built);
    }

    [Fact]
    public async Task TheRealEngine_OutlivesAnOverlay()
    {
        // With no model installed, a live engine reports the missing model; a disposed one would refuse outright.
        using var runtime = new TranslationRuntimeService();
        var factory = new TranslationSessionServiceFactory(runtime);
        var engine = runtime.GetEngine(_aiResourceService, _settingsService);
        factory.Create(_settingsService, _aiResourceService, _ocrRuntimeService, new OcrScriptDetector(_aiResourceService))
            .Dispose();

        Assert.Same(engine, runtime.GetEngine(_aiResourceService, _settingsService));
        Assert.False(engine.IsModelLoaded);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.TranslateAsync("hello", OCRLanguage.English, TranslationLanguage.Japanese));
    }

    public void Dispose()
    {
        _sut.Dispose();
        _ocrRuntimeService.Dispose();
        try { Directory.Delete(_baseDir, recursive: true); } catch { /* best effort temp cleanup */ }
    }
}
