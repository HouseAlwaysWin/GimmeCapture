using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace GimmeCapture.Services.Core.AI;

public class BackgroundRemovalService : IDisposable
{
    private InferenceSession? _session;
    private readonly AIResourceService _aiResourceService;
    private readonly AIPathService _pathService;
    private bool _isInitialized = false;


    public BackgroundRemovalService(AIResourceService aiResourceService, AIPathService pathService)
    {
        _aiResourceService = aiResourceService;
        _pathService = pathService;
        
        AIResourceService.RequestGlobalUnload += HandleGlobalUnload;
    }

    private void HandleGlobalUnload()
    {
        Dispose();
    }

    public async Task InitializeAsync()
    {
        if (_isInitialized) return;

        // Ensure we load the native libraries from the custom path
        _aiResourceService.SetupNativeResolvers();

        var baseDir = _aiResourceService.GetAIResourcesPath();
        var modelPath = _pathService.GetAICoreModelPath();

        if (!File.Exists(modelPath))
            throw new FileNotFoundException("AI Model not found. Please download it first.");

        await Task.Run(() =>
        {
            try
            {
                var options = new SessionOptions();
                bool cudaSuccess = false;
                try
                {
                    // Try CUDA first (4070 Super)
                    options.AppendExecutionProvider_CUDA(0);
                    cudaSuccess = true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"CUDA not available, falling back to CPU: {ex.Message}");
                }
                
                try
                {
                     _session = new InferenceSession(modelPath, options);
                }
                catch (Exception ex) when (cudaSuccess)
                {
                    // If CUDA was appended but session creation failed (e.g. missing cuDNN), try again with CPU
                     System.Diagnostics.Debug.WriteLine($"Session creation with CUDA failed: {ex.Message}. Retrying with CPU.");
                     options = new SessionOptions(); // Reset options
                     _session = new InferenceSession(modelPath, options);
                }
                
                _isInitialized = true;
            }
            catch (TypeInitializationException ex)
            {
                // Most common error: Missing Native DLL or CUDA dependencies
                var inner = ex.InnerException?.Message ?? "No inner exception";
                throw new Exception($"ONNX Runtime failed to initialize. Make sure you have the required native libraries and CUDA 12.x/cuDNN 9.x (if using GPU). \nInner Error: {inner}", ex);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to initialize AI model: {ex.Message}", ex);
            }
        });
    }


    public async Task<byte[]> RemoveBackgroundAsync(byte[] imageBytes, Avalonia.Rect? selectionRect = null)
    {
        if (!_isInitialized) await InitializeAsync();
        if (_session == null) throw new InvalidOperationException("AI Session not initialized.");

        return await Task.Run(() =>
        {
            using var original = SKBitmap.Decode(imageBytes);
            if (original == null) return imageBytes;

            SKBitmap targetBmp = original;
            SKRectI? roi = null;

            if (selectionRect.HasValue && selectionRect.Value.Width > 1 && selectionRect.Value.Height > 1)
            {
                // Convert logical Rect to pixel Rect using rounding for better accuracy
                int x = (int)Math.Round(Math.Max(0, selectionRect.Value.X));
                int y = (int)Math.Round(Math.Max(0, selectionRect.Value.Y));
                int w = (int)Math.Round(Math.Min(original.Width - x, selectionRect.Value.Width));
                int h = (int)Math.Round(Math.Min(original.Height - y, selectionRect.Value.Height));
                roi = new SKRectI(x, y, x + w, y + h);

                // Create cropped version
                targetBmp = new SKBitmap(w, h);
                original.ExtractSubset(targetBmp, roi.Value);
            }

            byte[] processedBytes;
            SKColor bgColor;
            
            // Try edge-based solid background detection first
            if (IsSolidBackground(targetBmp, out bgColor))
            {
                System.Diagnostics.Debug.WriteLine($"Solid background detected via edges ({bgColor}). Using Manual Removal.");
                processedBytes = RemoveSolidBackground(targetBmp, bgColor);
            }
            // Try corner-based detection as fallback (better for cropped regions where logo touches edges)
            else if (TryGetCornerBackgroundColor(targetBmp, out bgColor))
            {
                System.Diagnostics.Debug.WriteLine($"Solid background detected via corners ({bgColor}). Using Manual Removal.");
                processedBytes = RemoveSolidBackground(targetBmp, bgColor);
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("Complex background detected. Using AI Removal.");
                var session = _session;
                processedBytes = RunAIInference(targetBmp, session!);
            }

            if (roi.HasValue)
            {
                using var processedBmp = SKBitmap.Decode(processedBytes);
                
                // We create a new bitmap for the result to ensure transparency is handled correctly
                var resultBmp = new SKBitmap(original.Width, original.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
                using (var canvas = new SKCanvas(resultBmp))
                {
                    canvas.Clear(SKColors.Transparent);
                    canvas.DrawBitmap(original, 0, 0);
                    // Use Src blend mode to replace the ROI with processed transparent version
                    using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
                    canvas.DrawBitmap(processedBmp, roi.Value.Left, roi.Value.Top, paint);
                }

                if (targetBmp != original) targetBmp.Dispose();

                using var image = SKImage.FromBitmap(resultBmp);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                resultBmp.Dispose();
                return data.ToArray();
            }
            else
            {
                return processedBytes;
            }
        });
    }

    internal static bool IsSolidBackground(SKBitmap bmp, out SKColor bgColor)
    {
        bgColor = SKColors.Empty;
        if (bmp.Width < 50 || bmp.Height < 50) return false;

        const int step = 10;
        SampleAccumulator accumulator = default;
        int totalSamples = 0;

        for (int x = 0; x < bmp.Width; x += step)
        {
            totalSamples++;
            TryAccumulateOpaquePixel(bmp, x, 0, ref accumulator);
            totalSamples++;
            TryAccumulateOpaquePixel(bmp, x, bmp.Height - 1, ref accumulator);
        }

        for (int y = 0; y < bmp.Height; y += step)
        {
            totalSamples++;
            TryAccumulateOpaquePixel(bmp, 0, y, ref accumulator);
            totalSamples++;
            TryAccumulateOpaquePixel(bmp, bmp.Width - 1, y, ref accumulator);
        }

        if (totalSamples == 0 || accumulator.Count == 0) return false;
        if ((double)accumulator.Count / totalSamples < 0.8) return false;

        double avgR = (double)accumulator.SumR / accumulator.Count;
        double avgG = (double)accumulator.SumG / accumulator.Count;
        double avgB = (double)accumulator.SumB / accumulator.Count;

        double sumSqDiff = 0;
        for (int x = 0; x < bmp.Width; x += step)
        {
            AccumulateColorVariance(bmp, x, 0, avgR, avgG, avgB, ref sumSqDiff);
            AccumulateColorVariance(bmp, x, bmp.Height - 1, avgR, avgG, avgB, ref sumSqDiff);
        }

        for (int y = 0; y < bmp.Height; y += step)
        {
            AccumulateColorVariance(bmp, 0, y, avgR, avgG, avgB, ref sumSqDiff);
            AccumulateColorVariance(bmp, bmp.Width - 1, y, avgR, avgG, avgB, ref sumSqDiff);
        }

        double meanSqDiff = sumSqDiff / accumulator.Count;
        double stdDev = Math.Sqrt(meanSqDiff);

        System.Diagnostics.Debug.WriteLine($"[BgDetection] Edge StdDev: {stdDev:F2} (Threshold: 15.0)");

        // Thresholds:
        // A truly solid digital image (mspaint) has StdDev ~ 0.
        // A clean screenshot might have StdDev < 5 (compression artifacts).
        // A photo of a wall/curtain (even if it looks solid) will often have StdDev > 20 due to shadows/noise.
        
        if (stdDev < 15.0) 
        {
            bgColor = new SKColor((byte)avgR, (byte)avgG, (byte)avgB);
            System.Diagnostics.Debug.WriteLine($"[BgDetection] Solid background confirmed. Color: {bgColor}");
            return true;
        }

        return false;
    }

    private static bool ColorsAreSimilar(SKColor a, SKColor b, int tolerance)
    {
        return Math.Abs(a.Red - b.Red) <= tolerance &&
               Math.Abs(a.Green - b.Green) <= tolerance &&
               Math.Abs(a.Blue - b.Blue) <= tolerance;
    }

    /// <summary>
    /// Alternative background detection that samples from image corners with a small radius.
    /// More reliable than edge-only sampling when the image is cropped close to the subject.
    /// </summary>
    internal static bool TryGetCornerBackgroundColor(SKBitmap bmp, out SKColor bgColor)
    {
        bgColor = SKColors.Empty;
        if (bmp.Width < 20 || bmp.Height < 20) return false;

        int sampleSize = 5;
        Span<SKColor> opaqueSamples = stackalloc SKColor[sampleSize * sampleSize * 4];
        int totalSamples = 0;
        int opaqueCount = 0;

        CollectCornerSamples(bmp, 0, Math.Min(sampleSize, bmp.Width), 0, Math.Min(sampleSize, bmp.Height), opaqueSamples, ref totalSamples, ref opaqueCount);
        CollectCornerSamples(bmp, Math.Max(0, bmp.Width - sampleSize), bmp.Width, 0, Math.Min(sampleSize, bmp.Height), opaqueSamples, ref totalSamples, ref opaqueCount);
        CollectCornerSamples(bmp, 0, Math.Min(sampleSize, bmp.Width), Math.Max(0, bmp.Height - sampleSize), bmp.Height, opaqueSamples, ref totalSamples, ref opaqueCount);
        CollectCornerSamples(bmp, Math.Max(0, bmp.Width - sampleSize), bmp.Width, Math.Max(0, bmp.Height - sampleSize), bmp.Height, opaqueSamples, ref totalSamples, ref opaqueCount);

        if (totalSamples == 0 || opaqueCount < totalSamples * 0.7) return false;

        Span<SKColor> groupColors = stackalloc SKColor[opaqueSamples.Length];
        Span<int> groupCounts = stackalloc int[opaqueSamples.Length];
        int groupCount = 0;

        for (int i = 0; i < opaqueCount; i++)
        {
            SKColor sample = opaqueSamples[i];
            int matchIndex = -1;
            for (int groupIndex = 0; groupIndex < groupCount; groupIndex++)
            {
                if (ColorsAreSimilar(sample, groupColors[groupIndex], 30))
                {
                    matchIndex = groupIndex;
                    break;
                }
            }

            if (matchIndex < 0)
            {
                groupColors[groupCount] = sample;
                groupCounts[groupCount] = 1;
                groupCount++;
                continue;
            }

            groupCounts[matchIndex]++;
        }

        if (groupCount == 0) return false;

        int dominantIndex = 0;
        for (int i = 1; i < groupCount; i++)
        {
            if (groupCounts[i] > groupCounts[dominantIndex])
            {
                dominantIndex = i;
            }
        }

        double ratio = (double)groupCounts[dominantIndex] / opaqueCount;
        System.Diagnostics.Debug.WriteLine($"[CornerBg] Dominant color ratio: {ratio:P0}, Count: {groupCounts[dominantIndex]}/{opaqueCount}");

        if (ratio >= 0.6)
        {
            bgColor = groupColors[dominantIndex];
            System.Diagnostics.Debug.WriteLine($"[CornerBg] Background detected: {bgColor}");
            return true;
        }

        return false;
    }

    internal static byte[] RemoveSolidBackground(SKBitmap original, SKColor bgColor)
    {
        using var result = new SKBitmap(original.Width, original.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        const int tolerance = 60;

        for (int y = 0; y < original.Height; y++)
        {
            ReadOnlySpan<byte> srcRow = GetPixelRowSpan(original, y);
            Span<byte> dstRow = GetPixelRowSpan(result, y);
            for (int offset = 0; offset < srcRow.Length; offset += 4)
            {
                byte blue = srcRow[offset];
                byte green = srcRow[offset + 1];
                byte red = srcRow[offset + 2];
                byte alpha = srcRow[offset + 3];

                dstRow[offset] = blue;
                dstRow[offset + 1] = green;
                dstRow[offset + 2] = red;
                dstRow[offset + 3] = ColorsAreSimilar(new SKColor(red, green, blue, alpha), bgColor, tolerance) ? (byte)0 : alpha;
            }
        }

        using var image = SKImage.FromBitmap(result);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private byte[] RunAIInference(SKBitmap original, InferenceSession session)
    {
        // 1. Preprocess (Resize to 320x320 and Normalize)
        // Use High quality (Cubic) for better detail preservation -> Reverted to Linear for safety
        int inputSize = 320;
        using var resized = original.Resize(new SKImageInfo(inputSize, inputSize), new SKSamplingOptions(SKFilterMode.Linear));
        
        var inputTensor = ExtractPixels(resized);

        // 2. Inference
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input.1", inputTensor)
        };

        using var results = session.Run(inputs);
        var outputTensor = results.AsValueEnumerable().First().AsTensor<float>();

        // 3. Postprocess
        float minVal, maxVal;
        using var mask = ProcessMask(outputTensor, original.Width, original.Height, out minVal, out maxVal);
        
        // Apply mask to original
        return ApplyMask(original, mask, minVal, maxVal);
    }

    internal static DenseTensor<float> ExtractPixels(SKBitmap bitmap)
    {
        var tensor = new DenseTensor<float>(new[] { 1, 3, 320, 320 });
        for (int y = 0; y < 320; y++)
        {
            ReadOnlySpan<byte> row = GetPixelRowSpan(bitmap, y);
            for (int x = 0; x < 320; x++)
            {
                int offset = x * 4;
                float blue = row[offset] / 255f;
                float green = row[offset + 1] / 255f;
                float red = row[offset + 2] / 255f;
                tensor[0, 0, y, x] = (red - 0.485f) / 0.229f;
                tensor[0, 1, y, x] = (green - 0.456f) / 0.224f;
                tensor[0, 2, y, x] = (blue - 0.406f) / 0.225f;
            }
        }
        return tensor;
    }

    internal static SKBitmap ProcessMask(Tensor<float> tensor, int width, int height, out float minOut, out float maxOut)
    {
        var mask320 = new SKBitmap(320, 320, SKColorType.Gray8, SKAlphaType.Opaque);
        float[] probabilities = ArrayPool<float>.Shared.Rent(320 * 320);
        float minProb = float.MaxValue;
        float maxProb = float.MinValue;

        try
        {
            for (int y = 0; y < 320; y++)
            {
                int rowOffset = y * 320;
                for (int x = 0; x < 320; x++)
                {
                    float val = tensor[0, 0, y, x];
                    float prob = (float)(1.0 / (1.0 + Math.Exp(-val)));
                    probabilities[rowOffset + x] = prob;
                    if (prob < minProb) minProb = prob;
                    if (prob > maxProb) maxProb = prob;
                }
            }

            System.Diagnostics.Debug.WriteLine($"AI Mask Prob Range: Min={minProb}, Max={maxProb}");
            minOut = minProb;
            maxOut = maxProb;

            float range = maxProb - minProb;
            if (range < 0.001f) range = 1.0f;

            for (int y = 0; y < 320; y++)
            {
                Span<byte> row = GetWritableGrayRowSpan(mask320, y);
                int rowOffset = y * 320;
                for (int x = 0; x < 320; x++)
                {
                    float normalized = (probabilities[rowOffset + x] - minProb) / range;
                    row[x] = (byte)(normalized * 255);
                }
            }

            return mask320.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKFilterMode.Linear));
        }
        finally
        {
            ArrayPool<float>.Shared.Return(probabilities);
        }
    }

    internal static byte[] ApplyMask(SKBitmap original, SKBitmap mask, float min, float max)
    {
        using var result = new SKBitmap(original.Width, original.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);

        for (int y = 0; y < original.Height; y++)
        {
            ReadOnlySpan<byte> srcRow = GetPixelRowSpan(original, y);
            ReadOnlySpan<byte> maskRow = GetGrayRowSpan(mask, y);
            Span<byte> dstRow = GetPixelRowSpan(result, y);

            for (int x = 0; x < original.Width; x++)
            {
                int offset = x * 4;
                byte alpha = (byte)Math.Min(srcRow[offset + 3], maskRow[x]);
                if (alpha < 10) alpha = 0;
                if (alpha > 240) alpha = srcRow[offset + 3];

                dstRow[offset] = srcRow[offset];
                dstRow[offset + 1] = srcRow[offset + 1];
                dstRow[offset + 2] = srcRow[offset + 2];
                dstRow[offset + 3] = alpha;
            }
        }

        using var image = SKImage.FromBitmap(result);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static void CollectCornerSamples(
        SKBitmap bitmap,
        int startX,
        int endX,
        int startY,
        int endY,
        Span<SKColor> opaqueSamples,
        ref int totalSamples,
        ref int opaqueCount)
    {
        for (int y = startY; y < endY; y++)
        {
            for (int x = startX; x < endX; x++)
            {
                totalSamples++;
                SKColor color = ReadPixel(bitmap, x, y);
                if (color.Alpha <= 250)
                {
                    continue;
                }

                opaqueSamples[opaqueCount++] = color;
            }
        }
    }

    private static bool TryAccumulateOpaquePixel(SKBitmap bitmap, int x, int y, ref SampleAccumulator accumulator)
    {
        SKColor color = ReadPixel(bitmap, x, y);
        if (color.Alpha <= 250)
        {
            return false;
        }

        accumulator.Count++;
        accumulator.SumR += color.Red;
        accumulator.SumG += color.Green;
        accumulator.SumB += color.Blue;
        return true;
    }

    private static void AccumulateColorVariance(SKBitmap bitmap, int x, int y, double avgR, double avgG, double avgB, ref double sumSqDiff)
    {
        SKColor color = ReadPixel(bitmap, x, y);
        if (color.Alpha <= 250)
        {
            return;
        }

        double dr = color.Red - avgR;
        double dg = color.Green - avgG;
        double db = color.Blue - avgB;
        sumSqDiff += (dr * dr) + (dg * dg) + (db * db);
    }

    private static unsafe SKColor ReadPixel(SKBitmap bitmap, int x, int y)
    {
        ReadOnlySpan<byte> row = GetPixelRowSpan(bitmap, y);
        int offset = x * 4;
        return new SKColor(row[offset + 2], row[offset + 1], row[offset], row[offset + 3]);
    }

    private static unsafe Span<byte> GetPixelRowSpan(SKBitmap bitmap, int row)
    {
        byte* rowPtr = (byte*)bitmap.GetPixels() + (row * bitmap.RowBytes);
        return new Span<byte>(rowPtr, bitmap.Width * 4);
    }

    private static unsafe ReadOnlySpan<byte> GetGrayRowSpan(SKBitmap bitmap, int row)
    {
        byte* rowPtr = (byte*)bitmap.GetPixels() + (row * bitmap.RowBytes);
        return new ReadOnlySpan<byte>(rowPtr, bitmap.Width);
    }

    private static unsafe Span<byte> GetWritableGrayRowSpan(SKBitmap bitmap, int row)
    {
        byte* rowPtr = (byte*)bitmap.GetPixels() + (row * bitmap.RowBytes);
        return new Span<byte>(rowPtr, bitmap.Width);
    }

    private struct SampleAccumulator
    {
        public int Count;
        public long SumR;
        public long SumG;
        public long SumB;
    }

    public void Dispose()
    {
        AIResourceService.RequestGlobalUnload -= HandleGlobalUnload;
        _session?.Dispose();
        _session = null;
    }
}
