using System;
using System.Collections.Generic;
using GimmeCapture.Models;

namespace GimmeCapture.Services.Core.AI;

public readonly record struct LlamaModelPreset(string Id, string DisplayName, string DownloadUrl, string FileName);
public readonly record struct AICorePackage(string OnnxRuntimeZipUrl, string U2NetModelUrl);
public readonly record struct Sam2ModelPackage(string EncoderUrl, string DecoderUrl);
public readonly record struct OcrModelPackage(string DetectionUrl, string RecognitionUrl, string DictionaryUrl);
public readonly record struct NmtModelPackage(
    string EncoderUrl,
    string DecoderUrl,
    string TokenizerUrl,
    string SentencePieceUrl,
    string ConfigUrl,
    string GenerationConfigUrl);

public sealed class AIModelCatalog
{
    private static readonly LlamaModelPreset[] LlamaModelPresets =
    {
        new("qwen2.5-1.5b-instruct-q4", "Qwen2.5 1.5B Instruct (Q4)", "https://huggingface.co/bartowski/Qwen2.5-1.5B-Instruct-GGUF/resolve/main/Qwen2.5-1.5B-Instruct-Q4_K_M.gguf?download=true", "Qwen2.5-1.5B-Instruct-Q4_K_M.gguf"),
        new("qwen2.5-3b-instruct-q4", "Qwen2.5 3B Instruct (Q4)", "https://huggingface.co/bartowski/Qwen2.5-3B-Instruct-GGUF/resolve/main/Qwen2.5-3B-Instruct-Q4_K_M.gguf?download=true", "Qwen2.5-3B-Instruct-Q4_K_M.gguf"),
        new("gemma-3-1b-it-q4", "Gemma 3 1B IT (Q4)", "https://huggingface.co/bartowski/google_gemma-3-1b-it-GGUF/resolve/main/google_gemma-3-1b-it-Q4_K_M.gguf?download=true", "google_gemma-3-1b-it-Q4_K_M.gguf"),
        new("gemma-3-4b-it-q4", "Gemma 3 4B IT (Q4)", "https://huggingface.co/bartowski/google_gemma-3-4b-it-GGUF/resolve/main/google_gemma-3-4b-it-Q4_K_M.gguf?download=true", "google_gemma-3-4b-it-Q4_K_M.gguf"),
        new("llama-3.1-8b-instruct-q4", "Llama 3.1 8B Instruct (Q4)", "https://huggingface.co/bartowski/Meta-Llama-3.1-8B-Instruct-GGUF/resolve/main/Meta-Llama-3.1-8B-Instruct-Q4_K_M.gguf?download=true", "Meta-Llama-3.1-8B-Instruct-Q4_K_M.gguf"),
        new("gemma-4-placeholder", "Gemma4 (custom file)", string.Empty, string.Empty),
    };

    public IReadOnlyList<LlamaModelPreset> GetLlamaModelPresets() => LlamaModelPresets;

    public IReadOnlyList<LlamaModelPreset> GetDownloadableLlamaModelPresets() =>
        LlamaModelPresets.AsValueEnumerable()
            .Where(p => !string.IsNullOrWhiteSpace(p.DownloadUrl) && !string.IsNullOrWhiteSpace(p.FileName))
            .ToList();

    public bool TryGetLlamaModelPreset(string modelId, out LlamaModelPreset preset)
    {
        foreach (var candidate in LlamaModelPresets)
        {
            if (string.Equals(candidate.Id, modelId, StringComparison.Ordinal))
            {
                preset = candidate;
                return true;
            }
        }

        preset = default;
        return false;
    }

    public AICorePackage GetAICorePackage() =>
        new(
            "https://github.com/microsoft/onnxruntime/releases/download/v1.20.1/onnxruntime-win-x64-gpu-1.20.1.zip",
            "https://github.com/danielgatis/rembg/releases/download/v0.0.0/u2net.onnx");

