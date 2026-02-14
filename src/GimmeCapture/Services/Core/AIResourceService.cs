using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ReactiveUI;
using GimmeCapture.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Collections.Generic;
using System.Linq;

namespace GimmeCapture.Services.Core;

public class AIResourceService : ReactiveObject
{
    private const string ModelUrl = "https://github.com/danielgatis/rembg/releases/download/v0.0.0/u2net.onnx";
    private const string MobileSamEncoderUrl = "https://huggingface.co/Acly/MobileSAM/resolve/main/mobile_sam_image_encoder.onnx?download=true";
    private const string MobileSamDecoderUrl = "https://huggingface.co/Acly/MobileSAM/resolve/main/sam_mask_decoder_multi.onnx?download=true";
    
    private const string Sam2TinyEncoderUrl = "https://huggingface.co/shubham0204/sam2-onnx-models/resolve/main/sam2_hiera_tiny_encoder.onnx?download=true";
    private const string Sam2TinyDecoderUrl = "https://huggingface.co/shubham0204/sam2-onnx-models/resolve/main/sam2_hiera_tiny_decoder.onnx?download=true";
    
    private const string Sam2SmallEncoderUrl = "https://huggingface.co/shubham0204/sam2-onnx-models/resolve/main/sam2_hiera_small_encoder.onnx?download=true";
    private const string Sam2SmallDecoderUrl = "https://huggingface.co/shubham0204/sam2-onnx-models/resolve/main/sam2_hiera_small_decoder.onnx?download=true";
    
    // Note: Base Plus is significantly larger
    private const string Sam2BasePlusEncoderUrl = "https://huggingface.co/shubham0204/sam2-onnx-models/resolve/main/sam2_hiera_base_plus_encoder.onnx?download=true";
    private const string Sam2BasePlusDecoderUrl = "https://huggingface.co/shubham0204/sam2-onnx-models/resolve/main/sam2_hiera_base_plus_decoder.onnx?download=true";
    
    private const string Sam2LargeEncoderUrl = "https://huggingface.co/SharpAI/sam2-hiera-large-onnx/resolve/main/encoder.onnx?download=true";
    private const string Sam2LargeDecoderUrl = "https://huggingface.co/SharpAI/sam2-hiera-large-onnx/resolve/main/decoder.onnx?download=true";
    
    // PaddleOCR v4 ONNX Models (Using verified ModelScope mirrors for ONNX and PaddleOCR GitHub for Dicts)
    // Universal Detection Model (ch_PP-OCRv4_det - supports all)
    private const string OcrDetUrl = "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.6.0/onnx/PP-OCRv4/det/ch_PP-OCRv4_det_infer.onnx"; 
    
    // Recognition Models
    // English
    private const string OcrRecEnUrl = "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.6.0/onnx/PP-OCRv4/rec/en_PP-OCRv4_rec_infer.onnx";
    private const string OcrDictEnUrl = "https://raw.githubusercontent.com/PaddlePaddle/PaddleOCR/release/2.7/ppocr/utils/en_dict.txt";
    
    // Chinese Simplified (Standard for Chinese/English mixed)
    private const string OcrRecChsUrl = "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.6.0/onnx/PP-OCRv4/rec/ch_PP-OCRv4_rec_infer.onnx";
    private const string OcrDictChsUrl = "https://raw.githubusercontent.com/PaddlePaddle/PaddleOCR/release/2.7/ppocr/utils/ppocr_keys_v1.txt";

    // Chinese Traditional (No direct v4 ONNX widely avail, using ch_PP-OCRv4 is usually fine or v3, let's stick to v4 chs for now as it handles TC reasonably well, or try find specific)
    // Actually RapidOCR has specific models. Let's use the standard "ch" one as default for both Source variants for now to ensure stability, 
    // unless I find a specific "chinese_cht" one.
    // For now, mapping TC to the main CH model creates less friction.
    
