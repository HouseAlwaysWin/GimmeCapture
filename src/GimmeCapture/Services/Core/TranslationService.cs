using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using GimmeCapture.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;
using Splat;

namespace GimmeCapture.Services.Core;

public class TranslatedBlock
{
    public string OriginalText { get; set; } = string.Empty;
    public string TranslatedText { get; set; } = string.Empty;
    public Rect Bounds { get; set; }
}

public class TranslationService
{
    private readonly AIResourceService _aiResourceService;
    private InferenceSession? _detSession;
    private InferenceSession? _recSession;
    private OCRLanguage? _currentOCRLanguage;
    private List<string> _dict = new();
    
    private readonly HttpClient _httpClient = new();
    private readonly AppSettings _settings;

    public TranslationService(AIResourceService aiResourceService, AppSettingsService settingsService)
    {
        _aiResourceService = aiResourceService ?? throw new ArgumentNullException(nameof(aiResourceService));
        _settings = settingsService?.Settings ?? new AppSettings(); // Fallback if null, though unlikely in DI
        _httpClient.Timeout = TimeSpan.FromSeconds(60); 
    }

    private async Task EnsureLoadedAsync()
    {
        var targetLang = _settings.SourceLanguage;
        
        // Reload if language changed or sessions missing
        if (_currentOCRLanguage == targetLang && _detSession != null && _recSession != null) return;

        // Dispose previous if switching
        if (_currentOCRLanguage != targetLang)
        {
            _detSession?.Dispose(); _detSession = null;
            _recSession?.Dispose(); _recSession = null;
            System.Diagnostics.Debug.WriteLine($"[OCR] Switching language to {targetLang}");
        }

        System.Diagnostics.Debug.WriteLine("[OCR] Loading models...");
        Console.WriteLine("[OCR] EnsuresOCRAsync calling...");
        await _aiResourceService.EnsureOCRAsync();
        Console.WriteLine("[OCR] EnsuresOCRAsync finished.");
        var paths = _aiResourceService.GetOCRPaths(targetLang);

        if (!File.Exists(paths.Det) || !File.Exists(paths.Rec) || !File.Exists(paths.Dict))
        {
            System.Diagnostics.Debug.WriteLine("[OCR] ABORT: Model files missing even after EnsureOCRAsync");
            throw new FileNotFoundException("OCR model files missing");
        }

        var options = new SessionOptions();
        // Re-enable optimizations as the crash was shape-related, not optimization-related
        options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        
        System.Diagnostics.Debug.WriteLine($"[OCR] Rec Model Size: {new FileInfo(paths.Rec).Length} bytes");
        Console.WriteLine($"[OCR] Rec Model Size: {new FileInfo(paths.Rec).Length} bytes");

        try 
        { 
            options.AppendExecutionProvider_DML(0); 
            System.Diagnostics.Debug.WriteLine("[OCR] Using DirectML");
            Console.WriteLine("[OCR] Using DirectML");
        } 
        catch 
        {
            System.Diagnostics.Debug.WriteLine("[OCR] Using CPU");
            Console.WriteLine("[OCR] Using CPU");
        }

        Console.WriteLine($"[OCR] Creating DetSession from {paths.Det}...");
        _detSession = new InferenceSession(paths.Det, options);
        Console.WriteLine($"[OCR] Creating RecSession from {paths.Rec}...");
        _recSession = new InferenceSession(paths.Rec, options);
        _currentOCRLanguage = targetLang;
        System.Diagnostics.Debug.WriteLine($"[OCR] Sessions initialized ({targetLang})");
        Console.WriteLine($"[OCR] Sessions initialized ({targetLang})");
        
        _dict = File.ReadAllLines(paths.Dict).ToList();
        // PaddleOCR dict starts from index 1, index 0 is blank
        _dict.Insert(0, "");
        _dict.Add(" ");
    }

