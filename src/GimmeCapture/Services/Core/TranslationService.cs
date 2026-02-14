using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading;
using System.Text.RegularExpressions;
using System.Globalization;
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
    private const float DetBoxThreshold = 0.3f;
    private const float DetMinAreaRatio = 0.00005f;
    private const int DetMaxBoxes = 256;
    private const float RecMinAcceptConfidence = 0.10f;
    private const int MaxTranslationBlocks = 1;
    private static readonly bool SaveOcrDebugImages = false;
    private static readonly Regex LatinOrCjkRegex = new(@"[\p{IsCJKUnifiedIdeographs}\p{IsHiragana}\p{IsKatakana}\p{IsHangulSyllables}A-Za-z0-9]", RegexOptions.Compiled);

    private readonly AIResourceService _aiResourceService;
    private InferenceSession? _detSession;
    private InferenceSession? _recSession;
    private OCRLanguage? _currentOCRLanguage;
    private List<string> _dict = new();
    
    private readonly HttpClient _httpClient = new();
    private readonly AppSettingsService _settingsService;
    private readonly MarianMTService _marianMTService;
    private AppSettings _settings => _settingsService.Settings;
    private DateTime _modelsCacheAtUtc = DateTime.MinValue;
    private List<string> _cachedModels = new();
 
    public TranslationService(AIResourceService aiResourceService, AppSettingsService settingsService, MarianMTService marianMTService)
    {
        _aiResourceService = aiResourceService ?? throw new ArgumentNullException(nameof(aiResourceService));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _marianMTService = marianMTService ?? throw new ArgumentNullException(nameof(marianMTService));
        _httpClient.Timeout = TimeSpan.FromSeconds(20); 
    }


    private async Task EnsureLoadedAsync(OCRLanguage? targetLangOverride = null)
    {
        var targetLang = targetLangOverride ?? _settings.SourceLanguage;
        
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
        Debug.WriteLine("[OCR] EnsuresOCRAsync calling...");
        await _aiResourceService.EnsureOCRAsync();
        Debug.WriteLine("[OCR] EnsuresOCRAsync finished.");
        var paths = _aiResourceService.GetOCRPaths(targetLang);

        var options = new SessionOptions();
        options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        
        try 
        { 
            options.AppendExecutionProvider_CUDA(0);
            Debug.WriteLine("[OCR] CUDA Provider enabled.");
        } 
        catch { /* CUDA not available */ }

        try 
        {
            options.AppendExecutionProvider_DML(0); 
            Debug.WriteLine("[OCR] DirectML Provider enabled.");
        }
        catch { /* DirectML not available */ }


        _detSession = new InferenceSession(paths.Det, options);
        _recSession = new InferenceSession(paths.Rec, options);
        _currentOCRLanguage = targetLang;
        
        _dict = LoadDictionaryWithEncodingFallback(paths.Dict);
        // PaddleOCR index 0 is CTC blank
        _dict.Insert(0, "");
        Debug.WriteLine($"[OCR] Sessions initialized. Selected: {targetLang}. Dictionary: {_dict.Count} items.");
        Debug.WriteLine($"[OCR] Dict sample: {string.Join(", ", _dict.Skip(1).Take(8))}");
    }


    public async Task<List<TranslatedBlock>> AnalyzeAndTranslateAsync(SKBitmap bitmap, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        // Determine the OCR language to load
        OCRLanguage ocrLanguageToLoad = _settings.SourceLanguage;
        if (_settings.SourceLanguage == OCRLanguage.Auto)
        {
            // First, load with a default (e.g., TraditionalChinese) to detect boxes
            await EnsureLoadedAsync(OCRLanguage.TraditionalChinese);
            
            var tempBoxes = DetectText(bitmap);
            var sampleBlocks = tempBoxes.Take(5).ToList(); // Take a few boxes for script detection
            bool detectedJapanese = false;

            foreach (var box in sampleBlocks)
            {
                ct.ThrowIfCancellationRequested();
                var (text, confidence) = RecognizeTextWithConfidence(bitmap, box, ct);
                if (!string.IsNullOrEmpty(text))
                {
                    // Japanese Hiragana: 3040–309F, Katakana: 30A0–30FF
                    if (text.Any(c => (c >= 0x3040 && c <= 0x309F) || (c >= 0x30A0 && c <= 0x30FF)))
                    {
                        detectedJapanese = true;
                        break;
                    }
                }
            }

            if (detectedJapanese)
            {
                Debug.WriteLine("[OCR] Auto detected Japanese script, switching model.");
                ocrLanguageToLoad = OCRLanguage.Japanese;
            }
            else
            {
                Debug.WriteLine("[OCR] Auto defaulting to Chinese/English model.");
                ocrLanguageToLoad = OCRLanguage.TraditionalChinese; // Default to Traditional for TC/SC
            }
        }
        
        await EnsureLoadedAsync(ocrLanguageToLoad);
        if (_detSession == null || _recSession == null) return new();

        var results = new List<TranslatedBlock>();
        var recognizedBlocks = new List<(SKRectI Box, string Text, float Confidence)>();

        var boxes = DetectText(bitmap);
        Debug.WriteLine($"[OCR] Detected {boxes.Count} boxes. Target Output Language: {_settings.TargetLanguage}");
        
        foreach (var box in boxes)
        {
            ct.ThrowIfCancellationRequested();
            var (text, confidence) = RecognizeTextWithConfidence(bitmap, box, ct);
            Debug.WriteLine($"[OCR] Box at ({box.Left},{box.Top},{box.Width},{box.Height}) -> Text: \"{text}\" (Conf: {confidence:F3})");
            
            if (IsUsefulOcrText(text, confidence))
            {
                recognizedBlocks.Add((box, text, confidence));
            }
            else
            {
                Debug.WriteLine($"[OCR] Rejected block: \"{text}\" (Confidence too low or not useful)");
            }
        }

        // Fallback: if all detected boxes fail, try merged box and full image.
        if (recognizedBlocks.Count == 0)
        {
            foreach (var fallbackBox in BuildFallbackRecognitionBoxes(bitmap.Width, bitmap.Height, boxes))
            {
                ct.ThrowIfCancellationRequested();
                var (text, confidence) = RecognizeTextWithConfidence(bitmap, fallbackBox, ct);
                Debug.WriteLine($"[OCR][Fallback] Raw Text: \"{text}\" (Conf: {confidence:F3})");
                if (IsUsefulOcrText(text, confidence))
                {
                    recognizedBlocks.Add((fallbackBox, text, confidence));
                }
            }
        }

        // Merge all recognized blocks into one multi-line block for better context and cleaner UI
        if (recognizedBlocks.Count == 0) return results;

        // Sort by reading order before merging
        var sortedBlocks = recognizedBlocks
            .OrderBy(b => b.Box.Top / 16)
            .ThenBy(b => b.Box.Left)
            .ToList();

        var mergedText = string.Join("\n", sortedBlocks.Select(b => b.Text));
        
        // Calculate Union Bounding Box
        int minX = sortedBlocks.Min(b => b.Box.Left);
        int minY = sortedBlocks.Min(b => b.Box.Top);
        int maxX = sortedBlocks.Max(b => b.Box.Right);
        int maxY = sortedBlocks.Max(b => b.Box.Bottom);
        var unionBox = new SKRectI(minX, minY, maxX, maxY);

        ct.ThrowIfCancellationRequested();
        Debug.WriteLine($"[Translation] Calling TranslateAsync for text: \"{mergedText.Replace("\n", " ")}\"");
        var translated = await TranslateAsync(mergedText, ct);
        Debug.WriteLine($"[Translation] TranslateAsync returned: \"{translated}\"");
        
        bool acceptable = IsTranslationAcceptable(mergedText, translated, _settings.TargetLanguage);
        Debug.WriteLine($"[Translation] Result acceptable: {acceptable}");

        if (!acceptable)
        {
            if (_settings.TargetLanguage == TranslationLanguage.English)
            {
                Debug.WriteLine("[Translation] Attempting ForceTranslate to English...");
                var forced = await ForceTranslateTraditionalChineseToEnglishAsync(mergedText, ct);
                if (IsTranslationAcceptable(mergedText, forced, _settings.TargetLanguage))
                {
                    translated = forced;
                    acceptable = true;
                    Debug.WriteLine($"[Translation] ForceTranslate success: {translated}");
                }
            }
        }

        if (acceptable)
        {
            Debug.WriteLine($"[Translation] Merged result: \"{mergedText.Replace("\n", " ")}\" -> \"{translated.Replace("\n", " ")}\"");
            results.Add(new TranslatedBlock
            {
                OriginalText = mergedText,
                TranslatedText = translated,
                Bounds = new Rect(unionBox.Left, unionBox.Top, unionBox.Width, unionBox.Height)
            });
        }
        else
        {
            // Fallback to simple OCR text if translation is rejected
                var fallbackText = BuildTargetLanguageFallbackText(mergedText, _settings.TargetLanguage);
                if (!string.IsNullOrWhiteSpace(fallbackText))
                {
                    results.Add(new TranslatedBlock
                    {
                        OriginalText = mergedText,
                        TranslatedText = fallbackText, // [FIX] Removed (Fail) prefix to keep clean UI
                        Bounds = new Rect(unionBox.Left, unionBox.Top, unionBox.Width, unionBox.Height)
                    });
                }
        }

        return results;
    }

    private List<SkiaSharp.SKRectI> DetectText(SKBitmap bitmap)
    {
        // DB Model Pre-processing: Scale to 1280 limit (better for high-res screens)
        int limitSideLen = 1280;
        int w = bitmap.Width;
        int h = bitmap.Height;
        float ratio = 1.0f;
        if (Math.Max(h, w) > limitSideLen)
        {
            if (h > w) ratio = (float)limitSideLen / h;
            else ratio = (float)limitSideLen / w;
        }
        int targetWidth = (int)(w * ratio);
        int targetHeight = (int)(h * ratio);
        targetWidth = (targetWidth + 31) / 32 * 32;
        targetHeight = (targetHeight + 31) / 32 * 32;
        
        using var resized = bitmap.Resize(new SKImageInfo(targetWidth, targetHeight), SKSamplingOptions.Default);
        var input = new DenseTensor<float>(new[] { 1, 3, targetHeight, targetWidth });
        
        // ImageNet Normalization for Detection
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



        string inputName = _detSession!.InputMetadata.Keys.FirstOrDefault() ?? "x";
        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(inputName, input) };
        Debug.WriteLine("[OCR] Running DetSession...");
        using var outputs = _detSession!.Run(inputs);
        Debug.WriteLine("[OCR] DetSession Finished.");
        var outputTensor = outputs.First().AsTensor<float>();

        return FindBoxesFromMask(outputTensor, targetWidth, targetHeight, bitmap.Width, bitmap.Height);
    }

    private List<SKRectI> FindBoxesFromMask(Tensor<float> mask, int targetW, int targetH, int origW, int origH)
    {
        var boxes = new List<SKRectI>();
        if (!TryBuildDetProbabilityMap(mask, out var probMap, out var h, out var w))
        {
            return new List<SKRectI> { new SKRectI(0, 0, origW, origH) };
        }

        var visited = new bool[h, w];
        float scaleX = (float)origW / w;
        float scaleY = (float)origH / h;
        float minBlobArea = h * w * DetMinAreaRatio;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (visited[y, x] || probMap[y, x] <= DetBoxThreshold)
                {
                    continue;
                }

                // Flood fill to find blob bounds
                int minX = x, maxX = x, minY = y, maxY = y;
                int area = 0;
                var queue = new Queue<(int X, int Y)>();
                queue.Enqueue((x, y));
                visited[y, x] = true;

                while (queue.Count > 0)
                {
                    var (cx, cy) = queue.Dequeue();
                    area++;
                    minX = Math.Min(minX, cx);
                    maxX = Math.Max(maxX, cx);
                    minY = Math.Min(minY, cy);
                    maxY = Math.Max(maxY, cy);

                    // 4-neighbors
                    int[] dx = { 0, 0, 1, -1 };
                    int[] dy = { 1, -1, 0, 0 };
                    for (int i = 0; i < 4; i++)
                    {
                        int nx = cx + dx[i];
                        int ny = cy + dy[i];
                        if (nx >= 0 && nx < w && ny >= 0 && ny < h && !visited[ny, nx] && probMap[ny, nx] > DetBoxThreshold)
                        {
                            visited[ny, nx] = true;
                            queue.Enqueue((nx, ny));
                        }
                    }
                }

                int blobW = maxX - minX + 1;
                int blobH = maxY - minY + 1;
                if (area < minBlobArea || blobW < 3 || blobH < 3)
                {
                    continue;
                }

                // Approximate DB unclip expansion.
                int perimeter = 2 * (blobW + blobH);
                float expansion = perimeter > 0 ? area * 1.6f / perimeter : 0f;

                int rectX = (int)((minX - expansion) * scaleX);
                int rectY = (int)((minY - expansion) * scaleY);
                int rectW = (int)((blobW + 2 * expansion) * scaleX);
                int rectH = (int)((blobH + 2 * expansion) * scaleY);

                if (rectW > 6 && rectH > 6)
                {
                    var rect = new SKRectI(
                        Math.Max(0, rectX),
                        Math.Max(0, rectY),
                        Math.Min(origW, rectX + rectW),
                        Math.Min(origH, rectY + rectH)
                    );
                    boxes.Add(rect);
                    if (boxes.Count >= DetMaxBoxes) break;
                }
            }
            if (boxes.Count >= DetMaxBoxes) break;
        }

        if (!boxes.Any())
            return new List<SKRectI> { new SKRectI(0, 0, origW, origH) };

        return SortBoxesByReadingOrder(MergeOverlappingBoxes(boxes));
    }

    private static bool TryBuildDetProbabilityMap(Tensor<float> mask, out float[,] probMap, out int h, out int w)
    {
        probMap = new float[1, 1];
        h = 0;
        w = 0;

        if (mask.Dimensions.Length < 2) return false;

        // Common Paddle OCR det outputs:
        // [1,1,H,W], [1,H,W,1], [1,H,W], [H,W]
        if (mask.Dimensions.Length == 4)
        {
            int d0 = mask.Dimensions[0];
            int d1 = mask.Dimensions[1];
            int d2 = mask.Dimensions[2];
            int d3 = mask.Dimensions[3];

            if (d1 == 1) // NCHW
            {
                h = d2;
                w = d3;
                probMap = new float[h, w];
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        probMap[y, x] = ToProbability(mask[0, 0, y, x]);
                    }
                }
                return true;
            }

            if (d3 == 1) // NHWC
            {
                h = d1;
                w = d2;
                probMap = new float[h, w];
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        probMap[y, x] = ToProbability(mask[0, y, x, 0]);
                    }
                }
                return true;
            }

            // Unknown 4D layout, try treating last two as H/W.
            h = d2;
            w = d3;
            probMap = new float[h, w];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    probMap[y, x] = ToProbability(mask[0, 0, y, x]);
                }
            }
            return true;
        }

        if (mask.Dimensions.Length == 3)
        {
            // [1,H,W] or [H,W,1]
            if (mask.Dimensions[0] == 1)
            {
                h = mask.Dimensions[1];
                w = mask.Dimensions[2];
                probMap = new float[h, w];
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        probMap[y, x] = ToProbability(mask[0, y, x]);
                    }
                }
                return true;
            }

            if (mask.Dimensions[2] == 1)
            {
                h = mask.Dimensions[0];
                w = mask.Dimensions[1];
                probMap = new float[h, w];
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        probMap[y, x] = ToProbability(mask[y, x, 0]);
                    }
                }
                return true;
            }
        }

        if (mask.Dimensions.Length == 2)
        {
            h = mask.Dimensions[0];
            w = mask.Dimensions[1];
            probMap = new float[h, w];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    probMap[y, x] = ToProbability(mask[y, x]);
                }
            }
            return true;
        }

        return false;
    }

    private static float ToProbability(float value)
    {
        if (value >= 0f && value <= 1f) return value;
        return 1f / (1f + MathF.Exp(-value));
    }

    private static List<SKRectI> SortBoxesByReadingOrder(List<SKRectI> boxes)
    {
        return boxes
            .OrderBy(b => b.Top / 16)
            .ThenBy(b => b.Left)
            .ToList();
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
                    // [FIX] Reduced vertical buffer to 0 to prevent merging separate lines of text verticaly.
                    // Horizontal buffer kept at 5 to merge split characters/words on the same line.
                    int eLeft = r1.Left - 5;
                    int eTop = r1.Top;     // was -5
                    int eRight = r1.Right + 5;
                    int eBottom = r1.Bottom; // was +5
                    
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

    private List<SKRectI> BuildFallbackRecognitionBoxes(int width, int height, List<SKRectI> detectedBoxes)
    {
        var fallback = new List<SKRectI>();
        if (detectedBoxes.Count > 0)
        {
            int left = detectedBoxes.Min(b => b.Left);
            int top = detectedBoxes.Min(b => b.Top);
            int right = detectedBoxes.Max(b => b.Right);
            int bottom = detectedBoxes.Max(b => b.Bottom);
            fallback.Add(new SKRectI(
                Math.Clamp(left, 0, width),
                Math.Clamp(top, 0, height),
                Math.Clamp(right, 0, width),
                Math.Clamp(bottom, 0, height)));
        }

        fallback.Add(new SKRectI(0, 0, width, height));

        // Keep unique boxes only.
        return fallback
            .Where(b => b.Width > 0 && b.Height > 0)
            .GroupBy(b => $"{b.Left},{b.Top},{b.Right},{b.Bottom}")
            .Select(g => g.First())
            .ToList();
    }

    private (string text, float confidence) RecognizeTextWithConfidence(SKBitmap bitmap, SkiaSharp.SKRectI box, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            if (box.Width <= 0 || box.Height <= 0) return ("", 0f);

            // 1. Crop
            using var cropped = new SKBitmap(box.Width, box.Height);
            using (var canvas = new SKCanvas(cropped))
            {
                canvas.DrawBitmap(bitmap, box, new SKRect(0, 0, box.Width, box.Height));
            }

            // 2. Adaptive Inversion Analysis
            bool shouldInvert = false;
            float avgLuminance = 128f;
            using (var grayscale = new SKBitmap(cropped.Width, cropped.Height, SKColorType.Gray8, SKAlphaType.Opaque))
            {
                using (var grayCanvas = new SKCanvas(grayscale))
                {
                    using var paint = new SKPaint { ColorFilter = SKColorFilter.CreateColorMatrix(new float[] {
                        0.2126f, 0.7152f, 0.0722f, 0, 0,
                        0.2126f, 0.7152f, 0.0722f, 0, 0,
                        0.2126f, 0.7152f, 0.0722f, 0, 0,
                        0, 0, 0, 1, 0
                    }) };
                    grayCanvas.DrawBitmap(cropped, 0, 0, paint);
                }

                long totalLuminous = 0;
                for (int y = 0; y < grayscale.Height; y++)
                    for (int x = 0; x < grayscale.Width; x++)
                        totalLuminous += grayscale.GetPixel(x, y).Red;
                
                avgLuminance = totalLuminous / (float)(cropped.Width * cropped.Height);
                shouldInvert = avgLuminance < 120; 
            }

            // 3. Tensor Prepping
            int targetHeight = 48;
            int textWidth = (int)(box.Width * ((float)targetHeight / box.Height));
            
            // Limit width for stability and performance
            if (textWidth < 16) textWidth = 16; 
            if (textWidth > 1536) textWidth = 1536;

            // PP-OCRv4 REC handles variable width, but padding to 32 is often safer for some ONNX backends
            // However, RapidOCR usually just uses textWidth directly. Let's use textWidth but ensure it's reasonable.
            int paddedWidth = (textWidth + 31) / 32 * 32;

            using var normalBitmap = new SKBitmap(paddedWidth, targetHeight);
            using (var tCanvas = new SKCanvas(normalBitmap))
            {
                tCanvas.Clear(SKColors.White);

                // Use High sampling for better quality on small text
                using var resized = cropped.Resize(new SKImageInfo(textWidth, targetHeight), new SKSamplingOptions(SKCubicResampler.Mitchell));
                if (resized != null)
                {
                    tCanvas.DrawBitmap(resized, 0, 0);
                }
            }

            var normalResult = RunRecognitionOnPreparedBitmap(normalBitmap);
            var bestResult = normalResult;

            if (shouldInvert)
            {
                using var invertedBitmap = new SKBitmap(paddedWidth, targetHeight);
                using (var iCanvas = new SKCanvas(invertedBitmap))
                {
                    iCanvas.Clear(SKColors.White);
                    using var paint = new SKPaint
                    {
                        ColorFilter = SKColorFilter.CreateColorMatrix(new float[]
                        {
                            -1,  0,  0, 0, 255,
                             0, -1,  0, 0, 255,
                             0,  0, -1, 0, 255,
                             0,  0,  0, 1, 0
                        })
                    };

                    // Use High sampling for better quality on small text
                    using var resized = cropped.Resize(new SKImageInfo(textWidth, targetHeight), new SKSamplingOptions(SKCubicResampler.Mitchell));
                    if (resized != null)
                    {
                        iCanvas.DrawBitmap(resized, 0, 0, paint);
                    }
                }

                var invertedResult = RunRecognitionOnPreparedBitmap(invertedBitmap);
                if (invertedResult.confidence > bestResult.confidence ||
                    (string.IsNullOrWhiteSpace(bestResult.text) && !string.IsNullOrWhiteSpace(invertedResult.text)))
                {
                    bestResult = invertedResult;
                }
            }


            // [Diagnostic] Save tensor input images for accuracy analysis
            if (SaveOcrDebugImages)
            {
                try
                {
                    string debugDir = Path.Combine(_settingsService.BaseDataDirectory, "OCR_Debug");
                    Directory.CreateDirectory(debugDir);
                    string fileName = $"rec_{DateTime.Now:HHmmss_fff}_{Guid.NewGuid().ToString().Substring(0, 4)}.png";
                    using var image = SKImage.FromBitmap(normalBitmap);
                    using var dataData = image.Encode(SKEncodedImageFormat.Png, 100);
                    File.WriteAllBytes(Path.Combine(debugDir, fileName), dataData.ToArray());
                }
                catch { }
            }

            return bestResult;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OCR] Recognize Error: {ex.Message}");
            return ("", 0f);
        }
    }

    private (string text, float confidence) RunRecognitionOnPreparedBitmap(SKBitmap tensorBitmap)
    {
        int targetHeight = tensorBitmap.Height;
        int paddedWidth = tensorBitmap.Width;

        // Tensor Conversion
        try
        {
            var input = new DenseTensor<float>(new[] { 1, 3, targetHeight, paddedWidth });
            for (int y = 0; y < targetHeight; y++)
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
                inputName = _recSession.InputMetadata.Keys.First();

            var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(inputName, input) };
            using var outputs = _recSession!.Run(inputs);
            var outputTensor = outputs.First().AsTensor<float>();
            if (outputTensor.Dimensions.Length >= 3)
            {
                Debug.WriteLine($"[OCR] Rec Output Shape: [{string.Join(",", outputTensor.Dimensions.ToArray())}]");
                EnsureDictionaryAlignment(outputTensor.Dimensions[1], outputTensor.Dimensions[2]);
            }

            return DecodeCTCAuto(outputTensor);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OCR] Recognize Error: {ex.Message}");
            return ("", 0f);
        }
    }

    private (string text, float confidence) DecodeCTCAuto(Tensor<float> tensor)
    {
        if (_dict == null || _dict.Count == 0 || tensor.Dimensions.Length != 3) return ("", 0f);

        int d1 = tensor.Dimensions[1];
        int d2 = tensor.Dimensions[2];

        var ntc = DecodeCTCLayoutNTC(tensor, d1, d2);
        var nct = DecodeCTCLayoutNCT(tensor, d1, d2);

        bool ntcPlausible = IsDictSizePlausible(d2);
        bool nctPlausible = IsDictSizePlausible(d1);

        if (ntcPlausible && !nctPlausible) return ntc;
        if (!ntcPlausible && nctPlausible) return nct;

        if (nct.text.Length > ntc.text.Length) return nct;
        if (ntc.text.Length > nct.text.Length) return ntc;
        return nct.confidence > ntc.confidence ? nct : ntc;
    }

    private (string text, float confidence) DecodeCTCLayoutNTC(Tensor<float> tensor, int seqLen, int classCount)
    {
        var sb = new StringBuilder();
        int prevIdx = -1;
        float totalConf = 0f;
        int charCount = 0;

        for (int t = 0; t < seqLen; t++)
        {
            int maxIdx = 0;
            float maxVal = tensor[0, t, 0];
            for (int c = 1; c < classCount; c++)
            {
                float v = tensor[0, t, c];
                if (v > maxVal)
                {
                    maxVal = v;
                    maxIdx = c;
                }
            }

            if (maxIdx > 0 && maxIdx != prevIdx && maxIdx < _dict.Count)
            {
                sb.Append(_dict[maxIdx]);
                totalConf += ToStepProbability(maxVal);
                charCount++;
            }
            prevIdx = maxIdx;
        }

        float avgConf = charCount > 0 ? totalConf / charCount : 0f;
        return (sb.ToString(), avgConf);
    }

    private (string text, float confidence) DecodeCTCLayoutNCT(Tensor<float> tensor, int classCount, int seqLen)
    {
        var sb = new StringBuilder();
        int prevIdx = -1;
        float totalConf = 0f;
        int charCount = 0;

        for (int t = 0; t < seqLen; t++)
        {
            int maxIdx = 0;
            float maxVal = tensor[0, 0, t];
            for (int c = 1; c < classCount; c++)
            {
                float v = tensor[0, c, t];
                if (v > maxVal)
                {
                    maxVal = v;
                    maxIdx = c;
                }
            }

            if (maxIdx > 0 && maxIdx != prevIdx && maxIdx < _dict.Count)
            {
                sb.Append(_dict[maxIdx]);
                totalConf += ToStepProbability(maxVal);
                charCount++;
            }
            prevIdx = maxIdx;
        }

        float avgConf = charCount > 0 ? totalConf / charCount : 0f;
        return (sb.ToString(), avgConf);
    }

    private bool IsDictSizePlausible(int candidate)
    {
        if (candidate <= 1) return false;
        if (_dict.Count == 0) return false;
        int tolerance = Math.Max(64, (int)(_dict.Count * 0.2f));
        return Math.Abs(candidate - _dict.Count) <= tolerance;
    }

    private static float ToStepProbability(float value)
    {
        if (value >= 0f && value <= 1f) return value;
        return 1f / (1f + MathF.Exp(-value));
    }

    private List<string> LoadDictionaryWithEncodingFallback(string path)
    {
        // Register legacy code pages (GB18030) once for non-UTF8 dict files.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        // RapidOCR/Paddle dictionaries are UTF-8 in practice.
        // Prefer UTF-8 and only fallback when it fails validation.
        var utf8Strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        try
        {
            var utf8Lines = File.ReadAllLines(path, utf8Strict).ToList();
            if (IsLikelyValidDictionary(utf8Lines))
            {
                Debug.WriteLine("[OCR] Dictionary decode: UTF-8 strict");
                return utf8Lines;
            }
        }
        catch
        {
            // Continue to fallback logic.
        }

        var candidates = new List<Encoding>
        {
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false),
            Encoding.GetEncoding("GB18030")
        };

        foreach (var enc in candidates)
        {
            try
            {
                var lines = File.ReadAllLines(path, enc).ToList();
                if (IsLikelyValidDictionary(lines))
                {
                    Debug.WriteLine($"[OCR] Dictionary decode fallback: {enc.WebName}");
                    return lines;
                }
            }
            catch
            {
                // try next
            }
        }

        // Last resort: return lenient UTF-8 decode result.
        Debug.WriteLine("[OCR] Dictionary decode: last-resort UTF-8 lenient");
        return File.ReadAllLines(path, new UTF8Encoding(false, false)).ToList();
    }

    private static bool IsLikelyValidDictionary(List<string> lines)
    {
        if (lines.Count < 1000) return false;

        int replacementCount = lines.Sum(l => l.Count(ch => ch == '\uFFFD'));
        if (replacementCount > 0) return false;

        // Most entries should be single-char tokens for PP-OCR dicts.
        int nonEmptyCount = lines.Count(l => l.Length > 0);
        if (nonEmptyCount == 0) return false;
        int singleCharCount = lines.Count(l => l.Length == 1);
        double singleRatio = (double)singleCharCount / nonEmptyCount;

        return singleRatio > 0.90;
    }

    private bool IsUsefulOcrText(string text, float confidence)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (confidence < RecMinAcceptConfidence) return false;

        string trimmed = text.Trim();
        if (trimmed.Length == 0) return false;

        int replacementCount = trimmed.Count(ch => ch == '\uFFFD');
        if (replacementCount > 0) return false;
        if (trimmed.All(ch => ch == '?' || ch == '.' || ch == '-' || ch == '_' || ch == '*')) return false;
        int qCount = trimmed.Count(ch => ch == '?');
        if (qCount > 0 && qCount >= Math.Max(1, trimmed.Length / 3)) return false;

        int useful = trimmed.Count(ch => char.IsLetterOrDigit(ch) || IsCjk(ch));
        if (useful == 0) return false;
        double usefulRatio = (double)useful / Math.Max(1, trimmed.Length);
        if (usefulRatio < 0.5) return false;
        if (trimmed.Length <= 2 && useful == 1 && confidence < 0.35f) return false;
        if (trimmed.Length <= 4 && confidence < 0.5f) return false;

        return true;
    }

    private static double ScoreRecognizedBlock((SKRectI Box, string Text, float Confidence) item)
    {
        int area = Math.Max(1, item.Box.Width * item.Box.Height);
        int textLen = Math.Max(1, item.Text.Trim().Length);
        return item.Confidence * Math.Log(area + 1) * Math.Log(textLen + 1);
    }

    private void EnsureDictionaryAlignment(int dim1, int dim2)
    {
        if (_dict.Count == 0) return;

        int classDimCandidate = Math.Max(dim1, dim2);
        // seq length is usually small (<100). class count should be large.
        if (classDimCandidate < 128) return;

        int diff = classDimCandidate - _dict.Count;
        if (diff > 0 && diff <= 16)
        {
            Debug.WriteLine($"[OCR] Aligning dictionary: {_dict.Count} -> {classDimCandidate}");
            // PaddleOCR often expects a trailing space token.
            if (!_dict.Contains(" "))
            {
                _dict.Add(" ");
                diff--;
            }

            for (int i = 0; i < diff; i++)
            {
                _dict.Add($"<unk{i}>");
            }
        }
        else if (diff < 0 && Math.Abs(diff) <= 16)
        {
            Debug.WriteLine($"[OCR] Trimming dictionary: {_dict.Count} -> {classDimCandidate}");
            _dict = _dict.Take(classDimCandidate).ToList();
        }
    }

    private static bool IsCjk(char ch)
    {
        return (ch >= '\u4E00' && ch <= '\u9FFF')   // CJK Unified Ideographs
            || IsHiraganaKatakana(ch)
            || IsHangul(ch);
    }

    private static bool IsHiraganaKatakana(char ch) => (ch >= '\u3040' && ch <= '\u30FF');
    private static bool IsHangul(char ch) => (ch >= '\uAC00' && ch <= '\uD7AF');

    private string ResolveSourceLanguageForPrompt(string text)
    {
        int cjkCount = text.Count(IsCjk);
        int latinCount = text.Count(ch => (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z'));
        int total = Math.Max(1, text.Count(ch => !char.IsWhiteSpace(ch)));

        double cjkRatio = (double)cjkCount / total;
        double latinRatio = (double)latinCount / total;

        if (text.Any(IsHiraganaKatakana)) return "Japanese";
        if (text.Any(IsHangul)) return "Korean";

        if (cjkRatio > 0.45)
        {
            return _settings.SourceLanguage == OCRLanguage.TraditionalChinese
                ? "Traditional Chinese"
                : "Chinese";
        }

        if (latinRatio > 0.5) return "English";

        return _settings.SourceLanguage switch
        {
            OCRLanguage.Japanese => "Japanese",
            OCRLanguage.Korean => "Korean",
            OCRLanguage.English => "English",
            OCRLanguage.TraditionalChinese => "Traditional Chinese",
            OCRLanguage.SimplifiedChinese => "Simplified Chinese",
            OCRLanguage.Auto => "Chinese",
            _ => "Chinese"
        };
    }


    private async Task<string> TranslateAsync(string text, CancellationToken ct = default)
    {
        try
        {
            text = (text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return text;
            }

            string targetLang = _settings.TargetLanguage switch
            {
                TranslationLanguage.TraditionalChinese => "Traditional Chinese (Taiwan)",
                TranslationLanguage.SimplifiedChinese => "Simplified Chinese",
                TranslationLanguage.English => "English",
                TranslationLanguage.Japanese => "Japanese",
                TranslationLanguage.Korean => "Korean",
                _ => "Traditional Chinese"
            };

            bool bypass = ShouldBypassTranslation(text, _settings.TargetLanguage);
            Debug.WriteLine($"[Translation] ShouldBypassTranslation: {bypass}");
            if (bypass)
            {
                return text;
            }

            if (_settings.SelectedTranslationEngine == TranslationEngine.MarianMT)
            {
                Debug.WriteLine("[Translation] Using MarianMT (Offline) engine.");
                return await _marianMTService.TranslateAsync(text, _settings.TargetLanguage, _settings.SourceLanguage, ct);
            }

            if (_settings.SelectedTranslationEngine == TranslationEngine.Gemini)
            {
                Debug.WriteLine("[Translation] Using Gemini (Google AI) engine.");
                return await TranslateWithGeminiAsync(text, ct);
            }

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
            
            string sourceLang = ResolveSourceLanguageForPrompt(text);

            var request = new
            {
                model = model,
                prompt = BuildStrictTranslationPrompt(sourceLang, targetLang, text),
                stream = false,
                options = new
                {
                    temperature = 0.0,
                    top_p = 0.1,
                    repeat_penalty = 1.0,
                    num_predict = 512, // [FIX] Increased from 128 for multi-line support
                    seed = 42
                }
            };

            string url = !string.IsNullOrEmpty(_settings.OllamaApiUrl) ? _settings.OllamaApiUrl : "http://localhost:11434/api/generate";
            Debug.WriteLine($"[Translation] Ollama Request: model={request.model}, prompt=\"{request.prompt.Substring(0, Math.Min(30, request.prompt.Length))}...\"");

            var requestJson = JsonSerializer.Serialize(request);
            var firstTimeout = IsLikelySlowModel(model) ? TimeSpan.FromSeconds(28) : TimeSpan.FromSeconds(12);
            var retryTimeout = IsLikelySlowModel(model) ? TimeSpan.FromSeconds(45) : TimeSpan.FromSeconds(20);

            var response = await PostGenerateAsync(url, requestJson, firstTimeout, ct);
            if (response == null && !ct.IsCancellationRequested)
            {
                Debug.WriteLine($"[Translation] Primary request timed out, retrying with {retryTimeout.TotalSeconds:F0}s timeout...");
                response = await PostGenerateAsync(url, requestJson, retryTimeout, ct);
            }

            if (response == null)
            {
                Debug.WriteLine("[Translation] Request timed out, fallback to OCR text.");
                return text;
            }
            
            Debug.WriteLine($"[Translation] Ollama Response Status: {response.StatusCode}");
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Debug.WriteLine($"[Ollama Error] {response.StatusCode}: {errorContent}");
                
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    return $"Error: Model '{request.model}' not found.";
                }
                return $"Error: Ollama {response.StatusCode}";
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var resultText = doc.RootElement.GetProperty("response").GetString()?.Trim() ?? text;
            Debug.WriteLine($"[Translation] Ollama Raw Result: \"{resultText}\"");
            
            resultText = CleanupTranslationResult(resultText);
            
            if (string.IsNullOrWhiteSpace(resultText))
            {
                return text;
            }

            // Validation: If target is Chinese but result still has Japanese/Korean, it failed.
            if (!IsTranslationAcceptable(text, resultText, _settings.TargetLanguage))
            {
                Debug.WriteLine($"[Translation] Validation failed for result: \"{resultText}\". Retrying with forceful prompt...");
                var retried = await TranslateStrictRetryForCjkAsync(model, url, text, _settings.TargetLanguage, ct);
                if (!string.IsNullOrWhiteSpace(retried) && IsTranslationAcceptable(text, retried, _settings.TargetLanguage))
                {
                    resultText = retried;
                }
                else
                {
                    Debug.WriteLine("[Translation] Retry also failed or returned invalid result.");
                }
            }

            Debug.WriteLine($"[Translation] Final Result: {resultText}");
            return resultText;
        }
        catch (OperationCanceledException)
        {
            if (ct.IsCancellationRequested)
            {
                throw;
            }
            Debug.WriteLine("[Translation] Request timed out, fallback to OCR text.");
            return text;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Translation Error] {ex.Message}");
            return text;
        }
    }

    private static string BuildStrictTranslationPrompt(string sourceLang, string targetLang, string text)
    {
        return $@"You are an expert translator. 
Translate the following {sourceLang} text into {targetLang} accurately.

Rules:
1) Output ONLY the translated text.
2) NO explanations, NO quotes, NO original text.
3) Do NOT say ""Translation:"", ""Sure"", or ""Here is the translation"".
4) Preserve formatting, punctuation, and ORIGINAL LINE BREAKS.
5) If the text is already in {targetLang}, return it as is.

