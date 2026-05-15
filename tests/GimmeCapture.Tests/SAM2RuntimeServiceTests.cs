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
