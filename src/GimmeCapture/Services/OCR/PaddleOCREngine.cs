using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Interfaces;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;
using GimmeCapture.Services.Core;

namespace GimmeCapture.Services.OCR;

public class PaddleOCREngine : IOCREngine
{
    private const float DetBoxThreshold = 0.3f;
    private const float DetMinAreaRatio = 0.00005f;
    private const int DetMaxBoxes = 256;
    private static readonly bool SaveOcrDebugImages = false;

    private readonly AIResourceService _aiResourceService;
    private readonly AppSettingsService _settingsService;
    private InferenceSession? _detSession;
    private InferenceSession? _recSession;
    private OCRLanguage? _currentOCRLanguage;
    private List<string> _dict = new();

    public PaddleOCREngine(AIResourceService aiResourceService, AppSettingsService settingsService)
    {
        _aiResourceService = aiResourceService ?? throw new ArgumentNullException(nameof(aiResourceService));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
    }

    public async Task EnsureLoadedAsync(OCRLanguage lang, CancellationToken ct = default)
    {
        if (_currentOCRLanguage == lang && _detSession != null && _recSession != null) return;

        if (_currentOCRLanguage != lang)
        {
            _detSession?.Dispose(); _detSession = null;
            _recSession?.Dispose(); _recSession = null;
            Debug.WriteLine($"[OCR] Switching language to {lang}");
        }

        await _aiResourceService.EnsureOCRAsync();
        var paths = _aiResourceService.GetOCRPaths(lang);

        var options = new SessionOptions();
        options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;

        try { options.AppendExecutionProvider_CUDA(0); } catch { }
        try { options.AppendExecutionProvider_DML(0); } catch { }

        _detSession = new InferenceSession(paths.Det, options);
        _recSession = new InferenceSession(paths.Rec, options);
        _currentOCRLanguage = lang;

        _dict = LoadDictionaryWithEncodingFallback(paths.Dict);
        _dict.Insert(0, ""); // CTC Blank
    }

    public List<SKRectI> DetectText(SKBitmap bitmap)
    {
        if (_detSession == null) return new List<SKRectI>();

        int limitSideLen = 1280;
        int w = bitmap.Width;
        int h = bitmap.Height;
        float ratio = 1.0f;
        if (Math.Max(h, w) > limitSideLen)
        {
            ratio = h > w ? (float)limitSideLen / h : (float)limitSideLen / w;
        }
        int targetWidth = (int)(w * ratio);
        int targetHeight = (int)(h * ratio);
        targetWidth = (targetWidth + 31) / 32 * 32;
        targetHeight = (targetHeight + 31) / 32 * 32;

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

        string inputName = _detSession.InputMetadata.Keys.FirstOrDefault() ?? "x";
        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(inputName, input) };
        using var outputs = _detSession.Run(inputs);
        var outputTensor = outputs.First().AsTensor<float>();