    public async Task<List<TranslatedBlock>> AnalyzeAndTranslateAsync(SKBitmap bitmap)
    {
        await EnsureLoadedAsync();
        if (_detSession == null || _recSession == null) return new();

        var results = new List<TranslatedBlock>();

        // 1. Detection (Simplified: For now, we'll implement a basic detection bypass or single box if it gets too complex)
        // In a real implementation, we'd run DB model and find contours.
        // For the sake of moving forward, let's implement the core logic for a single pass or mock detection if needed.
        // Actually, let's try to implement a basic DB pre-post processing.
        
        var boxes = DetectText(bitmap);
        System.Diagnostics.Debug.WriteLine($"[OCR] Detected {boxes.Count} boxes");
        Console.WriteLine($"[OCR] Detected {boxes.Count} boxes");
        
        foreach (var box in boxes)
        {
            var text = RecognizeText(bitmap, box);
            Console.WriteLine($"[OCR] Recognized: {text}");
            if (!string.IsNullOrWhiteSpace(text))
            {
                var translated = await TranslateAsync(text);
                results.Add(new TranslatedBlock
                {
                    OriginalText = text,
                    TranslatedText = translated,
                    Bounds = new Rect(box.Left, box.Top, box.Width, box.Height)
                });
            }
        }

        return results;
    }

    private List<SKRectI> DetectText(SKBitmap bitmap)
    {
        // DB Model Pre-processing
        int targetWidth = (bitmap.Width + 31) / 32 * 32;
        int targetHeight = (bitmap.Height + 31) / 32 * 32;
        
        using var resized = bitmap.Resize(new SKImageInfo(targetWidth, targetHeight), SKSamplingOptions.Default);
        var input = new DenseTensor<float>(new[] { 1, 3, targetHeight, targetWidth });
        
        float[] mean = { 0.485f, 0.456f, 0.406f };
        float[] std = { 0.229f, 0.224f, 0.225f };

        for (int y = 0; y < targetHeight; y++)
        {
            for (int x = 0; x < targetWidth; x++)
            {
                var color = resized.GetPixel(x, y);
                input[0, 0, y, x] = (color.Red / 255.0f - mean[0]) / std[0];
                input[0, 1, y, x] = (color.Green / 255.0f - mean[1]) / std[1];
                input[0, 2, y, x] = (color.Blue / 255.0f - mean[2]) / std[2];
            }
        }

        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("x", input) };
        Console.WriteLine("[OCR] Running DetSession...");
        using var outputs = _detSession!.Run(inputs);
        Console.WriteLine("[OCR] DetSession Finished.");
        var outputTensor = outputs.First().AsTensor<float>();

        // Post-processing: Simple thresholding and bounding box finding
        // For high performance, we should use a proper contour finder, but here we'll do a simple greedy scan
        // or just return one big box if detection is too complex for a single step.
        // Actually, let's just use the whole area as a fallback if we can't find clear boxes.
        
