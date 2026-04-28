using GimmeCapture.Services.Core.AI;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Tests;

public class LlamaModelCatalogTests : IDisposable
{
    private readonly string _baseDir;
    private readonly AppSettingsService _settings;
    private readonly AIResourceService _sut;

    public LlamaModelCatalogTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_baseDir);

        _settings = new AppSettingsService(_baseDir);
        _settings.Settings.AIResourcesDirectory = Path.Combine(_baseDir, "AI");
        _settings.Settings.LlamaModelId = "gemma-3-1b-it-q4";

        var pathService = new AIPathService(_settings);
        var resolver = new NativeResolverService(pathService);
        var downloader = new AIModelDownloader();
        _sut = new AIResourceService(_settings, pathService, resolver, downloader);
    }

    [Fact]
    public void DownloadablePresets_ShouldContainGemma3_1B_And_4B()
    {
        var presets = _sut.GetDownloadableLlamaModelPresets();
        var ids = presets.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("gemma-3-1b-it-q4", ids);
        Assert.Contains("gemma-3-4b-it-q4", ids);
    }

    [Fact]
    public void GetLlmModelsDir_ShouldReturnDirectory_WhenCustomPathIsFile()
    {
        var customFile = Path.Combine(_baseDir, "custom", "my-model.gguf");
        Directory.CreateDirectory(Path.GetDirectoryName(customFile)!);
        File.WriteAllText(customFile, "dummy");
        _settings.Settings.LlamaCustomModelPath = customFile;

        var dir = _sut.GetLlmModelsDir();

        Assert.Equal(Path.GetDirectoryName(customFile), dir);
    }

    [Fact]
    public void GetInstalledLlamaModelPresets_ShouldOnlyReturnExistingFiles()
    {
        var gemma1bPath = _sut.GetLlamaModelPathById("gemma-3-1b-it-q4");
        Directory.CreateDirectory(Path.GetDirectoryName(gemma1bPath)!);
        File.WriteAllText(gemma1bPath, "dummy");

        var installed = _sut.GetInstalledLlamaModelPresets();
        var ids = installed.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("gemma-3-1b-it-q4", ids);
        Assert.DoesNotContain("gemma-3-4b-it-q4", ids);
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

