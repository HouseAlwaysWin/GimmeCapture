using System;
using System.Collections.Generic;
using GimmeCapture.Models;

namespace GimmeCapture.Services.Core.AI;

public readonly record struct LlamaModelPreset(
    string Id,
    string DisplayName,
    ArtifactDescriptor? Artifact,
    bool IsSelectable,
    bool IsDownloadable,
    string? MigrationTargetId)
{
    public string DownloadUrl => Artifact?.Uri.AbsoluteUri ?? string.Empty;
    public string FileName => Artifact?.FileName ?? string.Empty;
}

public readonly record struct AICorePackage(
    ArtifactDescriptor OnnxRuntimeZip,
    ArtifactDescriptor U2NetModel);

public readonly record struct Sam2ModelPackage(
    ArtifactDescriptor Encoder,
    ArtifactDescriptor Decoder);

public readonly record struct OcrModelPackage(
    ArtifactDescriptor Detection,
    ArtifactDescriptor Recognition,
    ArtifactDescriptor Dictionary);

public sealed class AIModelCatalog
{
    public const string DefaultLlamaModelId = "translategemma-4b-it";

    private static readonly LlamaModelPreset[] LlamaModelPresets =
    {
        DownloadablePreset(
            "translategemma-4b-it",
            "TranslateGemma 4B IT (Q4)",
            "https://huggingface.co/SandLogicTechnologies/translategemma-4b-it-GGUF/resolve/main/translategemma-4b_Q4_K_M.gguf?download=true",
            "translategemma-4b_Q4_K_M.gguf",
            "526747309109c016db547c6fc1c7b0c9c286b5e7a7556827b5419fd9543a09cd",
            2_489_909_312),
        DownloadablePreset(
            "gemma-3-4b-it-q4",
            "Gemma 3 4B IT (Q4)",
            "https://huggingface.co/bartowski/google_gemma-3-4b-it-GGUF/resolve/main/google_gemma-3-4b-it-Q4_K_M.gguf?download=true",
            "google_gemma-3-4b-it-Q4_K_M.gguf",
            "4996030242583a40aa151ff93f49ed787ac8c25e4120c3ae4588b2e2a7d1ae94",
            2_489_758_112),
        DownloadablePreset(
            "translategemma-12b-it",
            "TranslateGemma 12B IT (Q4)",
            "https://huggingface.co/bullerwins/translategemma-12b-it-GGUF/resolve/main/translategemma-12b-it-Q4_K_M.gguf?download=true",
            "translategemma-12b-it-Q4_K_M.gguf",
            "9196d728812afbf5efc10b539298585725edc3a4ecc092c22fdde5bbaf41879e",
            7_300_793_664),
        new("gemma-4-placeholder", "Gemma4 (custom file)", null, true, false, null),
        DeprecatedPreset("gemma-4-E4B", "Gemma 4 E4B IT (Q4)"),
        DeprecatedPreset("qwen3-1.7b-instruct-q4", "Qwen3 1.7B Instruct (Q4)"),
        DeprecatedPreset("qwen3-4b-instruct-q4", "Qwen3 4B Instruct (Q4)"),
        DeprecatedPreset("qwen2.5-1.5b-instruct-q4", "Qwen2.5 1.5B Instruct (Q4)"),
        DeprecatedPreset("qwen2.5-3b-instruct-q4", "Qwen2.5 3B Instruct (Q4)"),
        DeprecatedPreset("gemma-3-1b-it-q4", "Gemma 3 1B IT (Q4)"),
        DeprecatedPreset("llama-3.1-8b-instruct-q4", "Llama 3.1 8B Instruct (Q4)")
    };

    public IReadOnlyList<LlamaModelPreset> GetLlamaModelPresets() =>
        LlamaModelPresets.AsValueEnumerable()
            .Where(preset => preset.IsSelectable)
            .ToList();

    public IReadOnlyList<LlamaModelPreset> GetDownloadableLlamaModelPresets() =>
        LlamaModelPresets.AsValueEnumerable()
            .Where(preset => preset.IsDownloadable && preset.Artifact is not null)
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

    public static string NormalizeLlamaModelId(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return DefaultLlamaModelId;
        }

