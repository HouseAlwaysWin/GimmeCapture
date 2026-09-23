using System;
using System.IO;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.AI;
using GimmeCapture.Services.Translation;

namespace GimmeCapture.Tests;

/// <summary>
/// Lifetime rules for the translation engine that hold without a real GGUF model loaded.
/// </summary>
public sealed class LlamaSharpTranslationEngineLifetimeTests : IDisposable
{
    private readonly string _baseDir;
    private readonly LlamaSharpTranslationEngine _sut;

    public LlamaSharpTranslationEngineLifetimeTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_baseDir);

        var settingsService = new AppSettingsService(_baseDir);
        settingsService.Settings.AIResourcesDirectory = Path.Combine(_baseDir, "AI");
        var pathService = new AIPathService(settingsService);
        var aiResourceService = new AIResourceService(
            settingsService,
            pathService,
            new NativeResolverService(pathService),
            new AIModelDownloader());

        _sut = new LlamaSharpTranslationEngine(aiResourceService, settingsService, new InMemoryTranslationCache());
    }

    [Fact]
    public void DisposingTwiceIsSafe()
    {
        // Dispose used to dispose its semaphores, so a second Dispose — or an in-flight translation releasing its
        // lock after the overlay closed — threw ObjectDisposedException.
        _sut.Dispose();

        var failure = Record.Exception(() => _sut.Dispose());

        Assert.Null(failure);
    }

    [Fact]
    public async Task ADisposedEngineRefusesToTranslateInsteadOfReloadingTheModel()
    {
        // Nothing unloads a disposed engine's model, so letting it load one again would pin gigabytes for the rest
        // of the session.
        _sut.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => _sut.TranslateAsync("hello", OCRLanguage.Auto, TranslationLanguage.English));
    }

    public void Dispose()
    {
        _sut.Dispose();
        try { Directory.Delete(_baseDir, recursive: true); } catch { /* best effort temp cleanup */ }
    }
}