Input:
{text}

Output:";
    }

    private async Task<string> TranslateStrictRetryForCjkAsync(string model, string url, string text, TranslationLanguage target, CancellationToken ct)
    {
        string targetName = target switch
        {
            TranslationLanguage.TraditionalChinese => "Traditional Chinese (Taiwan)",
            TranslationLanguage.SimplifiedChinese => "Simplified Chinese",
            TranslationLanguage.English => "English",
            TranslationLanguage.Japanese => "Japanese",
            TranslationLanguage.Korean => "Korean",
            _ => "Traditional Chinese"
        };

        var retryRequest = new
        {
            model,
            prompt = $@"SYSTEM: You are a professional translator specializing in CJK languages.
USER: Translate the following text into natural, idiomatic {targetName}. 

MANDATORY RULES:
1) Output *ONLY* the translation. DO NOT provide any preamble, explanation, or notes.
2) DO NOT output any Japanese Hiragana or Katakana characters in the translation.
3) Use formal {targetName} vocabulary.
4) If the input is already {targetName}, return it unchanged.

Original Text:
{text}

Translation:",
            stream = false,
            options = new
            {
                temperature = 0.0,
                top_p = 0.1,
                num_predict = 512, // [FIX] Increased for retry as well
                seed = 42
            }
        };

        try
        {
            var json = JsonSerializer.Serialize(retryRequest);
            var response = await PostGenerateAsync(url, json, TimeSpan.FromSeconds(25), ct);
            if (response == null || !response.IsSuccessStatusCode) return string.Empty;
            var payload = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(payload);
            var result = doc.RootElement.GetProperty("response").GetString()?.Trim() ?? string.Empty;
            Debug.WriteLine($"[Translation] Retry Raw Result: \"{result}\"");
            return CleanupTranslationResult(result);
        }
        catch { return string.Empty; }
    }

    private static string CleanupTranslationResult(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var s = text.Trim();
        // Remove common wrapper artifacts from LLM output.
        s = s.Trim('"', '\'', '`');
        s = s.Replace("Translation:", "", StringComparison.OrdinalIgnoreCase).Trim();
        return s;
    }

    private async Task<string> TranslateStrictToEnglishRetryAsync(string model, string url, string text, CancellationToken ct = default)
    {
        try
        {
            var retryRequest = new
            {
                model,
                prompt = $@"Translate this text to English only.
Rules:
1) Output English only.
2) Do not keep original language.
3) If unreadable, output [unreadable].
Input:
{text}
Output:",
                stream = false,
                options = new
                {
                    temperature = 0.0,
                    top_p = 0.1,
                    repeat_penalty = 1.0,
                    num_predict = 64,
                    seed = 43
                }
            };

            var retryJsonBody = JsonSerializer.Serialize(retryRequest);
            var retryResponse = await PostGenerateAsync(url, retryJsonBody, TimeSpan.FromSeconds(20), ct);
            if (retryResponse == null) return string.Empty;
            if (!retryResponse.IsSuccessStatusCode) return string.Empty;

            var retryJson = await retryResponse.Content.ReadAsStringAsync();
            using var retryDoc = JsonDocument.Parse(retryJson);
            var retryText = retryDoc.RootElement.GetProperty("response").GetString()?.Trim() ?? string.Empty;
            return CleanupTranslationResult(retryText);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsLikelySlowModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;
        var m = model.ToLowerInvariant();
        return m.Contains("cloud") || m.Contains("vl") || m.Contains("235b");
    }

    private async Task<HttpResponseMessage?> PostGenerateAsync(string url, string jsonBody, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            reqCts.CancelAfter(timeout);
            var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            return await _httpClient.PostAsync(url, content, reqCts.Token);
        }
        catch (OperationCanceledException)
        {
            if (ct.IsCancellationRequested) throw;
            return null;
        }
    }

    private bool ShouldBypassTranslation(string text, TranslationLanguage target)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;

        int letterOrDigit = text.Count(char.IsLetterOrDigit);
        if (letterOrDigit == 0) return true;

        int cjkCount = text.Count(IsCjk);
        int latinCount = text.Count(ch => (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z'));
        int total = Math.Max(1, text.Count(ch => !char.IsWhiteSpace(ch)));

        double cjkRatio = (double)cjkCount / total;
        double latinRatio = (double)latinCount / total;

        bool res = target switch
        {
            TranslationLanguage.English => latinRatio > 0.8,
            TranslationLanguage.TraditionalChinese => cjkRatio > 0.8 && !text.Any(IsHiraganaKatakana) && !text.Any(IsHangul),
            TranslationLanguage.SimplifiedChinese => cjkRatio > 0.8 && !text.Any(IsHiraganaKatakana) && !text.Any(IsHangul),
            TranslationLanguage.Japanese => text.Any(IsHiraganaKatakana) || (cjkRatio > 0.8 && _settings.SourceLanguage == OCRLanguage.Japanese),
            TranslationLanguage.Korean => text.Any(IsHangul) || (cjkRatio > 0.8 && _settings.SourceLanguage == OCRLanguage.Korean),
            _ => false
        };
        Debug.WriteLine($"[Translation] ShouldBypass: {res} (CJK:{cjkRatio:F2}, Latin:{latinRatio:F2}, Target:{target})");
        return res;
    }

    private static bool ContainsCjk(string text)
    {
        return text.Any(IsCjk);
    }

    private bool IsTranslationAcceptable(string source, string translated, TranslationLanguage target)
    {
        if (string.IsNullOrWhiteSpace(translated)) return false;

        // Reject if target is not Japanese but result has Japanese Hiragana/Katakana
        if (target != TranslationLanguage.Japanese && translated.Any(IsHiraganaKatakana))
        {
            // Allowed if source also has it and it's a very short string (might be a logo/name kept)
            if (source.Any(IsHiraganaKatakana) && translated.Length > 2)
            {
                 // Check if it's mostly still Japanese
                 int kanaCount = translated.Count(IsHiraganaKatakana);
                 if (kanaCount > translated.Length / 4) return false;
            }
        }

        // For English target, reject outputs that are still mostly CJK or unchanged CJK.
        if (target == TranslationLanguage.English)
        {
            if (string.Equals(source.Trim(), translated.Trim(), StringComparison.Ordinal) && ContainsCjk(source))
            {
                return false;
            }

            int total = Math.Max(1, translated.Count(ch => !char.IsWhiteSpace(ch)));
            int latin = translated.Count(ch => (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z'));
            int cjk = translated.Count(IsCjk);
            double latinRatio = (double)latin / total;
            double cjkRatio = (double)cjk / total;
            if (latinRatio < 0.45 || cjkRatio > 0.2) return false;
        }

        return true;
    }

    private static string BuildTargetLanguageFallbackText(string sourceText, TranslationLanguage targetLanguage)
    {
        if (string.IsNullOrWhiteSpace(sourceText)) return string.Empty;
        var s = sourceText.Trim();
        if (s.Length == 0) return string.Empty;

        // Keep safe characters only.
        var filtered = new string(s.Where(ch =>
            ch != '\uFFFD' &&
            (char.IsLetterOrDigit(ch) || IsCjk(ch) || ch == ' ' || ch == '-' || ch == '_' || ch == ':' || ch == '.' || ch == '\n' || ch == '\r'))
            .ToArray()).Trim();

        if (string.IsNullOrWhiteSpace(filtered)) return GetLanguagePlaceholder(targetLanguage);

        int total = Math.Max(1, filtered.Count(ch => !char.IsWhiteSpace(ch)));
        int latin = filtered.Count(ch => (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z'));
        int cjk = filtered.Count(IsCjk);
        double latinRatio = (double)latin / total;
        double cjkRatio = (double)cjk / total;

        // Enforce target language style for fallback output.
        if (targetLanguage == TranslationLanguage.English)
        {
            // If not mostly latin, avoid showing source CJK/garbled text.
            if (latinRatio < 0.4) return "[unreadable]";
            return filtered;
        }

        if (targetLanguage == TranslationLanguage.TraditionalChinese || targetLanguage == TranslationLanguage.SimplifiedChinese)
        {
            if (cjkRatio < 0.3 && latinRatio > 0.6)
            {
                return targetLanguage == TranslationLanguage.TraditionalChinese ? "[?��?辨�?]" : "[?��?识别]";
            }
            return filtered;
        }

        // Japanese/Korean: if no corresponding script, give neutral placeholder.
        if (targetLanguage == TranslationLanguage.Japanese || targetLanguage == TranslationLanguage.Korean)
        {
            if (cjkRatio < 0.3 && latinRatio > 0.6) return GetLanguagePlaceholder(targetLanguage);
            return filtered;
        }

        return filtered;
    }

    private static string GetLanguagePlaceholder(TranslationLanguage targetLanguage)
    {
        return targetLanguage switch
        {
            TranslationLanguage.English => "[translation unavailable]",
            TranslationLanguage.TraditionalChinese => "[?��?辨�?]",
            TranslationLanguage.SimplifiedChinese => "[?��?识别]",
            TranslationLanguage.Japanese => "[?�読不能]",
            TranslationLanguage.Korean => "[?��? 불�?]",
            _ => "[translation unavailable]"
        };
    }

    private async Task<string> ForceTranslateTraditionalChineseToEnglishAsync(string text, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            string model = _settings.OllamaModel;
            if (string.IsNullOrWhiteSpace(model)) return string.Empty;

            string url = !string.IsNullOrEmpty(_settings.OllamaApiUrl) ? _settings.OllamaApiUrl : "http://localhost:11434/api/generate";
            var req = new
            {
                model,
                prompt = $@"Translate the following Traditional Chinese UI text into natural English.
Rules:
1) Output English only.
2) Keep concise UI wording.
3) If partially unreadable, still translate what you can.
Input:
{text}
Output:",
                stream = false,
                options = new
                {
                    temperature = 0.0,
                    top_p = 0.1,
                    num_predict = 64,
                    seed = 44
                }
            };

            var jsonBody = JsonSerializer.Serialize(req);
            var resp = await PostGenerateAsync(url, jsonBody, TimeSpan.FromSeconds(20), ct);
            if (resp == null || !resp.IsSuccessStatusCode) return string.Empty;
            var payload = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(payload);
            var result = doc.RootElement.GetProperty("response").GetString()?.Trim() ?? string.Empty;
            return CleanupTranslationResult(result);
        }
        catch
        {
            return string.Empty;
        }
    }

    private async Task<string> TranslateWithGeminiAsync(string text, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_settings.GeminiApiKey))
            {
                return "Error: Gemini API Key is missing. Please set it in settings.";
            }

            string model = _settings.GeminiModel?.Trim() ?? "";
            string apiKey = _settings.GeminiApiKey?.Trim() ?? "";
            
            if (string.IsNullOrWhiteSpace(model)) return "Error: Gemini Model is not set.";
            
            string targetLang = _settings.TargetLanguage switch
            {
                TranslationLanguage.TraditionalChinese => "Traditional Chinese (Taiwan)",
                TranslationLanguage.SimplifiedChinese => "Simplified Chinese",
                TranslationLanguage.English => "English",
                TranslationLanguage.Japanese => "Japanese",
                TranslationLanguage.Korean => "Korean",
                _ => "Traditional Chinese"
            };
            string sourceLang = ResolveSourceLanguageForPrompt(text);

            var prompt = BuildStrictTranslationPrompt(sourceLang, targetLang, text);
            
            var request = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new[]
                        {
                            new { text = prompt }
                        }
                    }
                },
                generationConfig = new
                {
                    temperature = 0.0,
                    topP = 0.95,
                    maxOutputTokens = 1024
                },
                // Add safety settings to be less restrictive for translation tasks
                safetySettings = new[]
                {
                    new { category = "HARM_CATEGORY_HARASSMENT", threshold = "BLOCK_NONE" },
                    new { category = "HARM_CATEGORY_HATE_SPEECH", threshold = "BLOCK_NONE" },
                    new { category = "HARM_CATEGORY_SEXUALLY_EXPLICIT", threshold = "BLOCK_NONE" },
                    new { category = "HARM_CATEGORY_DANGEROUS_CONTENT", threshold = "BLOCK_NONE" }
                }
            };

            string requestJson = JsonSerializer.Serialize(request);
            
            // Attempt v1beta first
            string url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";
            var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(url, content, ct);

            // Fallback to v1 if v1beta fails with 404
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                Debug.WriteLine($"[Gemini] v1beta for {model} returned 404, trying v1...");
                url = $"https://generativelanguage.googleapis.com/v1/models/{model}:generateContent?key={apiKey}";
                content = new StringContent(requestJson, Encoding.UTF8, "application/json");
                response = await _httpClient.PostAsync(url, content, ct);
            }

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                Debug.WriteLine($"[Gemini Error] {response.StatusCode}: {error}");
                
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    if (error.Contains("limit: 0"))
                    {
                        return "Error: Gemini Quota is 0. Try a different model (e.g. -exp) or check your API plan/region.";
                    }
                    return "Error: Gemini API Rate Limit Exceeded (429).";
                }
                
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    return $"Error: Gemini Model '{model}' not found (404) in both v1 and v1beta.";
                }

                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    return "Error: Gemini API Key invalid or restricted (403).";
                }
                
                return $"Error: Gemini API {response.StatusCode}";
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            
            if (doc.RootElement.TryGetProperty("candidates", out var candidates) && 
                candidates.GetArrayLength() > 0)
            {
                var candidate = candidates[0];
                if (candidate.TryGetProperty("content", out var resContent) &&
                    resContent.TryGetProperty("parts", out var parts) &&
                    parts.GetArrayLength() > 0)
                {
                    var resultText = parts[0].GetProperty("text").GetString()?.Trim() ?? text;
                    return CleanupTranslationResult(resultText);
                }
                
                // Check for safety finish reason
                if (candidate.TryGetProperty("finishReason", out var reason))
                {
                    return $"Error: Gemini blocked result (Reason: {reason.GetString()})";
                }
            }

            return "Error: Gemini returned empty result.";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Gemini Exception] {ex.Message}");
            return $"Error: Gemini call failed - {ex.Message}";
        }
    }

    public async Task<List<string>> GetAvailableModelsAsync()
    {
        if (_cachedModels.Count > 0 && DateTime.UtcNow - _modelsCacheAtUtc < TimeSpan.FromMinutes(2))
        {
            return new List<string>(_cachedModels);
        }

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
                _cachedModels = models;
                _modelsCacheAtUtc = DateTime.UtcNow;
                return new List<string>(_cachedModels);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Translation] Failed to fetch models: {ex.Message}");
        }
        return new List<string>();
    }
}
