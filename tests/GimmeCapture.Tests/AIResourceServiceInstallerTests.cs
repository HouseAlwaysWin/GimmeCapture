using System;
using System.IO;
using System.Threading.Tasks;
using GimmeCapture.Models;

namespace GimmeCapture.Tests;

public sealed class AIResourceServiceInstallerTests : IDisposable
{
    private readonly string _baseDir;
    private readonly AppSettingsService _settingsService;
    private readonly AIPathService _pathService;
    private readonly NativeResolverService _resolverService;
    private readonly Mock<AIModelDownloader> _downloader;
    private readonly AIResourceService _sut;

    public AIResourceServiceInstallerTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_baseDir);

        _settingsService = new AppSettingsService(_baseDir);
        _settingsService.Settings.AIResourcesDirectory = Path.Combine(_baseDir, "AI");
        _pathService = new AIPathService(_settingsService);
        _resolverService = new NativeResolverService(_pathService);
        _downloader = new Mock<AIModelDownloader>();
        _sut = new AIResourceService(_settingsService, _pathService, _resolverService, _downloader.Object);
    }

    [Fact]
    public async Task EnsureLlamaModelAsync_WhenPresetAlreadyExists_DoesNotDownload()
    {
        var modelPath = _sut.GetLlamaModelPathById("translategemma-4b-it");
        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
        File.WriteAllText(modelPath, "existing");

        var result = await _sut.EnsureLlamaModelAsync("translategemma-4b-it");

        Assert.True(result);
        _downloader.Verify(
            downloader => downloader.DownloadFileAsync(
                It.IsAny<ArtifactDescriptor>(),
                It.IsAny<string>(),
                It.IsAny<double>(),
                It.IsAny<double>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public void RemoveLlamaModelPreset_WhenPresetExists_DeletesFile()
    {
        var modelPath = _sut.GetLlamaModelPathById("translategemma-4b-it");
        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
        File.WriteAllText(modelPath, "existing");

        var result = _sut.RemoveLlamaModelPreset("translategemma-4b-it");

        Assert.True(result);
        Assert.False(File.Exists(modelPath));
    }

    [Fact]
    public void RemoveOcrResources_WhenDirectoryExists_DeletesDirectory()
    {
        var ocrPaths = _sut.GetOCRPaths(OCRLanguage.TraditionalChinese);
        Directory.CreateDirectory(Path.GetDirectoryName(ocrPaths.Det)!);
        File.WriteAllText(ocrPaths.Det, "det");
        File.WriteAllText(ocrPaths.Rec, "rec");
        File.WriteAllText(ocrPaths.Dict, "dict");

        var result = _sut.RemoveOCRResources();

        Assert.True(result);
        Assert.False(Directory.Exists(Path.GetDirectoryName(ocrPaths.Det)!));
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
