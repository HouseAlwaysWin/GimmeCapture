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
        using var sut = new SAM2RuntimeService(pathService);

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
        using var sut = new SAM2RuntimeService(pathService);

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
        using var sut = new SAM2RuntimeService(pathService);

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
        using var sut = new SAM2RuntimeService(pathService);

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
        return new SAM2RuntimeService(pathService);
    }

    [Fact]
    public async Task UnloadWaitsForTheRunningInferenceInsteadOfFreeingUnderIt()
    {
        // The crash this guards: Esc released the last lease and the sessions were disposed while the encoder was
        // still inside Run on the thread pool — an access violation, not an exception.
        using var sut = CreateRuntime();
        var runningInference = sut.BeginSessionUse();

        var unload = OwnThread.Run(() => sut.UnloadModels());
        var first = await Task.WhenAny(unload, OwnThread.Delay(Settle));
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

        var second = OwnThread.Run(() => sut.BeginSessionUse());
        var winner = await Task.WhenAny(second, OwnThread.Delay(Settle));
        Assert.NotSame(second, winner);

        first.Dispose();
        using var secondUse = await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, sut.ActiveSessionUses);
    }

    /// <summary>How long a test waits for something that should happen; a regression fails rather than hangs.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>A runtime whose model folder is this test's own empty one: a load finds no model files and returns.</summary>
    private SAM2RuntimeService CreateRuntimeWithoutModels()
    {
        var settingsService = new AppSettingsService(_baseDir);
        settingsService.Settings.AIResourcesDirectory = Path.Combine(_baseDir, "AI");
        return new SAM2RuntimeService(new AIPathService(settingsService));
    }

    [Fact]
    public async Task UnloadModelsAsync_ReturnsWithoutWaitingForTheRunningInference()
    {
        // Turning AI off unloaded on the UI thread, and the unload waits up to 30 s for an inference in flight: the
        // settings window froze for as long.
        using var sut = CreateRuntime();
        var runningInference = sut.BeginSessionUse();

        // On a thread of its own, so a call that blocks fails here instead of hanging the test until the unload gives
        // up. OwnThread.Run, unlike Task.Run, does not unwrap the returned task, which would wait for the unload itself.
        var call = OwnThread.Run<Task>(sut.UnloadModelsAsync);
        Assert.Same(call, await Task.WhenAny(call, OwnThread.Delay(Timeout)));

        var unload = await call;
        Assert.False(unload.IsCompleted, "the unload finished while the inference it has to wait for was still running");

        runningInference.Dispose();
        await unload.WaitAsync(Timeout);
    }

    [Fact]
    public async Task ALoad_WaitsForTheUnloadAskedForBeforeIt()
    {
        // Turning AI off and straight back on: a load that did not wait could take the sessions the unload was about
        // to free, and be left with none.
        using var sut = CreateRuntimeWithoutModels();
        var runningInference = sut.BeginSessionUse();
        var unload = sut.UnloadModelsAsync();

        // With no model files a load returns as soon as it starts, so one still pending has not started.
        var load = sut.LoadModelsAsync(SAM2Variant.Tiny);
        Assert.NotSame(load, await Task.WhenAny(load, OwnThread.Delay(Settle)));

        runningInference.Dispose();
        await load.WaitAsync(Timeout);
        Assert.True(unload.IsCompleted);
    }

    [Fact]
    public async Task ALoad_WaitsForTheUnloadTheLastLeaseLeftBehind()
    {
        // Leaving smart selection and going straight back in: the same race, through the idle unload.
        using var sut = CreateRuntimeWithoutModels();
        var lease = sut.AcquireLease();
        var runningInference = sut.BeginSessionUse();
        sut.ReleaseLease(lease);

        var load = sut.LoadModelsAsync(SAM2Variant.Tiny);
        Assert.NotSame(load, await Task.WhenAny(load, OwnThread.Delay(Settle)));

        runningInference.Dispose();
        await load.WaitAsync(Timeout);
    }

    [Fact]
    public async Task ALoad_DoesNotWaitWhenNoUnloadIsPending()
    {
        using var sut = CreateRuntimeWithoutModels();
        await sut.UnloadModelsAsync().WaitAsync(Timeout);

        var load = sut.LoadModelsAsync(SAM2Variant.Tiny);

        Assert.True(load.IsCompleted, "a load waited although every unload before it had finished");
        await load;
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