    public Sam2ModelPackage GetSam2Package(SAM2Variant variant) =>
        variant switch
        {
            SAM2Variant.Tiny => new(
                "https://huggingface.co/shubham0204/sam2-onnx-models/resolve/main/sam2_hiera_tiny_encoder.onnx?download=true",
                "https://huggingface.co/shubham0204/sam2-onnx-models/resolve/main/sam2_hiera_tiny_decoder.onnx?download=true"),
            SAM2Variant.Small => new(
                "https://huggingface.co/shubham0204/sam2-onnx-models/resolve/main/sam2_hiera_small_encoder.onnx?download=true",
                "https://huggingface.co/shubham0204/sam2-onnx-models/resolve/main/sam2_hiera_small_decoder.onnx?download=true"),
            SAM2Variant.BasePlus => new(
                "https://huggingface.co/shubham0204/sam2-onnx-models/resolve/main/sam2_hiera_base_plus_encoder.onnx?download=true",
                "https://huggingface.co/shubham0204/sam2-onnx-models/resolve/main/sam2_hiera_base_plus_decoder.onnx?download=true"),
            SAM2Variant.Large => new(
                "https://huggingface.co/SharpAI/sam2-hiera-large-onnx/resolve/main/encoder.onnx?download=true",
                "https://huggingface.co/SharpAI/sam2-hiera-large-onnx/resolve/main/decoder.onnx?download=true"),
            _ => new(
                "https://huggingface.co/shubham0204/sam2-onnx-models/resolve/main/sam2_hiera_tiny_encoder.onnx?download=true",
                "https://huggingface.co/shubham0204/sam2-onnx-models/resolve/main/sam2_hiera_tiny_decoder.onnx?download=true")
        };

    public OcrModelPackage GetOcrPackage(OCRLanguage language) =>
        language switch
        {
            OCRLanguage.Japanese => new(
                "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.6.0/onnx/PP-OCRv4/det/ch_PP-OCRv4_det_infer.onnx",
                "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.6.0/onnx/PP-OCRv4/rec/japan_PP-OCRv4_rec_infer.onnx",
                "https://raw.githubusercontent.com/PaddlePaddle/PaddleOCR/release/2.7/ppocr/utils/dict/japan_dict.txt"),
            OCRLanguage.Korean => new(
                "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.6.0/onnx/PP-OCRv4/det/ch_PP-OCRv4_det_infer.onnx",
                "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.6.0/onnx/PP-OCRv4/rec/korean_PP-OCRv4_rec_infer.onnx",
                "https://raw.githubusercontent.com/PaddlePaddle/PaddleOCR/release/2.7/ppocr/utils/dict/korean_dict.txt"),
            OCRLanguage.English => new(
                "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.6.0/onnx/PP-OCRv4/det/ch_PP-OCRv4_det_infer.onnx",
                "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.6.0/onnx/PP-OCRv4/rec/en_PP-OCRv4_rec_infer.onnx",
                "https://raw.githubusercontent.com/PaddlePaddle/PaddleOCR/release/2.7/ppocr/utils/en_dict.txt"),
            _ => new(
                "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.6.0/onnx/PP-OCRv4/det/ch_PP-OCRv4_det_infer.onnx",
                "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.6.0/onnx/PP-OCRv4/rec/ch_PP-OCRv4_rec_infer.onnx",
                "https://raw.githubusercontent.com/PaddlePaddle/PaddleOCR/release/2.7/ppocr/utils/ppocr_keys_v1.txt")
        };

    public NmtModelPackage GetNmtPackage() =>
        new(
            "https://huggingface.co/Xenova/m2m100_418M/resolve/main/onnx/encoder_model_quantized.onnx?download=true",
            "https://huggingface.co/Xenova/m2m100_418M/resolve/main/onnx/decoder_model_quantized.onnx?download=true",
            "https://huggingface.co/Xenova/m2m100_418M/resolve/main/tokenizer.json?download=true",
            "https://huggingface.co/facebook/m2m100_418M/resolve/main/sentencepiece.bpe.model?download=true",
            "https://huggingface.co/Xenova/m2m100_418M/resolve/main/config.json?download=true",
            "https://huggingface.co/Xenova/m2m100_418M/resolve/main/generation_config.json?download=true");
}
