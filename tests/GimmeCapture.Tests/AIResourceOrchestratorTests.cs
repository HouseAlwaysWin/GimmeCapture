using System;
using System.IO;
using GimmeCapture.Models;
using GimmeCapture.Services.OCR;

namespace GimmeCapture.Tests;

// The Modules tab's OCR badge counts the languages on disk. It said "not installed" short of all five, which read as
// broken to a user whose OCR was reading the two Chinese ones it had.
public sealed class AIResourceOrchestratorTests : IDisposable
{
    private readonly string _baseDir;
    private readonly AIPathService _paths;
    private readonly AIResourceOrchestrator _sut;

    public AIResourceOrchestratorTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_baseDir);

        var settings = new AppSettingsService(_baseDir);
        settings.Settings.AIResourcesDirectory = Path.Combine(_baseDir, "AI");
        _paths = new AIPathService(settings);
        _sut = new AIResourceService(settings, _paths, new AIModelDownloader()).Orchestrator;
    }

    [Fact]
    public void CountOcrLanguagesReady_CountsTheLanguagesWithAllTheirModels()
    {
        // What that user's machine had: the detector and the two Chinese recognisers.
        Install(OCRLanguage.TraditionalChinese);
        Install(OCRLanguage.SimplifiedChinese);
        // A recogniser without its dictionary does not make a language.
        File.WriteAllText(_paths.GetOCRPaths(OCRLanguage.Japanese).Rec, "rec");

        Assert.Equal((2, OcrLanguageResolver.InstallableLanguages.Count), _sut.CountOcrLanguagesReady());
    }

    [Fact]
    public void CountOcrLanguagesReady_CountsNoneWithoutTheDetector()
    {
        Install(OCRLanguage.TraditionalChinese);
        File.Delete(_paths.GetOCRPaths(OCRLanguage.TraditionalChinese).Det);

        Assert.Equal((0, OcrLanguageResolver.InstallableLanguages.Count), _sut.CountOcrLanguagesReady());
    }

    [Fact]
    public void CountOcrLanguagesReady_CountsEveryLanguageOnceAllAreInstalled()
    {
        foreach (var language in OcrLanguageResolver.InstallableLanguages)
        {
            Install(language);
        }

        var (ready, total) = _sut.CountOcrLanguagesReady();

        Assert.Equal(5, total);
        Assert.Equal(total, ready);
    }

    private void Install(OCRLanguage language)
    {
        var (det, rec, dict) = _paths.GetOCRPaths(language);
        Directory.CreateDirectory(Path.GetDirectoryName(det)!);
        File.WriteAllText(det, "det");
        File.WriteAllText(rec, "rec");
        File.WriteAllText(dict, "dict");
    }

    public void Dispose()
    {
        try { Directory.Delete(_baseDir, recursive: true); } catch { /* best effort temp cleanup */ }
    }
}
