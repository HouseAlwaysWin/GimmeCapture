using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Infrastructure;
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
    private const float RecognitionRetryConfidenceThreshold = 0.72f;
    private const float LowContrastThreshold = 64f;
    private static readonly bool SaveOcrDebugImages = false;

    private readonly IAppSettingsService _settingsService;
    private readonly OcrRuntimeService _ocrRuntimeService;
    private OCRLanguage? _currentOCRLanguage;
    private string? _leaseId;

    public PaddleOCREngine(AIResourceService aiResourceService, IAppSettingsService settingsService, OcrRuntimeService ocrRuntimeService)
    {
        ArgumentNullException.ThrowIfNull(aiResourceService);
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _ocrRuntimeService = ocrRuntimeService ?? throw new ArgumentNullException(nameof(ocrRuntimeService));
    }

    public async Task EnsureLoadedAsync(OCRLanguage lang, CancellationToken ct = default)
    {
        if (_leaseId == null)
        {
            _leaseId = _ocrRuntimeService.AcquireLease();
        }

        if (_currentOCRLanguage != lang)
        {
            Debug.WriteLine($"[OCR] Switching language to {lang}");
        }

        await _ocrRuntimeService.EnsureLoadedAsync(lang, ct);
        _currentOCRLanguage = lang;
    }

    public List<SKRectI> DetectText(SKBitmap bitmap)
    {
        var detSession = _ocrRuntimeService.DetectionSession;
        if (detSession == null) return new List<SKRectI>();

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
        FillDetectionTensor(resized, input, mean, std);

        string inputName = detSession.InputMetadata.Keys.AsValueEnumerable().FirstOrDefault() ?? "x";
        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(inputName, input) };
        using var outputs = detSession.Run(inputs);
        var outputTensor = outputs.AsValueEnumerable().First().AsTensor<float>();

        return FindBoxesFromMask(outputTensor, targetWidth, targetHeight, bitmap.Width, bitmap.Height);
    }

    public (string text, float confidence) RecognizeText(SKBitmap bitmap, SKRectI box, CancellationToken ct = default)
    {
        if (_ocrRuntimeService.RecognitionSession == null || box.Width <= 0 || box.Height <= 0) return ("", 0f);

        var expandedBox = ExpandRecognitionBox(box, bitmap.Width, bitmap.Height);
        using var cropped = new SKBitmap(expandedBox.Width, expandedBox.Height);
        using (var canvas = new SKCanvas(cropped))
        {
            canvas.DrawBitmap(bitmap, expandedBox, new SKRect(0, 0, expandedBox.Width, expandedBox.Height));
        }

        bool shouldInvert = AnalyzeLuminance(cropped) < 120;
        bool shouldEnhance = AnalyzeContrast(cropped) < LowContrastThreshold || box.Height < 28;

        const int targetHeight = 48;
        int textWidth = (int)MathF.Ceiling(cropped.Width * ((float)targetHeight / cropped.Height));
        textWidth = Math.Clamp(textWidth, 16, 1536);
        int paddedWidth = (textWidth + 31) / 32 * 32;

        var bestResult = EvaluateRecognitionCandidate(cropped, textWidth, paddedWidth, targetHeight, invert: false, enhanceContrast: false);

        if (shouldInvert)
        {
            bestResult = SelectBetterRecognitionResult(
                bestResult,
                EvaluateRecognitionCandidate(cropped, textWidth, paddedWidth, targetHeight, invert: true, enhanceContrast: false));
        }

        if (shouldEnhance || bestResult.confidence < RecognitionRetryConfidenceThreshold)
        {
            bestResult = SelectBetterRecognitionResult(
                bestResult,
                EvaluateRecognitionCandidate(cropped, textWidth, paddedWidth, targetHeight, invert: false, enhanceContrast: true));

            if (shouldInvert)
            {
                bestResult = SelectBetterRecognitionResult(
                    bestResult,
                    EvaluateRecognitionCandidate(cropped, textWidth, paddedWidth, targetHeight, invert: true, enhanceContrast: true));
            }
        }

        return bestResult;
    }

    private float AnalyzeLuminance(SKBitmap bitmap)
    {
        long totalLuminous = 0;
        for (int y = 0; y < bitmap.Height; y++)
        {
            var row = GetPixelRowSpan(bitmap, y);
            for (int x = 0; x < bitmap.Width; x++)
            {
                int offset = x * 4;
                byte blue = row[offset];
                byte green = row[offset + 1];
                byte red = row[offset + 2];
                totalLuminous += (long)(red * 0.2126f + green * 0.7152f + blue * 0.0722f);
            }
        }
        return totalLuminous / (float)(bitmap.Width * bitmap.Height);
    }

    private float AnalyzeContrast(SKBitmap bitmap)
    {
        byte min = byte.MaxValue;
        byte max = byte.MinValue;

        for (int y = 0; y < bitmap.Height; y++)
        {
            var row = GetPixelRowSpan(bitmap, y);
            for (int x = 0; x < bitmap.Width; x++)
            {
                int offset = x * 4;
                byte blue = row[offset];
                byte green = row[offset + 1];
                byte red = row[offset + 2];
                byte luminance = (byte)Math.Clamp((red * 0.2126f) + (green * 0.7152f) + (blue * 0.0722f), 0f, 255f);
                if (luminance < min) min = luminance;
                if (luminance > max) max = luminance;
            }
        }

        return max - min;
    }

    private SKBitmap PrepareTensorBitmap(SKBitmap cropped, int textWidth, int paddedWidth, int targetHeight, bool invert, bool enhanceContrast)
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

        // PaddleOCR's reference preprocessing uses linear interpolation. Cubic
        // resampling can add ringing around small anti-aliased CJK strokes.
        using var resized = cropped.Resize(
            new SKImageInfo(textWidth, targetHeight),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        if (resized != null) canvas.DrawBitmap(resized, 0, 0, paint);

        if (enhanceContrast)
        {
            EnhanceBitmapForOcr(bitmap);
        }

        return bitmap;
    }

    private (string text, float confidence) EvaluateRecognitionCandidate(
        SKBitmap cropped,
        int textWidth,
        int paddedWidth,
        int targetHeight,
        bool invert,
        bool enhanceContrast)
    {
        using var prepared = PrepareTensorBitmap(cropped, textWidth, paddedWidth, targetHeight, invert, enhanceContrast);
        if (SaveOcrDebugImages)
        {
            SaveDebugImage(prepared);
        }

        return RunRecognition(prepared);
    }

    private (string text, float confidence) SelectBetterRecognitionResult(
        (string text, float confidence) current,
        (string text, float confidence) candidate)
    {
        if (string.IsNullOrWhiteSpace(current.text))
        {
            return candidate;
        }

        return ScoreRecognitionResult(candidate) > ScoreRecognitionResult(current) + 0.75d
            ? candidate
            : current;
    }

    private double ScoreRecognitionResult((string text, float confidence) result)
    {
        if (string.IsNullOrWhiteSpace(result.text))
        {
            return double.NegativeInfinity;
        }

        int usefulCount = 0;
        int suspiciousCount = 0;
        foreach (char ch in result.text)
        {
            if (char.IsLetterOrDigit(ch)
                || (ch >= 0x4E00 && ch <= 0x9FFF)
                || (ch >= 0x3040 && ch <= 0x30FF)
                || (ch >= 0xAC00 && ch <= 0xD7AF))
            {
                usefulCount++;
            }
            else if (!char.IsWhiteSpace(ch) && !char.IsPunctuation(ch))
            {
                suspiciousCount++;
            }
        }

        double score = (result.confidence * 100d)
            + (Math.Min(usefulCount, 8) * 0.25d)
            - (suspiciousCount * 6d);
        score += _currentOCRLanguage switch
        {
            OCRLanguage.TraditionalChinese or OCRLanguage.SimplifiedChinese when ContainsCjk(result.text) => 1d,
            OCRLanguage.Japanese when ContainsJapaneseKana(result.text) || ContainsCjk(result.text) => 1d,
            OCRLanguage.Korean when ContainsHangul(result.text) => 1d,
            OCRLanguage.English when ContainsLatinLetter(result.text) => 1d,
            _ => 0d
        };

        return score;
    }

    private static void EnhanceBitmapForOcr(SKBitmap bitmap)
    {
        byte min = byte.MaxValue;
        byte max = byte.MinValue;

        for (int y = 0; y < bitmap.Height; y++)
        {
            var row = GetPixelRowSpan(bitmap, y);
            for (int x = 0; x < bitmap.Width; x++)
            {
                int offset = x * 4;
                byte blue = row[offset];
                byte green = row[offset + 1];
                byte red = row[offset + 2];
                byte luminance = (byte)Math.Clamp((red * 0.2126f) + (green * 0.7152f) + (blue * 0.0722f), 0f, 255f);
                if (luminance < min) min = luminance;
                if (luminance > max) max = luminance;
            }
        }

        float range = Math.Max(1f, max - min);
        // Reuse a single pooled row buffer instead of allocating one per row.
        int rowLength = bitmap.Width * 4;
        byte[] scratch = ArrayPool<byte>.Shared.Rent(rowLength);
        try
        {
            Span<byte> writableRow = scratch.AsSpan(0, rowLength);
            for (int y = 0; y < bitmap.Height; y++)
            {
                GetPixelRowSpan(bitmap, y).CopyTo(writableRow);
                for (int x = 0; x < bitmap.Width; x++)
                {
                    int offset = x * 4;
                    byte blue = writableRow[offset];
                    byte green = writableRow[offset + 1];
                    byte red = writableRow[offset + 2];
                    float luminance = (red * 0.2126f) + (green * 0.7152f) + (blue * 0.0722f);
                    float normalized = ((luminance - min) / range) * 255f;
                    byte value = (byte)Math.Clamp(MathF.Round(normalized), 0f, 255f);

                    writableRow[offset] = value;
                    writableRow[offset + 1] = value;
                    writableRow[offset + 2] = value;
                    writableRow[offset + 3] = 255;
                }

                WritePixelRow(bitmap, y, writableRow);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    internal static SKRectI ExpandRecognitionBox(SKRectI box, int bitmapWidth, int bitmapHeight)
    {
        int padX = Math.Max(2, (int)MathF.Ceiling(box.Height * 0.06f));
        // DB detection follows the visible strokes closely. Keeping that tight
        // crop makes small CJK glyphs fill the 48px recognizer input and
        // distorts their proportions, so retain more vertical line spacing.
        int padY = Math.Clamp((int)MathF.Ceiling(box.Height * 0.45f), 2, 7);

        return new SKRectI(
            Math.Max(0, box.Left - padX),
            Math.Max(0, box.Top - padY),
            Math.Min(bitmapWidth, box.Right + padX),
            Math.Min(bitmapHeight, box.Bottom + padY));
    }

    private (string text, float confidence) RunRecognition(SKBitmap tensorBitmap)
    {
        var recSession = _ocrRuntimeService.RecognitionSession;
        if (recSession == null) return ("", 0f);

        int h = tensorBitmap.Height;
        int w = tensorBitmap.Width;
        var input = new DenseTensor<float>(new[] { 1, 3, h, w });
        FillRecognitionTensor(tensorBitmap, input);

        string inputName = recSession.InputMetadata.Keys.AsValueEnumerable().FirstOrDefault() ?? "x";
        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(inputName, input) };
        using var outputs = recSession.Run(inputs);
        var outputTensor = outputs.AsValueEnumerable().First().AsTensor<float>();

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

                    TryEnqueueBlobNeighbor(cx, cy + 1, w, h, probMap, visited, queue);
                    TryEnqueueBlobNeighbor(cx, cy - 1, w, h, probMap, visited, queue);
                    TryEnqueueBlobNeighbor(cx + 1, cy, w, h, probMap, visited, queue);
                    TryEnqueueBlobNeighbor(cx - 1, cy, w, h, probMap, visited, queue);
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

        return boxes.AsValueEnumerable().Any() ? boxes : new List<SKRectI> { new SKRectI(0, 0, origW, origH) };
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
        FillDetProbabilityMap(mask, probMap, h, w);
        return true;
    }

    private static unsafe void FillDetectionTensor(SKBitmap bitmap, DenseTensor<float> input, ReadOnlySpan<float> mean, ReadOnlySpan<float> std)
    {
        for (int y = 0; y < bitmap.Height; y++)
        {
            var row = GetPixelRowSpan(bitmap, y);
            for (int x = 0; x < bitmap.Width; x++)
            {
                int offset = x * 4;
                float blue = row[offset] / 255.0f;
                float green = row[offset + 1] / 255.0f;
                float red = row[offset + 2] / 255.0f;
                input[0, 0, y, x] = (red - mean[0]) / std[0];
                input[0, 1, y, x] = (green - mean[1]) / std[1];
                input[0, 2, y, x] = (blue - mean[2]) / std[2];
            }
        }
    }

    private static unsafe void FillRecognitionTensor(SKBitmap bitmap, DenseTensor<float> input)
    {
        for (int y = 0; y < bitmap.Height; y++)
        {
            var row = GetPixelRowSpan(bitmap, y);
            for (int x = 0; x < bitmap.Width; x++)
            {
                int offset = x * 4;
                float blue = row[offset] / 255.0f;
                float green = row[offset + 1] / 255.0f;
                float red = row[offset + 2] / 255.0f;
                input[0, 0, y, x] = (red - 0.5f) / 0.5f;
                input[0, 1, y, x] = (green - 0.5f) / 0.5f;
                input[0, 2, y, x] = (blue - 0.5f) / 0.5f;
            }
        }
    }

    private static void FillDetProbabilityMap(Tensor<float> mask, float[,] probMap, int h, int w)
    {
        switch (mask.Dimensions.Length)
        {
            case 4 when mask.Dimensions[1] == 1:
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        probMap[y, x] = ToProbability(mask[0, 0, y, x]);
                return;
            case 4 when mask.Dimensions[3] == 1:
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        probMap[y, x] = ToProbability(mask[0, y, x, 0]);
                return;
            case 4:
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        probMap[y, x] = ToProbability(mask[0, 0, y, x]);
                return;
            case 3 when mask.Dimensions[0] == 1:
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        probMap[y, x] = ToProbability(mask[0, y, x]);
                return;
            case 3:
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        probMap[y, x] = ToProbability(mask[y, x, 0]);
                return;
            case 2:
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        probMap[y, x] = ToProbability(mask[y, x]);
                return;
        }
    }

    private static unsafe ReadOnlySpan<byte> GetPixelRowSpan(SKBitmap bitmap, int row)
    {
        byte* rowPtr = (byte*)bitmap.GetPixels() + (row * bitmap.RowBytes);
        return new ReadOnlySpan<byte>(rowPtr, bitmap.Width * 4);
    }

    private static unsafe void WritePixelRow(SKBitmap bitmap, int row, ReadOnlySpan<byte> data)
    {
        byte* rowPtr = (byte*)bitmap.GetPixels() + (row * bitmap.RowBytes);
        data.CopyTo(new Span<byte>(rowPtr, bitmap.Width * 4));
    }

    private static void TryEnqueueBlobNeighbor(int nx, int ny, int width, int height, float[,] probMap, bool[,] visited, Queue<(int X, int Y)> queue)
    {
        if (nx >= 0 && nx < width && ny >= 0 && ny < height && !visited[ny, nx] && probMap[ny, nx] > DetBoxThreshold)
        {
            visited[ny, nx] = true;
            queue.Enqueue((nx, ny));
        }
    }

    private static float ToProbability(float value) => (value >= 0f && value <= 1f) ? value : 1f / (1f + MathF.Exp(-value));

    private (string text, float confidence) DecodeCTCAuto(Tensor<float> tensor)
    {
        int d1 = tensor.Dimensions[1], d2 = tensor.Dimensions[2];
        var dictCount = _ocrRuntimeService.Dictionary.Count;

        // Which axis is the class dimension is fixed per model, so decode only the plausible orientation
        // instead of always decoding both (a seqLen×classCount scan over a ~6000-entry dictionary per box).
        bool ntcP = Math.Abs(dictCount - d2) <= 8;
        bool nctP = Math.Abs(dictCount - d1) <= 8;

        if (ntcP && !nctP) return DecodeCTC(tensor, d1, d2, true);
        if (!ntcP && nctP) return DecodeCTC(tensor, d2, d1, false);

        // Ambiguous (both or neither plausible): fall back to decoding both and taking the higher confidence.
        var ntc = DecodeCTC(tensor, d1, d2, true);
        var nct = DecodeCTC(tensor, d2, d1, false);
        return nct.confidence > ntc.confidence ? nct : ntc;
    }

    private (string text, float confidence) DecodeCTC(Tensor<float> tensor, int seqLen, int classCount, bool isNTC)
    {
        var sb = new StringBuilder();
        var dict = _ocrRuntimeService.Dictionary;
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

            if (maxIdx > 0 && maxIdx != prevIdx && maxIdx < dict.Count)
            {
                sb.Append(dict[maxIdx]);
                totalConf += ToProbability(maxVal);
                count++;
            }
            prevIdx = maxIdx;
        }
        return (sb.ToString(), count > 0 ? totalConf / count : 0f);
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
        catch (Exception ex)
        {
            AppLog.Warning("PaddleOCR.SaveDebugImage", ex);
        }
    }

    public void Dispose()
    {
        if (_leaseId != null)
        {
            _ocrRuntimeService.ReleaseLease(_leaseId);
            _leaseId = null;
        }
    }

    private static bool ContainsLatinLetter(string text) =>
        !string.IsNullOrWhiteSpace(text)
        && text.AsValueEnumerable().Any(static c => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'));

    private static bool ContainsHangul(string text) =>
        !string.IsNullOrWhiteSpace(text)
        && text.AsValueEnumerable().Any(static c => c >= '\uAC00' && c <= '\uD7AF');

    private static bool ContainsCjk(string text) =>
        !string.IsNullOrWhiteSpace(text)
        && text.AsValueEnumerable().Any(static c => c >= '\u4E00' && c <= '\u9FFF');

    private static bool ContainsJapaneseKana(string text) =>
        !string.IsNullOrWhiteSpace(text)
        && text.AsValueEnumerable().Any(static c => c >= '\u3040' && c <= '\u30FF');
}
