using System;
using System.IO;
using System.Threading.Tasks;
using GimmeCapture.Services.Core.AI;

namespace GimmeCapture.Tests;

/// <summary>
/// Session lifetime rules for the shared OCR runtime. These do not need real models loaded — the point is the
/// gating around the sessions, which applies whether or not any are present.
/// </summary>
public sealed class OcrRuntimeServiceTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(150);

    private readonly string _baseDir;
    private readonly OcrRuntimeService _sut;

    public OcrRuntimeServiceTests()
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

        _sut = new OcrRuntimeService(aiResourceService);
    }

    [Fact]
    public async Task BeginSessionUse_SerialisesInference()
    {
        // Two threads calling Run on one ONNX session killed the process with an access violation. The app
        // produces overlapping callers routinely: DetectText cannot be cancelled once started, so cancelling a
        // scan (Esc) leaves its inference running while the replacement scan begins.
        var first = _sut.BeginSessionUse();

        var second = Task.Run(() =>
        {
            using var use = _sut.BeginSessionUse();
            return true;
        });

        Assert.False(second.Wait(Settle), "a second inference started while the first was still running");

        first.Dispose();

        Assert.True(await second.WaitAsync(Timeout));
    }

    [Fact]
    public async Task DisposingASessionUseTwice_DoesNotAdmitTwoInferences()
    {
        var use = _sut.BeginSessionUse();
        use.Dispose();
        use.Dispose();

        // A double release would have raised the semaphore's count, letting two callers in at once from then on.
        var first = _sut.BeginSessionUse();
        var second = Task.Run(() =>
        {
            using var scope = _sut.BeginSessionUse();
            return true;
        });

        Assert.False(second.Wait(Settle));

        first.Dispose();
        Assert.True(await second.WaitAsync(Timeout));
    }

    [Fact]
    public void BeginSessionUse_WithNothingLoaded_ReportsNoSessions()
    {
        using var use = _sut.BeginSessionUse();

        Assert.Null(use.Detection);
        Assert.Null(use.Recognition);
        Assert.Empty(use.Dictionary);
    }

    [Fact]
    public void ReleasingTheLastLease_DoesNotUnloadImmediately()
    {
        // The unload is deferred so a follow-up capture reuses the live sessions instead of forcing a
        // teardown-and-rebuild, which is the sequence that preceded every observed crash.
        var lease = _sut.AcquireLease();
        Assert.True(_sut.HasActiveLeases);

        _sut.ReleaseLease(lease);

        Assert.False(_sut.HasActiveLeases);
    }

    public void Dispose()
    {
        _sut.Dispose();
        try { Directory.Delete(_baseDir, recursive: true); } catch { /* best effort temp cleanup */ }
    }
}
