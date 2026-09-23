using GimmeCapture.Models;

namespace GimmeCapture.Tests;

public sealed class SAM2RuntimeServiceTests : IDisposable
{
    private readonly string _baseDir;

    public SAM2RuntimeServiceTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", nameof(SAM2RuntimeServiceTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_baseDir);
    }

    [Fact]
    public async Task LoadModelsAsync_WhenModelFilesMissing_LeavesSessionsEmpty()
    {
        var settingsService = new AppSettingsService(_baseDir);
        settingsService.Settings.AIResourcesDirectory = Path.Combine(_baseDir, "AI");
        var pathService = new AIPathService(settingsService);
        var resolverService = new NativeResolverService(pathService);
        using var sut = new SAM2RuntimeService(pathService, resolverService);

        await sut.LoadModelsAsync(SAM2Variant.Tiny);

        var sessions = sut.GetSessions();
        Assert.Null(sessions.Encoder);
        Assert.Null(sessions.Decoder);
        Assert.False(sut.IsLoaded);
        Assert.False(sut.IsLoadedAndWarmed);
    }

    [Fact]
    public async Task EnsureLoadedAndWarmedAsync_WhenModelFilesMissing_LeavesRuntimeCold()
    {
        var settingsService = new AppSettingsService(_baseDir);
        settingsService.Settings.AIResourcesDirectory = Path.Combine(_baseDir, "AI");
        var pathService = new AIPathService(settingsService);
        var resolverService = new NativeResolverService(pathService);
        using var sut = new SAM2RuntimeService(pathService, resolverService);

        await sut.EnsureLoadedAndWarmedAsync(SAM2Variant.Tiny);

        Assert.False(sut.IsLoaded);
        Assert.False(sut.IsLoadedAndWarmed);
        Assert.Null(sut.LoadedVariant);
    }

    [Fact]
    public void UnloadModels_BeforeLoad_IsSafe()
    {
        var settingsService = new AppSettingsService(_baseDir);
        var pathService = new AIPathService(settingsService);
        var resolverService = new NativeResolverService(pathService);
        using var sut = new SAM2RuntimeService(pathService, resolverService);

        sut.UnloadModels();

        var sessions = sut.GetSessions();
        Assert.Null(sessions.Encoder);
        Assert.Null(sessions.Decoder);
        Assert.False(sut.IsLoaded);
        Assert.False(sut.IsLoadedAndWarmed);
    }

    [Fact]
    public void ReleaseLease_WhenLastLeaseReleased_LeavesRuntimeIdle()
    {
        var settingsService = new AppSettingsService(_baseDir);
        var pathService = new AIPathService(settingsService);
        var resolverService = new NativeResolverService(pathService);
        using var sut = new SAM2RuntimeService(pathService, resolverService);

        var leaseId = sut.AcquireLease();
        Assert.True(sut.HasActiveLeases);

        sut.ReleaseLease(leaseId);

        Assert.False(sut.HasActiveLeases);
        Assert.False(sut.IsLoaded);
        Assert.False(sut.IsLoadedAndWarmed);
    }


    /// <summary>Long enough for a blocked call to show it is blocked; short enough to keep the suite fast.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(200);

    private SAM2RuntimeService CreateRuntime()
    {
        var settingsService = new AppSettingsService(_baseDir);
        var pathService = new AIPathService(settingsService);
        return new SAM2RuntimeService(pathService, new NativeResolverService(pathService));
    }

    [Fact]
    public async Task UnloadWaitsForTheRunningInferenceInsteadOfFreeingUnderIt()
    {
        // The crash this guards: Esc released the last lease and the sessions were disposed while the encoder was
        // still inside Run on the thread pool — an access violation, not an exception.
        using var sut = CreateRuntime();
        var runningInference = sut.BeginSessionUse();

        var unload = Task.Run(() => sut.UnloadModels());
        var first = await Task.WhenAny(unload, Task.Delay(Settle));
        Assert.NotSame(unload, first);

        runningInference.Dispose();
        await unload.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task InferenceIsSerialised()
    {
        // Two threads inside Run on one ONNX session crash the process just like a disposed one.
        using var sut = CreateRuntime();
        var first = sut.BeginSessionUse();

        var second = Task.Run(() => sut.BeginSessionUse());
        var winner = await Task.WhenAny(second, Task.Delay(Settle));
        Assert.NotSame(second, winner);

        first.Dispose();
        using var secondUse = await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, sut.ActiveSessionUses);
    }

    [Fact]
    public void ASessionUseReleasesExactlyOnce()
    {
        using var sut = CreateRuntime();
        var use = sut.BeginSessionUse();

        use.Dispose();
        use.Dispose(); // a double dispose must not release the gate twice

        Assert.Equal(0, sut.ActiveSessionUses);
        using var next = sut.BeginSessionUse(); // and the next inference can still start
        Assert.Equal(1, sut.ActiveSessionUses);
    }

    public void Dispose()
    {
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
