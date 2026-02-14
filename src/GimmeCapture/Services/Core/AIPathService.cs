using System;
using System.IO;
using GimmeCapture.Models;

namespace GimmeCapture.Services.Core;

public class AIPathService
{
    private readonly AppSettingsService _settingsService;

    public AIPathService(AppSettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public virtual string GetAIResourcesPath()
    {
        var path = _settingsService.Settings.AIResourcesDirectory;
        if (string.IsNullOrEmpty(path))
        {
            path = Path.Combine(_settingsService.BaseDataDirectory, "AI");
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
        
        string langSuffix = language switch
        {
            OCRLanguage.Japanese => "jp",
            OCRLanguage.Korean => "ko",
            OCRLanguage.English => "en",
            OCRLanguage.TraditionalChinese => "ch",
            OCRLanguage.SimplifiedChinese => "ch",
            _ => "ch" 
        };

        return (
            Path.Combine(ocrDir, "ocr_det.onnx"),
            Path.Combine(ocrDir, $"ocr_rec_{langSuffix}.onnx"),
            Path.Combine(ocrDir, $"ocr_dict_{langSuffix}.txt")
        );
    }

    public virtual (string Encoder, string Decoder, string Tokenizer, string Spm, string Config, string GenConfig) GetNmtPaths()
    {
        var baseDir = GetAIResourcesPath();
        var nmtDir = Path.Combine(baseDir, "nmt");
        return (
            Path.Combine(nmtDir, "encoder_model.onnx"),
            Path.Combine(nmtDir, "decoder_model.onnx"),
            Path.Combine(nmtDir, "tokenizer.json"),
            Path.Combine(nmtDir, "sentencepiece.bpe.model"),
            Path.Combine(nmtDir, "config.json"),
            Path.Combine(nmtDir, "generation_config.json")
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