        // Better yet, let's implement a very simple grid-based blob detector for now
        return FindBoxesFromMask(outputTensor, targetWidth, targetHeight, bitmap.Width, bitmap.Height);
    }

    private List<SKRectI> FindBoxesFromMask(Tensor<float> mask, int targetW, int targetH, int origW, int origH)
    {
        // Simple implementation: Just return the whole area for now to verify recognition/translation
        // We will refine this with actual contour detection later.
        return new List<SKRectI> { new SKRectI(0, 0, origW, origH) };
    }

    private string RecognizeText(SKBitmap bitmap, SKRectI box)
    {
        try
        {
            Console.WriteLine($"[OCR] RecognizeText Box: {box.Left},{box.Top} {box.Width}x{box.Height}");
            
            if (box.Width <= 0 || box.Height <= 0) 
            {
                Console.WriteLine("[OCR] Skip: Box has invalid dimensions.");
                return "";
            }

            // Crop and Prepare for CRNN
            using var cropped = new SKBitmap(box.Width, box.Height);
            using var canvas = new SKCanvas(cropped);
            canvas.DrawBitmap(bitmap, box, new SKRect(0, 0, box.Width, box.Height));
            
            // Ensure height 48 (Standard for many PP-OCRv4 models)
            int targetWidth = (int)(box.Width * (48.0 / box.Height));
            if (targetWidth < 4) targetWidth = 4;
            
            // Limit max width
            if (targetWidth > 960) targetWidth = 960;

            int paddedWidth = 320;
            if (targetWidth > 320)
            {
                paddedWidth = (targetWidth + 31) / 32 * 32;
            }
            
            Console.WriteLine($"[OCR] TargetWidth: {targetWidth}, TensorWidth: {paddedWidth} (Fixed 48H/Padded)");

            using var tensorBitmap = new SKBitmap(paddedWidth, 48);
            using (var tCanvas = new SKCanvas(tensorBitmap))
            {
                // Fill with White (255,255,255) which maps to 1.0f
                // Most OCR models are trained with white backgrounds.
                tCanvas.Clear(SKColors.White);
                
                // Draw the actual image
                using var rResized = cropped.Resize(new SKImageInfo(targetWidth, 48), SKSamplingOptions.Default);
                if (rResized != null)
                {
                    tCanvas.DrawBitmap(rResized, 0, 0);
                }
            }
            
            var input = new DenseTensor<float>(new[] { 1, 3, 48, paddedWidth });
            for (int y = 0; y < 48; y++)
            {
                for (int x = 0; x < paddedWidth; x++)
                {
                    var color = tensorBitmap.GetPixel(x, y);
                    input[0, 0, y, x] = (color.Red / 255.0f - 0.5f) / 0.5f;
                    input[0, 1, y, x] = (color.Green / 255.0f - 0.5f) / 0.5f;
                    input[0, 2, y, x] = (color.Blue / 255.0f - 0.5f) / 0.5f;
                }
            }

            string inputName = "x";
            if (_recSession != null && _recSession.InputMetadata.Count > 0)
            {
                inputName = _recSession.InputMetadata.Keys.First();
                var meta = _recSession.InputMetadata[inputName];
                Console.WriteLine($"[OCR] Model Input: {inputName} Shape: [{string.Join(",", meta.Dimensions)}]");
            }

            var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(inputName, input) };
            
            using var outputs = _recSession!.Run(inputs);
            var outputTensor = outputs.First().AsTensor<float>();

            // Decode CTC
            return DecodeCTC(outputTensor);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OCR] CRITICAL RecognizeText Error: {ex.GetType().Name} - {ex.Message} at {ex.StackTrace}");
            return "";
        }
    }

    private string DecodeCTC(Tensor<float> tensor)
    {
        if (_dict == null || _dict.Count == 0)
        {
            Console.WriteLine("[OCR] Dictionary is empty/null during DecodeCTC");
            return "";
        }

        var sb = new StringBuilder();
        int prevIdx = -1;
        
        // Tensor shape: [1, seq_len, dict_size]
        int seqLen = tensor.Dimensions[1];
        int dictSize = tensor.Dimensions[2];

        for (int i = 0; i < seqLen; i++)
        {
            int maxIdx = 0;
            float maxVal = tensor[0, i, 0];
            for (int j = 1; j < dictSize; j++)
            {
                if (tensor[0, i, j] > maxVal)
                {
                    maxVal = tensor[0, i, j];
                    maxIdx = j;
                }
            }

            if (maxIdx > 0 && maxIdx != prevIdx && maxIdx < _dict.Count)
            {
                sb.Append(_dict[maxIdx]);
            }
            prevIdx = maxIdx;
        }

        return sb.ToString();
    }

    private async Task<string> TranslateAsync(string text)
    {
        try
        {
            string targetLang = _settings.TargetLanguage switch
            {
                TranslationLanguage.TraditionalChinese => "Traditional Chinese (Taiwan)",
                TranslationLanguage.SimplifiedChinese => "Simplified Chinese",
                TranslationLanguage.English => "English",
                TranslationLanguage.Japanese => "Japanese",
                TranslationLanguage.Korean => "Korean",
                _ => "Traditional Chinese"
            };

            var request = new
            {
                model = _settings.OllamaModel,
                prompt = $"Translate the following text to {targetLang}. Only provide the translation, no explanation: \"{text}\"",
                stream = false
            };

            string url = !string.IsNullOrEmpty(_settings.OllamaApiUrl) ? _settings.OllamaApiUrl : "http://localhost:11434/api/generate";
            var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(url, content);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                System.Diagnostics.Debug.WriteLine($"[Ollama Error] {response.StatusCode}: {errorContent}");
                Console.WriteLine($"[Ollama Error] {response.StatusCode}: {errorContent}");
                
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    return $"Error: Model '{request.model}' not found. Please run 'ollama pull {request.model}' in terminal.";
                }
                return $"Error: Ollama returned {response.StatusCode}";
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("response").GetString()?.Trim() ?? text;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Translation Error] {ex.Message}");
            return text;
        }
    }
}