        return FindBoxesFromMask(outputTensor, targetWidth, targetHeight, bitmap.Width, bitmap.Height);
    }

    public (string text, float confidence) RecognizeText(SKBitmap bitmap, SKRectI box, CancellationToken ct = default)
    {
        if (_recSession == null || box.Width <= 0 || box.Height <= 0) return ("", 0f);

        using var cropped = new SKBitmap(box.Width, box.Height);
        using (var canvas = new SKCanvas(cropped))
        {
            canvas.DrawBitmap(bitmap, box, new SKRect(0, 0, box.Width, box.Height));
        }

        bool shouldInvert = AnalyzeLuminance(cropped) < 120;

        int targetHeight = 48;
        int textWidth = (int)(box.Width * ((float)targetHeight / box.Height));
        textWidth = Math.Clamp(textWidth, 16, 1536);
        int paddedWidth = (textWidth + 31) / 32 * 32;

        using var normalBitmap = PrepareTensorBitmap(cropped, textWidth, paddedWidth, targetHeight, false);
        var normalResult = RunRecognition(normalBitmap);
        var bestResult = normalResult;

        if (shouldInvert)
        {
            using var invertedBitmap = PrepareTensorBitmap(cropped, textWidth, paddedWidth, targetHeight, true);
            var invertedResult = RunRecognition(invertedBitmap);
            if (invertedResult.confidence > bestResult.confidence || (string.IsNullOrWhiteSpace(bestResult.text) && !string.IsNullOrWhiteSpace(invertedResult.text)))
            {
                bestResult = invertedResult;
            }
        }

        if (SaveOcrDebugImages) SaveDebugImage(normalBitmap);

        return bestResult;
    }

    private float AnalyzeLuminance(SKBitmap bitmap)
    {
        long totalLuminous = 0;
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                var color = bitmap.GetPixel(x, y);
                totalLuminous += (long)(color.Red * 0.2126f + color.Green * 0.7152f + color.Blue * 0.0722f);
            }
        return totalLuminous / (float)(bitmap.Width * bitmap.Height);
    }

    private SKBitmap PrepareTensorBitmap(SKBitmap cropped, int textWidth, int paddedWidth, int targetHeight, bool invert)
    {
        var bitmap = new SKBitmap(paddedWidth, targetHeight);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);

        using var paint = new SKPaint();
        if (invert)
        {
            paint.ColorFilter = SKColorFilter.CreateColorMatrix(new float[]
            {
                -1,  0,  0, 0, 255,
                 0, -1,  0, 0, 255,
                 0,  0, -1, 0, 255,
                 0,  0,  0, 1, 0
            });
        }

        using var resized = cropped.Resize(new SKImageInfo(textWidth, targetHeight), new SKSamplingOptions(SKCubicResampler.Mitchell));
        if (resized != null) canvas.DrawBitmap(resized, 0, 0, paint);
        
        return bitmap;
    }

    private (string text, float confidence) RunRecognition(SKBitmap tensorBitmap)
    {
        if (_recSession == null) return ("", 0f);

        int h = tensorBitmap.Height;
        int w = tensorBitmap.Width;
        var input = new DenseTensor<float>(new[] { 1, 3, h, w });
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                var color = tensorBitmap.GetPixel(x, y);
                input[0, 0, y, x] = (color.Red / 255.0f - 0.5f) / 0.5f;
                input[0, 1, y, x] = (color.Green / 255.0f - 0.5f) / 0.5f;
                input[0, 2, y, x] = (color.Blue / 255.0f - 0.5f) / 0.5f;
            }
        }

        string inputName = _recSession.InputMetadata.Keys.FirstOrDefault() ?? "x";
        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(inputName, input) };
        using var outputs = _recSession.Run(inputs);
        var outputTensor = outputs.First().AsTensor<float>();

        return DecodeCTCAuto(outputTensor);
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
                if (visited[y, x] || probMap[y, x] <= DetBoxThreshold) continue;

                int minX = x, maxX = x, minY = y, maxY = y;
                int area = 0;
                var queue = new Queue<(int X, int Y)>();
                queue.Enqueue((x, y));
                visited[y, x] = true;

                while (queue.Count > 0)
                {
                    var (cx, cy) = queue.Dequeue();
                    area++;
                    minX = Math.Min(minX, cx); maxX = Math.Max(maxX, cx);
                    minY = Math.Min(minY, cy); maxY = Math.Max(maxY, cy);

                    int[] dx = { 0, 0, 1, -1 };
                    int[] dy = { 1, -1, 0, 0 };
                    for (int i = 0; i < 4; i++)
                    {
                        int nx = cx + dx[i], ny = cy + dy[i];
                        if (nx >= 0 && nx < w && ny >= 0 && ny < h && !visited[ny, nx] && probMap[ny, nx] > DetBoxThreshold)
                        {
                            visited[ny, nx] = true;
                            queue.Enqueue((nx, ny));
                        }
                    }
                }

                int blobW = maxX - minX + 1, blobH = maxY - minY + 1;
                if (area < minBlobArea || blobW < 3 || blobH < 3) continue;

                float expansion = (2 * (blobW + blobH)) > 0 ? area * 1.6f / (2 * (blobW + blobH)) : 0f;
                int rectX = (int)((minX - expansion) * scaleX);
                int rectY = (int)((minY - expansion) * scaleY);
                int rectW = (int)((blobW + 2 * expansion) * scaleX);
                int rectH = (int)((blobH + 2 * expansion) * scaleY);

                if (rectW > 6 && rectH > 6)
                {
                    boxes.Add(new SKRectI(Math.Max(0, rectX), Math.Max(0, rectY), Math.Min(origW, rectX + rectW), Math.Min(origH, rectY + rectH)));
                    if (boxes.Count >= DetMaxBoxes) break;
                }
            }
            if (boxes.Count >= DetMaxBoxes) break;
        }

        return boxes.Any() ? boxes : new List<SKRectI> { new SKRectI(0, 0, origW, origH) };
    }

    private bool TryBuildDetProbabilityMap(Tensor<float> mask, out float[,] probMap, out int h, out int w)
    {
        probMap = new float[0, 0]; h = 0; w = 0;
        if (mask.Dimensions.Length < 2) return false;

        if (mask.Dimensions.Length == 4) // NCHW or NHWC
        {
            if (mask.Dimensions[1] == 1) { h = mask.Dimensions[2]; w = mask.Dimensions[3]; }
            else if (mask.Dimensions[3] == 1) { h = mask.Dimensions[1]; w = mask.Dimensions[2]; }
            else { h = mask.Dimensions[2]; w = mask.Dimensions[3]; }
        }
        else if (mask.Dimensions.Length == 3)
        {
            if (mask.Dimensions[0] == 1) { h = mask.Dimensions[1]; w = mask.Dimensions[2]; }
            else { h = mask.Dimensions[0]; w = mask.Dimensions[1]; }
        }
        else if (mask.Dimensions.Length == 2) { h = mask.Dimensions[0]; w = mask.Dimensions[1]; }

        if (h == 0 || w == 0) return false;
        probMap = new float[h, w];
        float[] buffer = mask.ToArray();
        for (int i = 0; i < h; i++)
            for (int j = 0; j < w; j++)
                probMap[i, j] = ToProbability(buffer[i * w + j]);
        return true;
    }

    private float ToProbability(float value) => (value >= 0f && value <= 1f) ? value : 1f / (1f + MathF.Exp(-value));

    private (string text, float confidence) DecodeCTCAuto(Tensor<float> tensor)
    {
        int d1 = tensor.Dimensions[1], d2 = tensor.Dimensions[2];
        var ntc = DecodeCTC(tensor, d1, d2, true);
        var nct = DecodeCTC(tensor, d2, d1, false);

        bool ntcP = _dict.Count <= d2 + 500 && _dict.Count >= d2 - 500;
        bool nctP = _dict.Count <= d1 + 500 && _dict.Count >= d1 - 500;

        if (ntcP && !nctP) return ntc;
        if (!ntcP && nctP) return nct;
        return nct.text.Length > ntc.text.Length ? nct : (ntc.text.Length > nct.text.Length ? ntc : (nct.confidence > ntc.confidence ? nct : ntc));
    }

    private (string text, float confidence) DecodeCTC(Tensor<float> tensor, int seqLen, int classCount, bool isNTC)
    {
        var sb = new StringBuilder();
        int prevIdx = -1;
        float totalConf = 0f;
        int count = 0;

        for (int t = 0; t < seqLen; t++)
        {
            int maxIdx = 0;
            float maxVal = isNTC ? tensor[0, t, 0] : tensor[0, 0, t];
            for (int c = 1; c < classCount; c++)
            {
                float v = isNTC ? tensor[0, t, c] : tensor[0, c, t];
                if (v > maxVal) { maxVal = v; maxIdx = c; }
            }

            if (maxIdx > 0 && maxIdx != prevIdx && maxIdx < _dict.Count)
            {
                sb.Append(_dict[maxIdx]);
                totalConf += ToProbability(maxVal);
                count++;
            }
            prevIdx = maxIdx;
        }
        return (sb.ToString(), count > 0 ? totalConf / count : 0f);
    }

    private List<string> LoadDictionaryWithEncodingFallback(string path)
    {
        try { return File.ReadAllLines(path, Encoding.UTF8).ToList(); }
        catch { return File.ReadAllLines(path, Encoding.GetEncoding("GBK")).ToList(); }
    }

    private void SaveDebugImage(SKBitmap bitmap)
    {
        try
        {
            string dir = Path.Combine(_settingsService.BaseDataDirectory, "OCR_Debug");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"rec_{DateTime.Now:HHmmss_fff}_{Guid.NewGuid().ToString()[..4]}.png");
            using var img = SKImage.FromBitmap(bitmap);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(file, data.ToArray());
        }
        catch { }
    }

    public void Dispose()
    {
        _detSession?.Dispose();
        _recSession?.Dispose();
    }
}
