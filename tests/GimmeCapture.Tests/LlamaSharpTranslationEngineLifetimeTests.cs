using System;
using System.IO;
using System.Reflection;
using System.Threading;
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
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(150);

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

    [Fact]
    public void TheModelOutlivesIdleUseByTwoMinutes_AndIsGoneBeforeAnIdleTrim()
    {
        // The end of a translate press no longer frees the model, so this countdown is what does. Every use of the
        // model counts as activity for the idle trim, so with the shorter delay the model is always unloaded before a
        // trim could push its weights out of memory.
        Assert.Equal(TimeSpan.FromMinutes(2), LlamaSharpTranslationEngine.UnloadIdleDelay);
        Assert.True(LlamaSharpTranslationEngine.UnloadIdleDelay < ProcessMemoryTrimService.IdleTrimDelay);
    }

    [Fact]
    public void ReleasingWithNoModelLoaded_IsSafe()
    {
        _sut.ReleaseModel();

        Assert.True(_sut.UnloadModel(TimeSpan.Zero));
        Assert.False(_sut.IsModelLoaded);
    }

    [Fact]
    public async Task UnloadModel_WaitsForARunningTranslation()
    {
        // A module removal deletes the GGUF file right after this, which fails while the model is still mapped.
        var inferenceLock = InferenceLock();
        inferenceLock.Wait();

        var unload = OwnThread.Run(() => _sut.UnloadModel(Timeout));
        var finished = await Task.WhenAny(unload, OwnThread.Delay(Settle));
        Assert.False(finished == unload, "the unload went ahead while a translation was generating");

        inferenceLock.Release();
        Assert.True(await unload.WaitAsync(Timeout));
    }

    [Fact]
    public void UnloadModel_GivesUpOnceItsTimeoutRunsOut()
    {
        var inferenceLock = InferenceLock();
        inferenceLock.Wait();
        try
        {
            Assert.False(_sut.UnloadModel(TimeSpan.FromMilliseconds(50)));
        }
        finally
        {
            inferenceLock.Release();
        }
    }

    [Fact]
    public async Task ReleaseModel_NeverWaitsForARunningTranslation()
    {
        // Turning AI off calls this on the UI thread.
        var inferenceLock = InferenceLock();
        inferenceLock.Wait();
        try
        {
            var release = OwnThread.Run(() =>
            {
                _sut.ReleaseModel();
                return true;
            });

            Assert.True(await release.WaitAsync(Timeout));
        }
        finally
        {
            inferenceLock.Release();
        }
    }

    [Fact]
    public async Task PreloadAsync_WithNoModelInstalled_ReportsIt()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.PreloadAsync());
    }

    [Fact]
    public async Task ADisposedEngineDoesNotPreload()
    {
        _sut.Dispose();

        var failure = await Record.ExceptionAsync(() => _sut.PreloadAsync());

        Assert.Null(failure);
        Assert.False(_sut.IsModelLoaded);
    }

    // Held by every load and generation: holding it stands in for a translation in progress.
    private SemaphoreSlim InferenceLock() => Assert.IsType<SemaphoreSlim>(
        typeof(LlamaSharpTranslationEngine)
            .GetField("_inferLock", BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(_sut));

    public void Dispose()
    {
        _sut.Dispose();
        try { Directory.Delete(_baseDir, recursive: true); } catch { /* best effort temp cleanup */ }
    }
}
