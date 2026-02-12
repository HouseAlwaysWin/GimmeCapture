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
using ReactiveUI;
using SKRectI = SkiaSharp.SKRectI;
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
        
        _dict = File.ReadAllLines(paths.Dict, Encoding.UTF8).ToList();
        // PaddleOCR dict starts from index 1, index 0 is blank
        _dict.Insert(0, "");
        _dict.Add(" ");
        var sample = string.Join(", ", _dict.Skip(1).Take(10).Select(c => $"[{c}] (U+{(c.Length > 0 ? ((int)c[0]).ToString("X4") : "0000")})"));
        Console.WriteLine($"[OCR] Dictionary Loaded: {_dict.Count} items. Sample: {sample}");
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
        Console.WriteLine($"[OCR] Detected {boxes.Count} boxes. Target Output Language: {_settings.TargetLanguage}");
        
        foreach (var box in boxes)
        {
            var text = RecognizeText(bitmap, box);
            Console.WriteLine($"[OCR] Raw Recognized Text: \"{text}\""); // Diagnostic Logging
            
            if (!string.IsNullOrWhiteSpace(text))
            {
                var translated = await TranslateAsync(text);
                Console.WriteLine($"[Translation] From \"{text}\" -> To \"{translated}\""); // Diagnostic Logging
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

    private List<SkiaSharp.SKRectI> DetectText(SKBitmap bitmap)
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
        // Simple threshold-based blob detection
        // mask is [1, 1, H, W] or similar. PaddleOCR det output is probability map.
        var boxes = new List<SKRectI>();
        float threshold = 0.3f;
        
        int h = mask.Dimensions[2];
        int w = mask.Dimensions[3];
        
        var visited = new bool[h, w];
        float scaleX = (float)origW / w;
        float scaleY = (float)origH / h;

        for (int y = 0; y < h; y += 2) // Step for performance
        {
            for (int x = 0; x < w; x += 2)
            {
                if (mask[0, 0, y, x] > threshold && !visited[y, x])
                {
                    // Flood fill to find blob bounds
                    int minX = x, maxX = x, minY = y, maxY = y;
                    var queue = new Queue<(int, int)>();
                    queue.Enqueue((x, y));
                    visited[y, x] = true;

                    while (queue.Count > 0)
                    {
                        var (cx, cy) = queue.Dequeue();
                        minX = Math.Min(minX, cx);
                        maxX = Math.Max(maxX, cx);
                        minY = Math.Min(minY, cy);
                        maxY = Math.Max(maxY, cy);

                        // Check 4-neighbors
                        int[] dx = { 0, 0, 1, -1 };
                        int[] dy = { 1, -1, 0, 0 };
                        for (int i = 0; i < 4; i++)
                        {
                            int nx = cx + dx[i];
                            int ny = cy + dy[i];
                            if (nx >= 0 && nx < w && ny >= 0 && ny < h && !visited[ny, nx] && mask[0, 0, ny, nx] > threshold)
                            {
                                visited[ny, nx] = true;
                                queue.Enqueue((nx, ny));
                            }
                        }
                    }

                    // Convert to original coordinates and add padding
                    int rectX = (int)(minX * scaleX);
                    int rectY = (int)(minY * scaleY);
                    int rectW = (int)((maxX - minX + 1) * scaleX);
                    int rectH = (int)((maxY - minY + 1) * scaleY);
                    
                    if (rectW > 4 && rectH > 4) // Filter noise
                    {
                        // Add some padding
                        int left = Math.Max(0, rectX - 2);
                        int top = Math.Max(0, rectY - 2);
                        int right = Math.Min(origW, rectX + rectW + 2);
                        int bottom = Math.Min(origH, rectY + rectH + 2);
                        boxes.Add(new SKRectI(left, top, right, bottom));
                    }
                }
            }
        }

        // If no boxes found or too many (noise), fallback to whole area for verification
        if (!boxes.Any())
            return new List<SKRectI> { new SKRectI(0, 0, origW, origH) };

        // Post-processing: Merge overlapping boxes
        return MergeOverlappingBoxes(boxes);
    }

    private List<SKRectI> MergeOverlappingBoxes(List<SKRectI> boxes)
    {
        bool merged = true;
        while (merged)
        {
            merged = false;
            for (int i = 0; i < boxes.Count; i++)
            {
                for (int j = i + 1; j < boxes.Count; j++)
                {
                    var r1 = boxes[i];
                    var r2 = boxes[j];
                    
                    // Check if they are close or overlapping
                    // Use manual intersection check for maximum compatibility
                    int eLeft = r1.Left - 5;
                    int eTop = r1.Top - 5;
                    int eRight = r1.Right + 5;
                    int eBottom = r1.Bottom + 5;
                    
                    if (eLeft < r2.Right && eRight > r2.Left && eTop < r2.Bottom && eBottom > r2.Top)
                    {
                        // Manual Union
                        int uLeft = Math.Min(r1.Left, r2.Left);
                        int uTop = Math.Min(r1.Top, r2.Top);
                        int uRight = Math.Max(r1.Right, r2.Right);
                        int uBottom = Math.Max(r1.Bottom, r2.Bottom);
                        
                        boxes[i] = new SKRectI(uLeft, uTop, uRight, uBottom);
                        boxes.RemoveAt(j);
                        merged = true;
                        break;
                    }
                }
                if (merged) break;
            }
        }
        return boxes;
    }

    private string RecognizeText(SKBitmap bitmap, SkiaSharp.SKRectI box)
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
            using var cropped = new global::SkiaSharp.SKBitmap(box.Width, box.Height);
            using var canvas = new global::SkiaSharp.SKCanvas(cropped);
            canvas.DrawBitmap(bitmap, box, new SKRect(0, 0, box.Width, box.Height));
            
            // Ensure height 48 (Standard for many PP-OCRv4 models)
            // Maintain aspect ratio but prevent extreme squashing for tall boxes
            int targetWidth = (int)(box.Width * (48.0 / box.Height));
            if (targetWidth < 64 && box.Width > 20) targetWidth = 64; 
            if (targetWidth < 4) targetWidth = 4;
            if (targetWidth > 1280) targetWidth = 1280;

            int paddedWidth = Math.Max(64, (targetWidth + 31) / 32 * 32); // Reduced min width from 320 to 64
            Console.WriteLine($"[OCR] Recognize Box: {box.Left},{box.Top} {box.Width}x{box.Height} -> Target: {targetWidth}x48, Padded: {paddedWidth}");

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

        bool hasNonBlank = false;
        var topIndices = new List<int>();

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

            if (maxIdx > 0)
            {
                hasNonBlank = true;
                if (maxIdx != prevIdx) topIndices.Add(maxIdx);
            }
            
            if (maxIdx > 0 && maxIdx != prevIdx && maxIdx < _dict.Count)
            {
                var chr = _dict[maxIdx];
                sb.Append(chr);
            }
            prevIdx = maxIdx;
        }

        if (!hasNonBlank) Console.WriteLine("[OCR] DecodeCTC: No characters found (all blank).");
        else Console.WriteLine($"[OCR] DecodeCTC: Found indices [{string.Join(",", topIndices.Take(20))}]");

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

            string model = _settings.OllamaModel;
            if (string.IsNullOrEmpty(model)) 
            {
                var availableModels = await GetAvailableModelsAsync();
                if (availableModels.Any())
                {
                    model = availableModels.First();
                }
                else
                {
                    return "Error: No Ollama models found. Please install one first.";
                }
            }
            
            string sourceLang = _settings.SourceLanguage switch
            {
                OCRLanguage.Japanese => "Japanese",
                OCRLanguage.Korean => "Korean",
                OCRLanguage.English => "English",
                OCRLanguage.TraditionalChinese => "Traditional Chinese",
                OCRLanguage.SimplifiedChinese => "Simplified Chinese",
                _ => "Auto-detect"
            };

            var request = new
            {
                model = model,
                prompt = $@"You are a professional translator. Translate the following text from {sourceLang} to {targetLang}. 
### Instructions:
- Only provide the translated text.
- Do not include any explanations, notes, or original text.
- Maintain the original tone and context.
- If the text is already in the target language, return it as is.

### Text to translate:
""{text}""

### Translation:",
                stream = false
            };

            string url = !string.IsNullOrEmpty(_settings.OllamaApiUrl) ? _settings.OllamaApiUrl : "http://localhost:11434/api/generate";
            Console.WriteLine($"[Translation] Ollama Request: model={request.model}, prompt=\"{request.prompt.Substring(0, Math.Min(30, request.prompt.Length))}...\"");
            
            var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(url, content);
            
            Console.WriteLine($"[Translation] Ollama Response Status: {response.StatusCode}");
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"[Ollama Error] {response.StatusCode}: {errorContent}");
                
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    return $"Error: Model '{request.model}' not found.";
                }
                return $"Error: Ollama {response.StatusCode}";
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var resultText = doc.RootElement.GetProperty("response").GetString()?.Trim() ?? text;
            Console.WriteLine($"[Translation] Result: {resultText}");
            return resultText;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Translation Error] {ex.Message}");
            return text;
        }
    }

    public async Task<List<string>> GetAvailableModelsAsync()
    {
        try
        {
            string url = !string.IsNullOrEmpty(_settings.OllamaApiUrl) ? _settings.OllamaApiUrl.Replace("/generate", "/tags") : "http://localhost:11434/api/tags";
            var response = await _httpClient.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var models = new List<string>();
                if (doc.RootElement.TryGetProperty("models", out var modelsElement))
                {
                    foreach (var model in modelsElement.EnumerateArray())
                    {
                        if (model.TryGetProperty("name", out var nameElement))
                        {
                            models.Add(nameElement.GetString() ?? "");
                        }
                    }
                }
                return models;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Translation] Failed to fetch models: {ex.Message}");
        }
        return new List<string>();
    }
}
