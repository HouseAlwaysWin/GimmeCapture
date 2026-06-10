using GimmeCapture.Models;
using GimmeCapture.Services.Core.AI;

namespace GimmeCapture.Tests;

public class AIModelCatalogTests
{
    private readonly AIModelCatalog _sut = new();

    [Fact]
    public void GetDownloadableLlamaModelPresets_ShouldExcludePlaceholder()
    {
        var presets = _sut.GetDownloadableLlamaModelPresets();
        var ids = presets.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("gemma-3-1b-it-q4", ids);
        Assert.Contains("gemma-3-4b-it-q4", ids);
        Assert.DoesNotContain("gemma-4-placeholder", ids);
    }

    [Theory]
    [InlineData(OCRLanguage.TraditionalChinese, "chinese_cht_PP-OCRv3_rec_infer.onnx", "chinese_cht_dict.txt")]
    [InlineData(OCRLanguage.SimplifiedChinese, "ch_PP-OCRv4_rec_infer.onnx", "ppocr_keys_v1.txt")]
    [InlineData(OCRLanguage.English, "en_PP-OCRv4_rec_infer.onnx", "en_dict.txt")]
    [InlineData(OCRLanguage.Japanese, "japan_PP-OCRv4_rec_infer.onnx", "japan_dict.txt")]
    [InlineData(OCRLanguage.Korean, "korean_PP-OCRv4_rec_infer.onnx", "korean_dict.txt")]
    public void GetOcrPackage_ShouldReturnExpectedArtifacts(OCRLanguage language, string expectedRecognitionFile, string expectedDictionaryFile)
    {
        var package = _sut.GetOcrPackage(language);

        Assert.EndsWith("ch_PP-OCRv4_det_infer.onnx", package.DetectionUrl, StringComparison.Ordinal);
        Assert.EndsWith(expectedRecognitionFile, package.RecognitionUrl, StringComparison.Ordinal);
        Assert.EndsWith(expectedDictionaryFile, package.DictionaryUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void GetOcrPaths_ShouldKeepTraditionalAndSimplifiedModelsSeparate()
    {
        var settings = new AppSettingsService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var paths = new AIPathService(settings);

        var traditional = paths.GetOCRPaths(OCRLanguage.TraditionalChinese);
        var simplified = paths.GetOCRPaths(OCRLanguage.SimplifiedChinese);

        Assert.EndsWith("ocr_rec_cht.onnx", traditional.Rec, StringComparison.Ordinal);
        Assert.EndsWith("ocr_dict_cht.txt", traditional.Dict, StringComparison.Ordinal);
        Assert.EndsWith("ocr_rec_ch.onnx", simplified.Rec, StringComparison.Ordinal);
        Assert.EndsWith("ocr_dict_ch.txt", simplified.Dict, StringComparison.Ordinal);
    }

    [Fact]
    public void GetSam2Package_ShouldReturnLargeVariantUrls()
    {
        var package = _sut.GetSam2Package(SAM2Variant.Large);

        Assert.Contains("sam2-hiera-large-onnx", package.EncoderUrl, StringComparison.Ordinal);
        Assert.Contains("sam2-hiera-large-onnx", package.DecoderUrl, StringComparison.Ordinal);
    }
}
