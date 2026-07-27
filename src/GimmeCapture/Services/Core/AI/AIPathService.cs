using System;
using System.IO;
using GimmeCapture.Models;
using GimmeCapture.Services.OCR;

namespace GimmeCapture.Services.Core.AI;

public class AIPathService
{
    private readonly IAppSettingsService _settingsService;

    public AIPathService(IAppSettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public virtual string GetAIResourcesPath()
    {
        var path = _settingsService.Settings.AIResourcesDirectory;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = AppStoragePaths.GetSharedAIResourcesDirectory(RuntimePathProvider.GetExecutableDirectory());
        }
        return path;
    }

    public virtual (string Encoder, string Decoder) GetSAM2Paths(SAM2Variant variant)
    {
        var baseDir = GetAIResourcesPath();
        var modelsDir = Path.Combine(baseDir, "models");
        
        return variant switch
        {
            SAM2Variant.Tiny => (Path.Combine(modelsDir, "sam2_hiera_tiny_encoder.onnx"), Path.Combine(modelsDir, "sam2_hiera_tiny_decoder.onnx")),
            SAM2Variant.Small => (Path.Combine(modelsDir, "sam2_hiera_small_encoder.onnx"), Path.Combine(modelsDir, "sam2_hiera_small_decoder.onnx")),
            SAM2Variant.BasePlus => (Path.Combine(modelsDir, "sam2_hiera_base_plus_encoder.onnx"), Path.Combine(modelsDir, "sam2_hiera_base_plus_decoder.onnx")),
            SAM2Variant.Large => (Path.Combine(modelsDir, "sam2_hiera_large_encoder.onnx"), Path.Combine(modelsDir, "sam2_hiera_large_decoder.onnx")),
            _ => (Path.Combine(modelsDir, "sam2_hiera_tiny_encoder.onnx"), Path.Combine(modelsDir, "sam2_hiera_tiny_decoder.onnx"))
        };
    }

    public virtual (string Det, string Rec, string Dict) GetOCRPaths(OCRLanguage language)
    {
        var baseDir = GetAIResourcesPath();
        var ocrDir = Path.Combine(baseDir, "ocr");
        
        // Auto is resolved here rather than falling through to the default arm: it used to land on "ch", so an Auto
        // capture silently ran the simplified-Chinese recogniser and could never read Japanese or Korean at all.
        string langSuffix = OcrLanguageResolver.Resolve(language) switch
        {
            OCRLanguage.Japanese => "jp",
            OCRLanguage.Korean => "ko",
            OCRLanguage.English => "en",
            OCRLanguage.TraditionalChinese => "cht",
            OCRLanguage.SimplifiedChinese => "ch",
            _ => "ch"
        };

        return (
            Path.Combine(ocrDir, "ocr_det.onnx"),
            Path.Combine(ocrDir, $"ocr_rec_{langSuffix}.onnx"),
            Path.Combine(ocrDir, $"ocr_dict_{langSuffix}.txt")
        );
    }

    public virtual string GetAICoreModelPath()
    {
        return Path.Combine(GetAIResourcesPath(), "models", "u2net.onnx");
    }

    public virtual string GetOnnxDllPath()
    {
        return Path.Combine(GetAIResourcesPath(), "runtime", "onnxruntime.dll");
    }

    public virtual string GetAIModelsDir()
    {
        return Path.Combine(GetAIResourcesPath(), "models");
    }

    public virtual string GetRuntimeDir()
    {
        return Path.Combine(GetAIResourcesPath(), "runtime");
    }
}