    // Japanese
    private const string OcrRecJpUrl = "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.6.0/onnx/PP-OCRv4/rec/japan_PP-OCRv4_rec_infer.onnx";
    private const string OcrDictJpUrl = "https://raw.githubusercontent.com/PaddlePaddle/PaddleOCR/release/2.7/ppocr/utils/dict/japan_dict.txt";
    
    // Korean
    private const string OcrRecKoUrl = "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.6.0/onnx/PP-OCRv4/rec/korean_PP-OCRv4_rec_infer.onnx";
    private const string OcrDictKoUrl = "https://raw.githubusercontent.com/PaddlePaddle/PaddleOCR/release/2.7/ppocr/utils/dict/korean_dict.txt";

    // MarianMT (M2M100 fallback for high quality ja-zh)
    // Using quantized INT8 models for faster inference (~3-4x speedup, ~5x smaller download)
    private const string NmtEncoderUrl = "https://huggingface.co/Xenova/m2m100_418M/resolve/main/onnx/encoder_model_quantized.onnx?download=true";
    private const string NmtDecoderUrl = "https://huggingface.co/Xenova/m2m100_418M/resolve/main/onnx/decoder_model_quantized.onnx?download=true";
    private const string NmtTokenizerUrl = "https://huggingface.co/Xenova/m2m100_418M/resolve/main/tokenizer.json?download=true";
    private const string NmtSpmUrl = "https://huggingface.co/facebook/m2m100_418M/resolve/main/sentencepiece.bpe.model?download=true";
    private const string NmtConfigUrl = "https://huggingface.co/Xenova/m2m100_418M/resolve/main/config.json?download=true";
    private const string NmtGenerationConfigUrl = "https://huggingface.co/Xenova/m2m100_418M/resolve/main/generation_config.json?download=true";

    // Using a reliable direct link to ONNX Runtime GPU (Win x64)
    private const string OnnxRuntimeZipUrl = "https://github.com/microsoft/onnxruntime/releases/download/v1.20.1/onnxruntime-win-x64-gpu-1.20.1.zip";

    private string _lastErrorMessage = "";
    public string LastErrorMessage
    {
        get => _lastErrorMessage;
        set => this.RaiseAndSetIfChanged(ref _lastErrorMessage, value);
    }

    private double _downloadProgress;
    public double DownloadProgress
    {
        get => _downloadProgress;
        set => this.RaiseAndSetIfChanged(ref _downloadProgress, value);
    }

    private bool _isDownloading;
    public bool IsDownloading
    {
        get => _isDownloading;
        set => this.RaiseAndSetIfChanged(ref _isDownloading, value);
    }

    private string _currentDownloadName = "AI Component";
    public string CurrentDownloadName
    {
        get => _currentDownloadName;
        set => this.RaiseAndSetIfChanged(ref _currentDownloadName, value);
    }

    private readonly AppSettingsService _settingsService;

    public AIResourceService(AppSettingsService settingsService)
    {
        _settingsService = settingsService;
        _lastErrorMessage = string.Empty;
    }

    public string GetAIResourcesPath()
    {
        var path = _settingsService.Settings.AIResourcesDirectory;
        if (string.IsNullOrEmpty(path))
        {
            path = Path.Combine(_settingsService.BaseDataDirectory, "AI");
        }
        return path;
    }

    public (string Encoder, string Decoder) GetSAM2Paths(SAM2Variant variant)
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