        foreach (var preset in LlamaModelPresets)
        {
            if (!string.Equals(preset.Id, modelId, StringComparison.Ordinal))
            {
                continue;
            }

            if (preset.IsSelectable)
            {
                return preset.Id;
            }

            return preset.MigrationTargetId ?? DefaultLlamaModelId;
        }

        return DefaultLlamaModelId;
    }

    public AICorePackage GetAICorePackage() =>
        new(
            Descriptor(
                "https://github.com/microsoft/onnxruntime/releases/download/v1.20.1/onnxruntime-win-x64-gpu-1.20.1.zip",
                "onnxruntime-win-x64-gpu-1.20.1.zip",
                "3e9658d4aa7c21b3f5cbb5a7ce0356184f3c183c317b52f9cfff23a3f079634e",
                337_932_991),
            Descriptor(
                "https://github.com/danielgatis/rembg/releases/download/v0.0.0/u2net.onnx",
                "u2net.onnx",
                "8d10d2f3bb75ae3b6d527c77944fc5e7dcd94b29809d47a739a7a728a912b491",
                175_997_641));

    public Sam2ModelPackage GetSam2Package(SAM2Variant variant) =>
        variant switch
        {
            SAM2Variant.Tiny => Sam2(
                "sam2_hiera_tiny_encoder.onnx", "bf8c3c0eb5943b3376261aed4c52cf335161daad0dce5af9c1a243704550ca72", 109_471_936,
                "sam2_hiera_tiny_decoder.onnx", "2008192a9ff55a2a9c46813a56015315f4230495f04e7a315dbedbe35f39a339", 16_540_977),
            SAM2Variant.Small => Sam2(
                "sam2_hiera_small_encoder.onnx", "0edca276aeda91ac715c140266894ffa91885989ea6164be1eb24963ef3f67df", 162_703_493,
                "sam2_hiera_small_decoder.onnx", "c619187e57d45c5b273778ab3eb37e737fed3fac92a2341a1b109d2ef166d9ff", 16_540_983),
            SAM2Variant.BasePlus => Sam2(
                "sam2_hiera_base_plus_encoder.onnx", "0effdbb8c9be0f29bb04fe96e09b93965c49ca678b82b3f3bdaff8418be0d4cd", 277_472_674,
                "sam2_hiera_base_plus_decoder.onnx", "3a0e9232d8ee93231e9d986eba7b457ffe4bb5a9d5d43b87dce8726e00f0a27b", 16_541_001),
            SAM2Variant.Large => new(
                Descriptor(
                    "https://huggingface.co/SharpAI/sam2-hiera-large-onnx/resolve/main/encoder.onnx?download=true",
                    "encoder.onnx",
                    "7c8dbc1648b28f34d92dd0c797a3c8117ce7130cd8cecfff9daf5e53d83b5cf0",
                    889_359_163),
                Descriptor(
                    "https://huggingface.co/SharpAI/sam2-hiera-large-onnx/resolve/main/decoder.onnx?download=true",
                    "decoder.onnx",
                    "e55f83e96e9ec1b1f64cf051e5f229bc6784da055f79e116b27d6621eeae2ce4",
                    20_639_878)),
            _ => GetSam2Package(SAM2Variant.Tiny)
        };

    public OcrModelPackage GetOcrPackage(OCRLanguage language)
    {
        var detection = Descriptor(
            "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.6.0/onnx/PP-OCRv4/det/ch_PP-OCRv4_det_infer.onnx",
            "ch_PP-OCRv4_det_infer.onnx",
            "d2a7720d45a54257208b1e13e36a8479894cb74155a5efe29462512d42f49da9",
            4_745_517);

        return language switch
        {
            OCRLanguage.TraditionalChinese => new(
                detection,
                Descriptor(
                    "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.6.0/onnx/PP-OCRv4/rec/chinese_cht_PP-OCRv3_rec_infer.onnx",
                    "chinese_cht_PP-OCRv3_rec_infer.onnx",
                    "779656d044ce388045e02ea9244724616194e63928606436cdfc6dc3c9528cc6",
                    11_152_536),
                Descriptor(
                    "https://raw.githubusercontent.com/PaddlePaddle/PaddleOCR/release/2.7/ppocr/utils/dict/chinese_cht_dict.txt",
                    "chinese_cht_dict.txt",
                    "832551fee1f2fbc97508772d81ebdc8dba12c00de97a35c71c9ddf43ddac1a83",
                    33_443)),
            OCRLanguage.Japanese => new(
                detection,
                Descriptor(
                    "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.6.0/onnx/PP-OCRv4/rec/japan_PP-OCRv4_rec_infer.onnx",
                    "japan_PP-OCRv4_rec_infer.onnx",
                    "e1075a67dba758ecfc7ebc78a10ae61c95ac8fb66a9c86fab5541e33f085cb7a",
                    9_753_335),
                Descriptor(
                    "https://raw.githubusercontent.com/PaddlePaddle/PaddleOCR/release/2.7/ppocr/utils/dict/japan_dict.txt",
                    "japan_dict.txt",
                    "1dcfcb41eec90576a945b3084f22ade11ced506e24f14879245b071698f308e8",
                    17_332)),
            OCRLanguage.Korean => new(
                detection,
                Descriptor(
                    "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.6.0/onnx/PP-OCRv4/rec/korean_PP-OCRv4_rec_infer.onnx",
                    "korean_PP-OCRv4_rec_infer.onnx",
                    "ab151ba9065eccd98f884cf4d927db091be86137276392072edd4f9d43ad7426",
                    24_067_780),
                Descriptor(
                    "https://raw.githubusercontent.com/PaddlePaddle/PaddleOCR/release/2.7/ppocr/utils/dict/korean_dict.txt",
                    "korean_dict.txt",
                    "aa1fdc8ae8f7cd40a0ec4edb472eb0421e11427e6ccfee9915440742c18b0a20",
                    14_480)),
            OCRLanguage.English => new(
                detection,
                Descriptor(
                    "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.6.0/onnx/PP-OCRv4/rec/en_PP-OCRv4_rec_infer.onnx",
                    "en_PP-OCRv4_rec_infer.onnx",
                    "e8770c967605983d1570cdf5352041dfb68fa0c21664f49f47b155abd3e0e318",
                    7_653_044),
                Descriptor(
                    "https://raw.githubusercontent.com/PaddlePaddle/PaddleOCR/release/2.7/ppocr/utils/en_dict.txt",
                    "en_dict.txt",
                    "5662df9d2d03f0e8ca0d3b0649d6acbab904b6a14b3d3521463c71c37c668ce3",
                    190)),
            _ => new(
                detection,
                Descriptor(
                    "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.6.0/onnx/PP-OCRv4/rec/ch_PP-OCRv4_rec_infer.onnx",
                    "ch_PP-OCRv4_rec_infer.onnx",
                    "48fc40f24f6d2a207a2b1091d3437eb3cc3eb6b676dc3ef9c37384005483683b",
                    10_857_958),
                Descriptor(
                    "https://raw.githubusercontent.com/PaddlePaddle/PaddleOCR/release/2.7/ppocr/utils/ppocr_keys_v1.txt",
                    "ppocr_keys_v1.txt",
                    "28b2362ad4ab2dc38769aa72feb535e3a9ddb3fd2a7585a05920e6393b1dc7f7",
                    26_249))
        };
    }

    private static LlamaModelPreset DownloadablePreset(
        string id,
        string displayName,
        string url,
        string fileName,
        string sha256,
        long size) =>
        new(id, displayName, Descriptor(url, fileName, sha256, size), true, true, null);

    private static LlamaModelPreset DeprecatedPreset(string id, string displayName) =>
        new(id, displayName, null, false, false, DefaultLlamaModelId);

    private static Sam2ModelPackage Sam2(
        string encoderName,
        string encoderHash,
        long encoderSize,
        string decoderName,
        string decoderHash,
        long decoderSize) =>
        new(
            Descriptor(
                $"https://huggingface.co/shubham0204/sam2-onnx-models/resolve/main/{encoderName}?download=true",
                encoderName,
                encoderHash,
                encoderSize),
            Descriptor(
                $"https://huggingface.co/shubham0204/sam2-onnx-models/resolve/main/{decoderName}?download=true",
                decoderName,
                decoderHash,
                decoderSize));

    private static ArtifactDescriptor Descriptor(
        string url,
        string fileName,
        string sha256,
        long size) =>
        new(new Uri(url), fileName, sha256, size);
}
