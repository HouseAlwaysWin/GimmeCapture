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
    private List<string> _dict = new();
    
    private readonly HttpClient _httpClient = new();
    private const string OllamaUrl = "http://localhost:11434/api/generate";

    public TranslationService()
    {
        _aiResourceService = Locator.Current.GetService<AIResourceService>() ?? throw new Exception("AIResourceService not found");
        _httpClient.Timeout = TimeSpan.FromSeconds(5); // Don't hang the loop if Ollama is slow/missing
    }

    private async Task EnsureLoadedAsync()
    {
        if (_detSession != null && _recSession != null) return;

        System.Diagnostics.Debug.WriteLine("[OCR] Loading models...");
        await _aiResourceService.EnsureOCRAsync();
        var paths = _aiResourceService.GetOCRPaths();

        if (!File.Exists(paths.Det) || !File.Exists(paths.Rec) || !File.Exists(paths.Dict))
        {
            System.Diagnostics.Debug.WriteLine("[OCR] ABORT: Model files missing even after EnsureOCRAsync");
            throw new FileNotFoundException("OCR model files missing");
        }

        var options = new SessionOptions();
        try 
        { 
            options.AppendExecutionProvider_DML(0); 
            System.Diagnostics.Debug.WriteLine("[OCR] Using DirectML");
        } 
        catch 
        {
            System.Diagnostics.Debug.WriteLine("[OCR] Using CPU");
        }

        _detSession = new InferenceSession(paths.Det, options);
        _recSession = new InferenceSession(paths.Rec, options);
        System.Diagnostics.Debug.WriteLine("[OCR] Sessions initialized");
        
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
        
        foreach (var box in boxes)
        {
            var text = RecognizeText(bitmap, box);
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
        using var outputs = _detSession!.Run(inputs);
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
        // Crop and Prepare for CRNN
        using var cropped = new SKBitmap(box.Width, box.Height);
        using var canvas = new SKCanvas(cropped);
        canvas.DrawBitmap(bitmap, box, new SKRect(0, 0, box.Width, box.Height));
        
        // CRNN expects fixed height 48
        int recWidth = (int)(box.Width * (48.0 / box.Height));
        using var resized = cropped.Resize(new SKImageInfo(recWidth, 48), SKSamplingOptions.Default);
        
        var input = new DenseTensor<float>(new[] { 1, 3, 48, recWidth });
        for (int y = 0; y < 48; y++)
        {
            for (int x = 0; x < recWidth; x++)
            {
                var color = resized.GetPixel(x, y);
                input[0, 0, y, x] = (color.Red / 255.0f - 0.5f) / 0.5f;
                input[0, 1, y, x] = (color.Green / 255.0f - 0.5f) / 0.5f;
                input[0, 2, y, x] = (color.Blue / 255.0f - 0.5f) / 0.5f;
            }
        }

        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("x", input) };
        using var outputs = _recSession!.Run(inputs);
        var outputTensor = outputs.First().AsTensor<float>();

        // Decode CTC
        return DecodeCTC(outputTensor);
    }

    private string DecodeCTC(Tensor<float> tensor)
    {
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
             var request = new
            {
                model = "qwen2.5:3b",
                prompt = $"Translate the following text to Traditional Chinese. Only provide the translation, no explanation: \"{text}\"",
                stream = false
            };

            var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(OllamaUrl, content);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            System.Diagnostics.Debug.WriteLine($"[Ollama] Response: {json}");
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