    public virtual bool IsNmtReady()
    {
        var paths = GetNmtPaths();
        string[] files = { paths.Encoder, paths.Decoder, paths.Spm, paths.Config };
        
        foreach (var file in files)
        {
            if (!File.Exists(file)) return false;
            
            var info = new FileInfo(file);
            // Quantized model size checks (encoder ~288MB, decoder ~339MB)
            if (file.EndsWith("encoder_model.onnx") && info.Length < 50 * 1024 * 1024) return false;
            if (file.EndsWith("decoder_model.onnx") && info.Length < 50 * 1024 * 1024) return false;
        }
        return true;
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

    public bool IsAICoreReady()
    {
        var baseDir = GetAIResourcesPath();
        var modelPath = Path.Combine(baseDir, "models", "u2net.onnx");
        var onnxDll = Path.Combine(baseDir, "runtime", "onnxruntime.dll");
        return File.Exists(modelPath) && File.Exists(onnxDll);
    }

    public bool IsSAM2Ready(SAM2Variant variant)
    {
        var paths = GetSAM2Paths(variant);
        return File.Exists(paths.Encoder) && File.Exists(paths.Decoder);
    }

    public bool IsOCRReady()
    {
        // Check if CURRENT selected language model exists
        var paths = GetOCRPaths(_settingsService.Settings.SourceLanguage);
        return File.Exists(paths.Det) && File.Exists(paths.Rec) && File.Exists(paths.Dict);
    }

    public bool IsOCRReady(OCRLanguage language)
    {
        var paths = GetOCRPaths(language);
        return File.Exists(paths.Det) && File.Exists(paths.Rec) && File.Exists(paths.Dict);
    }

    // Deprecated monolithic check, keeping for compatibility if needed, but logic should move to specific checks
    public bool AreResourcesReady()
    {
        bool ready = IsAICoreReady() && IsSAM2Ready(_settingsService.Settings.SelectedSAM2Variant) && IsOCRReady();
        if (_settingsService.Settings.SelectedTranslationEngine == TranslationEngine.MarianMT)
        {
            ready = ready && IsNmtReady();
        }
        return ready;
    }

    public bool IsNmtResourcesPresent()
    {
        var paths = GetNmtPaths();
        return File.Exists(paths.Encoder) && File.Exists(paths.Decoder) && File.Exists(paths.Tokenizer);
    }

    public void RemoveAICoreResources()
    {
        try
        {
            var baseDir = GetAIResourcesPath();
            var runtimeDir = Path.Combine(baseDir, "runtime");
            var modelsDir = Path.Combine(baseDir, "models");

            if (Directory.Exists(runtimeDir)) Directory.Delete(runtimeDir, true);
            
            var u2net = Path.Combine(modelsDir, "u2net.onnx");
            if (File.Exists(u2net)) File.Delete(u2net);

            this.RaisePropertyChanged(nameof(IsAICoreReady));
            this.RaisePropertyChanged(nameof(AreResourcesReady));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AI Core Removal Failed: {ex.Message}");
        }
    }

    public void RemoveSAM2Resources(SAM2Variant variant)
    {
        try
        {
            var paths = GetSAM2Paths(variant);
            
            if (File.Exists(paths.Encoder)) File.Delete(paths.Encoder);
            if (File.Exists(paths.Decoder)) File.Delete(paths.Decoder);

            this.RaisePropertyChanged(nameof(IsSAM2Ready));
            this.RaisePropertyChanged(nameof(AreResourcesReady));
        }
        catch (Exception ex)
        {
             System.Diagnostics.Debug.WriteLine($"SAM2 Removal Failed: {ex.Message}");
        }
    }

    public void RemoveOCRResources()
    {
        try
        {
            var baseDir = GetAIResourcesPath();
            var ocrDir = Path.Combine(baseDir, "ocr");

            if (Directory.Exists(ocrDir)) Directory.Delete(ocrDir, true);

            this.RaisePropertyChanged(nameof(IsOCRReady));
            this.RaisePropertyChanged(nameof(AreResourcesReady));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"OCR Removal Failed: {ex.Message}");
        }
    }

