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
    private readonly Mock<AIModelDownloader> _downloader;
    private readonly AIResourceService _sut;

    public AIResourceServiceInstallerTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_baseDir);

        _settingsService = new AppSettingsService(_baseDir);
        _settingsService.Settings.AIResourcesDirectory = Path.Combine(_baseDir, "AI");
        _pathService = new AIPathService(_settingsService);
        _downloader = new Mock<AIModelDownloader>();
        _sut = new AIResourceService(_settingsService, _pathService, _downloader.Object);
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

    // AI Core used to download onnxruntime-win-x64-gpu-1.20.1.zip as well, and the app then loaded that runtime in
    // place of the one it ships: 671 MB unpacked, and under the shipped 1.24.4 wrapper a crash on the first session.
    [Fact]
    public async Task EnsureAICoreAsync_DownloadsTheU2NetModelAndNothingElse()
    {
        var downloads = new List<string>();
        _downloader
            .Setup(downloader => downloader.DownloadFileAsync(
                It.IsAny<ArtifactDescriptor>(),
                It.IsAny<string>(),
                It.IsAny<double>(),
                It.IsAny<double>(),
                It.IsAny<CancellationToken>()))
            .Callback<ArtifactDescriptor, string, double, double, CancellationToken>((descriptor, destination, _, _, _) =>
            {
                downloads.Add(descriptor.FileName);
                File.WriteAllText(destination, "model");
            })
            .Returns(Task.CompletedTask);

        Assert.True(await _sut.EnsureAICoreAsync());

        Assert.Equal(new[] { "u2net.onnx" }, downloads);
        Assert.False(Directory.Exists(Path.Combine(_baseDir, "AI", "runtime")));
    }

    [Fact]
    public void AICore_IsInstalledOnceItsModelIsThere()
    {
        WriteFile(Path.Combine("AI", "models", "u2net.onnx"));

        Assert.True(_sut.IsAICoreReady());
    }

    [Fact]
    public void RemoveAICoreResources_AlsoDeletesTheRuntimeOlderVersionsDownloaded()
    {
        string model = WriteFile(Path.Combine("AI", "models", "u2net.onnx"));
        string runtime = WriteFile(Path.Combine("AI", "runtime", "onnxruntime_providers_cuda.dll"));

        Assert.True(_sut.RemoveAICoreResources());

        Assert.False(File.Exists(model));
        Assert.False(Directory.Exists(Path.GetDirectoryName(runtime)));
    }

    private string WriteFile(string relativePath)
    {
        string path = Path.Combine(_baseDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
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
