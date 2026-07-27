using System;
using System.IO;
using GimmeCapture.Models;
using GimmeCapture.Services.OCR;

namespace GimmeCapture.Tests;

public sealed class OcrLanguageResolverTests : IDisposable
{
    private readonly string _baseDir;
    private readonly AppSettingsService _settingsService;
    private readonly AIPathService _pathService;

    public OcrLanguageResolverTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_baseDir);

        _settingsService = new AppSettingsService(_baseDir);
        _settingsService.Settings.AIResourcesDirectory = Path.Combine(_baseDir, "AI");
        _pathService = new AIPathService(_settingsService);
    }

    [Fact]
    public void GetOCRPaths_ForAuto_DoesNotFallThroughToSimplifiedChinese()
    {
        // Regression: Auto had no switch arm, so it hit the "ch" default and every auto-language capture silently
        // ran the simplified-Chinese recogniser — which cannot emit kana or hangul at all.
        var auto = _pathService.GetOCRPaths(OCRLanguage.Auto);
        var simplified = _pathService.GetOCRPaths(OCRLanguage.SimplifiedChinese);
        var preferred = _pathService.GetOCRPaths(OcrLanguageResolver.PreferredChineseVariant);

        Assert.NotEqual(simplified.Rec, auto.Rec);
        Assert.Equal(preferred.Rec, auto.Rec);
        Assert.Equal(preferred.Dict, auto.Dict);
    }

    [Theory]
    [InlineData(OCRLanguage.Japanese, "ocr_rec_jp.onnx", "ocr_dict_jp.txt")]
    [InlineData(OCRLanguage.Korean, "ocr_rec_ko.onnx", "ocr_dict_ko.txt")]
    [InlineData(OCRLanguage.English, "ocr_rec_en.onnx", "ocr_dict_en.txt")]
    [InlineData(OCRLanguage.TraditionalChinese, "ocr_rec_cht.onnx", "ocr_dict_cht.txt")]
    [InlineData(OCRLanguage.SimplifiedChinese, "ocr_rec_ch.onnx", "ocr_dict_ch.txt")]
    public void GetOCRPaths_MapsEachLanguageToItsOwnRecogniser(OCRLanguage language, string rec, string dict)
    {
        var paths = _pathService.GetOCRPaths(language);

        Assert.Equal(rec, Path.GetFileName(paths.Rec));
        Assert.Equal(dict, Path.GetFileName(paths.Dict));
        // The detection model is language-independent and shared by every recogniser.
        Assert.Equal("ocr_det.onnx", Path.GetFileName(paths.Det));
    }

    [Fact]
    public void Resolve_LeavesExplicitLanguagesAlone()
    {
        Assert.Equal(OCRLanguage.Japanese, OcrLanguageResolver.Resolve(OCRLanguage.Japanese));
        Assert.Equal(OCRLanguage.SimplifiedChinese, OcrLanguageResolver.Resolve(OCRLanguage.SimplifiedChinese));
        Assert.Equal(OcrLanguageResolver.PreferredChineseVariant, OcrLanguageResolver.Resolve(OCRLanguage.Auto));
    }

    [Fact]
    public void ProbeCandidates_CarryExactlyOneChineseVariant()
    {
        // Traditional vs simplified has no character-set signal, so the probe must never be asked to choose.
        int chineseCandidates = 0;
        foreach (var candidate in OcrLanguageResolver.ProbeCandidates)
        {
            if (candidate is OCRLanguage.TraditionalChinese or OCRLanguage.SimplifiedChinese)
            {
                chineseCandidates++;
            }

            Assert.NotEqual(OCRLanguage.Auto, candidate);
        }

        Assert.Equal(1, chineseCandidates);
        Assert.Contains(OCRLanguage.Japanese, OcrLanguageResolver.ProbeCandidates);
        Assert.Contains(OCRLanguage.Korean, OcrLanguageResolver.ProbeCandidates);
        Assert.Contains(OCRLanguage.English, OcrLanguageResolver.ProbeCandidates);
    }

    [Fact]
    public void InstallableLanguages_CoverEveryConcreteOcrLanguage()
    {
        foreach (OCRLanguage language in Enum.GetValues<OCRLanguage>())
        {
            if (language == OCRLanguage.Auto) continue;
            Assert.Contains(language, OcrLanguageResolver.InstallableLanguages);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_baseDir, recursive: true); } catch { /* best effort temp cleanup */ }
    }
}