    public void RemoveNmtResources()
    {
        try
        {
            var baseDir = GetAIResourcesPath();
            var nmtDir = Path.Combine(baseDir, "nmt");
            if (Directory.Exists(nmtDir)) Directory.Delete(nmtDir, true);
            this.RaisePropertyChanged(nameof(IsNmtReady));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"NMT Removal Failed: {ex.Message}");
        }
    }

    // Deprecated but kept for compatibility if referenced elsewhere
    public void RemoveResources()
    {
        RemoveAICoreResources();
        // Default to removing all if generic called, or maybe just log warning
        var baseDir = GetAIResourcesPath();
        var modelsDir = Path.Combine(baseDir, "models");
        if (Directory.Exists(modelsDir))
        {
            var files = Directory.GetFiles(modelsDir, "sam2_*.onnx");
            foreach (var f in files) try { File.Delete(f); } catch { }
        }
    }

    private readonly SemaphoreSlim _downloadLock = new(1, 1);


    public async Task<bool> EnsureAICoreAsync(CancellationToken ct = default)
    {
        if (IsAICoreReady()) return true;

        await _downloadLock.WaitAsync(ct);
        try
        {
            return await DownloadAICoreInternal(ct);
        }
        finally
        {
            _downloadLock.Release();
        }
    }

    private async Task<bool> DownloadAICoreInternal(CancellationToken ct)
    {
        if (IsAICoreReady()) return true;

        try
        {
            IsDownloading = true;
            CurrentDownloadName = "AI Core";
            DownloadProgress = 0;

            var baseDir = GetAIResourcesPath();
            var runtimeDir = Path.Combine(baseDir, "runtime");
            var modelsDir = Path.Combine(baseDir, "models");

            Directory.CreateDirectory(runtimeDir);
            Directory.CreateDirectory(modelsDir);

            // 1. Download Runtime
            var onnxDll = Path.Combine(runtimeDir, "onnxruntime.dll");
            if (!File.Exists(onnxDll))
            {
                await DownloadAndExtractZip(OnnxRuntimeZipUrl, runtimeDir, 0, 60, ct); 
            }
            else
            {
                DownloadProgress = 60;
            }

            // 2. Download U2Net Model
            var modelPath = Path.Combine(modelsDir, "u2net.onnx");
            if (!File.Exists(modelPath))
            {
                await DownloadFile(ModelUrl, modelPath, 60, 40, ct);
            }
            else
            {
                DownloadProgress = 100;
            }

            return IsAICoreReady();
        }
        catch (OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine("AI Core Download Cancelled");
            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AI Core Download Failed: {ex.Message}");
            LastErrorMessage = ex.Message;
            return false;
        }
        finally
        {
            IsDownloading = false;
        }
    }

    public async Task<bool> EnsureSAM2Async(SAM2Variant variant, CancellationToken ct = default)
    {
        if (IsSAM2Ready(variant)) return true;

        await _downloadLock.WaitAsync(ct);
        try
        {
            return await DownloadSAM2Internal(variant, ct);
        }
        finally
        {
            _downloadLock.Release();
        }
    }

    private async Task<bool> DownloadSAM2Internal(SAM2Variant variant, CancellationToken ct)
    {
         if (IsSAM2Ready(variant)) return true;

        try
        {
            IsDownloading = true;
            CurrentDownloadName = $"SAM2 Model ({variant})";
            DownloadProgress = 0;

            var baseDir = GetAIResourcesPath();
            var modelsDir = Path.Combine(baseDir, "models");
            Directory.CreateDirectory(modelsDir);

            var paths = GetSAM2Paths(variant);
            
            // Determine URLs
            string encoderUrl = variant switch {
                SAM2Variant.Tiny => Sam2TinyEncoderUrl,
                SAM2Variant.Small => Sam2SmallEncoderUrl,
                SAM2Variant.BasePlus => Sam2BasePlusEncoderUrl,
                SAM2Variant.Large => Sam2LargeEncoderUrl,
                _ => Sam2TinyEncoderUrl
            };
            
            string decoderUrl = variant switch {
                SAM2Variant.Tiny => Sam2TinyDecoderUrl,
                SAM2Variant.Small => Sam2SmallDecoderUrl,
                SAM2Variant.BasePlus => Sam2BasePlusDecoderUrl,
                SAM2Variant.Large => Sam2LargeDecoderUrl,
                _ => Sam2TinyDecoderUrl
            };

            // 1. Download Encoder
            if (!File.Exists(paths.Encoder))
            {
                // Encoder is much larger (~90%)
                await DownloadFile(encoderUrl, paths.Encoder, 0, 90, ct);
            }
            else
            {
                DownloadProgress = 90;
            }

            // 2. Download Decoder
            if (!File.Exists(paths.Decoder))
            {
                await DownloadFile(decoderUrl, paths.Decoder, 90, 10, ct);
            }
            else
            {
                DownloadProgress = 100;
            }

            return IsSAM2Ready(variant);
        }
        catch (OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine("SAM2 Download Cancelled");
            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SAM2 Download Failed: {ex.Message}");
            LastErrorMessage = ex.Message;
            return false;
        }
        finally
        {
            IsDownloading = false;
        }
    }

    public virtual async Task<bool> EnsureOCRAsync(CancellationToken ct = default)
    {
        if (IsOCRReady()) return true;

        await _downloadLock.WaitAsync(ct);
        try
        {
            return await DownloadOCRInternal(ct);
        }
        finally
        {
            _downloadLock.Release();
        }
    }

    private async Task<bool> DownloadOCRInternal(CancellationToken ct)
    {
        if (IsOCRReady()) return true;

        try
        {
            IsDownloading = true;
            CurrentDownloadName = "OCR Models (PaddleOCR v5)";
            DownloadProgress = 0;

            var baseDir = GetAIResourcesPath();
            var ocrDir = Path.Combine(baseDir, "ocr");
            Directory.CreateDirectory(ocrDir);

            var language = _settingsService.Settings.SourceLanguage;
            var paths = GetOCRPaths(language);
            
            string recUrl, dictUrl;
            
            switch (language)
            {
                case OCRLanguage.Japanese:
                    recUrl = OcrRecJpUrl;
                    dictUrl = OcrDictJpUrl;
                    break;
                case OCRLanguage.Korean:
                    recUrl = OcrRecKoUrl;
                    dictUrl = OcrDictKoUrl;
                    break;
                case OCRLanguage.English:
                    recUrl = OcrRecEnUrl;
                    dictUrl = OcrDictEnUrl;
                    break;
                case OCRLanguage.Auto:     // Auto
                default:                   // Chinese (Traditional/Simplified)
                    recUrl = OcrRecChsUrl;
                    dictUrl = OcrDictChsUrl;
                    break;
            }

            // 1. Download Detection
            if (!File.Exists(paths.Det))
                await DownloadFile(OcrDetUrl, paths.Det, 0, 40, ct);
            else
                DownloadProgress = 40;

            // 2. Download Recognition
            if (!File.Exists(paths.Rec))
                await DownloadFile(recUrl, paths.Rec, 40, 50, ct);
            else
                DownloadProgress = 90;

            // 3. Download Dictionary
            if (!File.Exists(paths.Dict))
                await DownloadFile(dictUrl, paths.Dict, 90, 10, ct);
            else
                DownloadProgress = 100;

            return IsOCRReady(language);
        }
        catch (OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine("OCR Download Cancelled");
            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"OCR Download Failed: {ex.Message}");
            LastErrorMessage = ex.Message;
            return false;
        }
        finally
        {
            IsDownloading = false;
        }
    }

    public virtual async Task<bool> EnsureNmtAsync(CancellationToken ct = default)
    {
        if (IsNmtReady()) return true;

        await _downloadLock.WaitAsync(ct);
        try
        {
            return await DownloadNmtInternal(ct);
        }
        finally
        {
            _downloadLock.Release();
        }
    }

    private async Task<bool> DownloadNmtInternal(CancellationToken ct)
    {
        if (IsNmtReady()) return true;

        try
        {
            IsDownloading = true;
            CurrentDownloadName = "NMT Translation Models (MarianMT)";
            DownloadProgress = 0;

            var baseDir = GetAIResourcesPath();
            var nmtDir = Path.Combine(baseDir, "nmt");
            Directory.CreateDirectory(nmtDir);

            var paths = GetNmtPaths();

            // 1. Encoder
            if (!File.Exists(paths.Encoder))
                await DownloadFile(NmtEncoderUrl, paths.Encoder, 0, 40, ct);
            else
                DownloadProgress = 40;

            // 2. Decoder
            if (!File.Exists(paths.Decoder))
                await DownloadFile(NmtDecoderUrl, paths.Decoder, 40, 50, ct);
            else
                DownloadProgress = 90;

            // 3. Tokenizer
            if (!File.Exists(paths.Tokenizer))
                await DownloadFile(NmtTokenizerUrl, paths.Tokenizer, 90, 2.5, ct);

            // 4. SentencePiece Model
            if (!File.Exists(paths.Spm))
                await DownloadFile(NmtSpmUrl, paths.Spm, 92.5, 2.5, ct);
            
            // 5. Configs
            if (!File.Exists(paths.Config))
                await DownloadFile(NmtConfigUrl, paths.Config, 95, 2.5, ct);
            if (!File.Exists(paths.GenConfig))
                await DownloadFile(NmtGenerationConfigUrl, paths.GenConfig, 97.5, 2.5, ct);

            DownloadProgress = 100;
            IsDownloading = false;
            return IsNmtReady();
        }
        catch (OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine("NMT Download Cancelled");
            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"NMT Download Failed: {ex.Message}");
            LastErrorMessage = ex.Message;
            return false;
        }
        finally
        {
            IsDownloading = false;
        }
    }

    private async Task DownloadFile(string url, string destination, double progressOffset, double progressWeight, CancellationToken ct)
    {
        using var client = new HttpClient();
        // Increase timeout to 60 minutes for large models
        client.Timeout = TimeSpan.FromMinutes(60);
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
        
        System.Diagnostics.Debug.WriteLine($"[AIResourceService] Downloading {url} to {destination}");
        Console.WriteLine($"[AIResourceService] Downloading {url} to {destination}");
        
        string tempPath = destination + ".tmp";
        
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            using var contentStream = await response.Content.ReadAsStreamAsync(ct);
            using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            var buffer = new byte[8192];
            long totalRead = 0;
            int read;

            while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, read, ct);
                totalRead += read;

                if (totalBytes != -1)
                {
                    DownloadProgress = progressOffset + ((double)totalRead / totalBytes * progressWeight);
                }
            }
            
            await fileStream.FlushAsync(ct);
            fileStream.Close();

            // Verify integrity
            if (totalBytes != -1 && totalRead < totalBytes)
            {
                throw new IOException($"Download truncated: Expected {totalBytes} bytes but only received {totalRead} bytes.");
            }

            // Success: Move to final destination
            if (File.Exists(destination)) File.Delete(destination);
            File.Move(tempPath, destination);
        }
        catch (Exception)
        {
            if (File.Exists(tempPath)) try { File.Delete(tempPath); } catch { }
            throw;
        }
    }

    private async Task DownloadAndExtractZip(string url, string outputDir, double progressOffset, double progressWeight, CancellationToken ct)
    {
        string zipPath = Path.Combine(outputDir, "temp_ai.zip");
        await DownloadFile(url, zipPath, progressOffset, progressWeight, ct);

        await Task.Run(() =>
        {
            if (ct.IsCancellationRequested) return;

            // Official zip has subfolders like onnxruntime-win-x64-gpu-1.20.1/lib/
            // We want to extract just the DLLs from /lib into outputDir
            using (ZipArchive archive = ZipFile.OpenRead(zipPath))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    {
                        // Get only filename
                        string fileName = Path.GetFileName(entry.FullName);
                        string destinationPath = Path.Combine(outputDir, fileName);
                        entry.ExtractToFile(destinationPath, true);
                    }
                }
            }
            File.Delete(zipPath);
        }, ct);
    }
    private static bool _isResolverSet = false;

    public void SetupNativeResolvers()
    {
        if (_isResolverSet) return;
        _isResolverSet = true;

        try
        {
            // Use .NET's modern DllImportResolver to point directly to the AI/runtime folder.
            // This is much more reliable than modifying PATH at runtime.
            // We only need to set this once for the onnxruntime assembly.
            var onnxAssembly = typeof(Microsoft.ML.OnnxRuntime.InferenceSession).Assembly;
            
            System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(onnxAssembly, (libraryName, assembly, searchPath) =>
            {
                if (libraryName == "onnxruntime")
                {
                    var runtimeDir = Path.Combine(GetAIResourcesPath(), "runtime");
                    var dllPath = Path.Combine(runtimeDir, "onnxruntime.dll");
                    
                    if (File.Exists(dllPath))
                    {
                        System.Diagnostics.Debug.WriteLine($"[AI] Custom Resolver loading: {dllPath}");
                        return System.Runtime.InteropServices.NativeLibrary.Load(dllPath);
                    }
                }
                return IntPtr.Zero;
            });

            // Also add to PATH as fallback for dependencies of onnxruntime.dll (like zlib, etc)
            var runtimeDirFallback = Path.Combine(GetAIResourcesPath(), "runtime");
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            if (!path.Contains(runtimeDirFallback))
            {
                Environment.SetEnvironmentVariable("PATH", runtimeDirFallback + Path.PathSeparator + path);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AI] Failed to setup native resolvers: {ex.Message}");
        }
    }

    private InferenceSession? _cachedEncoder;
    private InferenceSession? _cachedDecoder;
    private SAM2Variant? _cachedVariant;
    private bool _isWarmedUp = false;
    private readonly SemaphoreSlim _modelLoadingLock = new(1, 1);

    public async Task LoadSAM2ModelsAsync(SAM2Variant variant)
    {
        if (_cachedVariant == variant && _cachedEncoder != null && _cachedDecoder != null) return;

        await _modelLoadingLock.WaitAsync();
        try
        {
             if (_cachedVariant == variant && _cachedEncoder != null && _cachedDecoder != null) return;

             UnloadSAM2Models();

             var paths = GetSAM2Paths(variant);
             if (!File.Exists(paths.Encoder) || !File.Exists(paths.Decoder))
             {
                 System.Diagnostics.Debug.WriteLine("[AI] Check Model files missing, cannot load.");
                 return;
             }

             await Task.Run(async () =>
             {
                 try
                 {
                     var options = new SessionOptions
                     {
                         GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_BASIC,
                         LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
                     };
                     
                     // Try GPU if available
                     try { options.AppendExecutionProvider_CUDA(0); } catch { }
                     try { options.AppendExecutionProvider_DML(0); } catch { }
                     
                     System.Diagnostics.Debug.WriteLine($"[AI] Loading Encoder: {paths.Encoder}");
                     _cachedEncoder = new InferenceSession(paths.Encoder, options);
                     
                     System.Diagnostics.Debug.WriteLine($"[AI] Loading Decoder: {paths.Decoder}");
                     _cachedDecoder = new InferenceSession(paths.Decoder, options);
                     
                     _cachedVariant = variant;
                     _isWarmedUp = false; // Reset for new variant
                     System.Diagnostics.Debug.WriteLine("[AI] Models Loaded Successfully");
                     
                     // Centralized Warmup: Trigger it once when sessions are created
                     WarmupSessions();
                 }
                 catch (Exception ex)
                 {
                     System.Diagnostics.Debug.WriteLine($"[AI] Model Load Error: {ex.Message}");
                     UnloadSAM2Models();
                     throw;
                 }
             });
        }
        finally
        {
            _modelLoadingLock.Release();
        }
    }

    private void WarmupSessions()
    {
        if (_isWarmedUp || _cachedEncoder == null || _cachedDecoder == null) return;

        System.Diagnostics.Debug.WriteLine("[AI] Warming up SAM2 sessions centralized...");
        try
        {
            // Encoder Warmup
            var encoderInput = new DenseTensor<float>(new[] { 1, 3, 1024, 1024 });
            var encInputMetaData = _cachedEncoder.InputMetadata;
            var encInputName = encInputMetaData.Keys.FirstOrDefault(k => k == "image" || k == "pixel_values") ?? "image";
            var encInputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(encInputName, encoderInput) };
            using var encResults = _cachedEncoder.Run(encInputs);

            // Decoder Warmup (Requires mock embeddings and points)
            var decInputMetaData = _cachedDecoder.InputMetadata;
            var decInputNames = decInputMetaData.Keys.ToList();
            var decInputs = new List<NamedOnnxValue>();

            void AddMock(string[] aliases, int[] dims, float val = 0f)
            {
                var name = decInputNames.FirstOrDefault(n => aliases.Any(a => n == a || n == a.Replace("_", "") || n.Contains(a)));
                if (name == null) return;
                
                var meta = decInputMetaData[name];
                if (meta.ElementType == typeof(int))
                {
                    var data = new int[dims.Aggregate(1, (a, b) => a * b)];
                    if (val != 0) Array.Fill(data, (int)val);
                    decInputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<int>(data, dims)));
                }
                else if (meta.ElementType == typeof(long))
                {
                    var data = new long[dims.Aggregate(1, (a, b) => a * b)];
                    if (val != 0) Array.Fill(data, (long)val);
                    decInputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(data, dims)));
                }
                else
                {
                    var data = new float[dims.Aggregate(1, (a, b) => a * b)];
                    if (val != 0) Array.Fill(data, val);
                    decInputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<float>(data, dims)));
                }
            }

            AddMock(new[] { "image_embeddings", "image_embed", "embeddings", "image_embedding" }, new[] { 1, 256, 64, 64 });
            AddMock(new[] { "high_res_feats_0", "feat_0", "high_res_feat_0" }, new[] { 1, 32, 256, 256 });
            AddMock(new[] { "high_res_feats_1", "feat_1", "high_res_feat_1" }, new[] { 1, 64, 128, 128 });
            AddMock(new[] { "point_coords", "coords" }, new[] { 1, 1, 2 });
            AddMock(new[] { "point_labels", "labels" }, new[] { 1, 1 }, 1f);
            AddMock(new[] { "mask_input", "mask" }, new[] { 1, 1, 256, 256 });
            AddMock(new[] { "has_mask_input", "has_mask" }, new[] { 1 }, 0f);
            AddMock(new[] { "orig_im_size", "im_size" }, new[] { 2 }, 1024f);
            
            using var decResults = _cachedDecoder.Run(decInputs);
            
            _isWarmedUp = true;
            System.Diagnostics.Debug.WriteLine("[AI] Centralized warmup complete.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AI] Session Warmup Warning (Non-fatal): {ex.Message}");
        }
    }

    public (InferenceSession? Encoder, InferenceSession? Decoder) GetSAM2Sessions()
    {
        return (_cachedEncoder, _cachedDecoder);
    }

    public void UnloadSAM2Models()
    {
        _cachedEncoder?.Dispose();
        _cachedEncoder = null;
        
        _cachedDecoder?.Dispose();
        _cachedDecoder = null;
        
        _cachedVariant = null;
        _isWarmedUp = false;
    }
}
