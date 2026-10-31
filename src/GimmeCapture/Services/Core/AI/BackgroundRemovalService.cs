using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Services.Core.Infrastructure;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace GimmeCapture.Services.Core.AI;

public class BackgroundRemovalService : GimmeCapture.Services.Abstractions.IBackgroundRemovalService
{
    /// <summary>Fixed square input resolution required by the U2Net background-removal model.</summary>
    private const int U2NetInputSize = 320;

    // What a canvas writes for each channel value of a drawn pixel, by alpha; see DrawnChannels. Built on first use.
    private static readonly Lazy<byte[]> s_drawnPremulChannels = new(() => BuildDrawnChannels(SKAlphaType.Premul));
    private static readonly Lazy<byte[]> s_drawnUnpremulChannels = new(() => BuildDrawnChannels(SKAlphaType.Unpremul));

    private InferenceSession? _session;
    private readonly AIResourceService _aiResourceService;
    private readonly AIPathService _pathService;
    private bool _isInitialized = false;
    // Serializes session use vs. unload, and idle-unloads the ~185 MB U2Net session when unused, so a one-off
    // background removal doesn't keep the model resident. The next removal transparently reloads it.
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly IdleReleaseScheduler _idleUnload;


    public BackgroundRemovalService(AIResourceService aiResourceService, AIPathService pathService)
    {
        _aiResourceService = aiResourceService;
        _pathService = pathService;
        // Background removal is a one-shot per image, so unload promptly (10s) after it finishes; a fresh
        // removal within the window resets this (keep-warm) instead of unloading then reloading the model.
        _idleUnload = new IdleReleaseScheduler(TimeSpan.FromSeconds(10), UnloadSession);

        AIResourceService.RequestGlobalUnload += HandleGlobalUnload;
    }

    private void HandleGlobalUnload()
    {
        Dispose();
    }

    public async Task InitializeAsync()
    {
        if (_isInitialized) return;

        var baseDir = _aiResourceService.GetAIResourcesPath();
        var modelPath = _pathService.GetAICoreModelPath();

        if (!File.Exists(modelPath))
            throw new FileNotFoundException("AI Model not found. Please download it first.");

        await Task.Run(() =>
        {
            try
            {
                _session = CreateSession(modelPath);
                _isInitialized = true;
            }
            catch (TypeInitializationException ex)
            {
                // Most common error: the native ONNX Runtime library is missing or failed to load
                var inner = ex.InnerException?.Message ?? "No inner exception";
                throw new Exception($"ONNX Runtime failed to initialize. Make sure you have the required native libraries. \nInner Error: {inner}", ex);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to initialize AI model: {ex.Message}", ex);
            }
        });
    }

    /// <summary>
    /// Builds the U2Net session, on the CPU with ONNX Runtime's memory arena off.
    /// </summary>
    /// <remarks>
    /// It ran on DirectML, which held 1.09 GB of the process with the model loaded and does not give back all it
    /// allocates: about 480 MB stayed while the device was kept for the next removal, and about 255 MB for good once it
    /// closed. On the CPU the model's run takes 0.4 s instead of 0.01-0.04, but building the session 0.45-0.5 s instead
    /// of 1.0-1.2, so a removal after the 10 s unload, the usual case, is done sooner: 0.9-1.1 s for a picture the size
    /// of two screens (Ryzen 7 5700X against an RTX 4070 SUPER). The arena kept what each run allocated, 1.24 GB after
    /// two runs; without it the session holds 175-185 MB and the runs are no slower. <c>BackgroundRemovalServiceTests</c>
    /// has the measurement.
    /// </remarks>
    internal static InferenceSession CreateSession(string modelPath)
    {
        var stopwatch = Stopwatch.StartNew();
        using var options = new SessionOptions { EnableCpuMemArena = false };
        var session = new InferenceSession(modelPath, options);
        AppLog.Information(
            "BackgroundRemoval.SessionCreated",
            $"{OnnxProviderConfigurator.CpuProvider} in {stopwatch.ElapsedMilliseconds} ms");
        return session;
    }


    /// <summary>
    /// Removes the background from <paramref name="source"/>, confined to <paramref name="selectionRect"/>
    /// (in source pixels) when one is given.
    /// </summary>
    /// <remarks>
    /// Takes and returns pixels rather than encoded PNG. The pin already holds a bitmap and wants one back, so
    /// a <c>byte[]</c> boundary only bought a PNG encode on the way in and a decode on the way out — and the
    /// encode alone cost a 4K pin over 2 s. <paramref name="source"/> stays the caller's to dispose, and the
    /// returned bitmap becomes the caller's.
    /// </remarks>
    public async Task<SKBitmap> RemoveBackgroundAsync(SKBitmap source, Avalonia.Rect? selectionRect = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        // Hold the gate across init + inference so the idle-unload can't dispose the session mid-removal.
        await _sessionGate.WaitAsync();
        try
        {
        if (!_isInitialized) await InitializeAsync();
        if (_session == null) throw new InvalidOperationException("AI Session not initialized.");

        return await Task.Run(() =>
        {
            SKBitmap? cropped = null;
            SKBitmap targetBmp = source;
            SKRectI? roi = null;

            if (selectionRect.HasValue && selectionRect.Value.Width > 1 && selectionRect.Value.Height > 1)
            {
                // Convert logical Rect to pixel Rect using rounding for better accuracy
                int x = (int)Math.Round(Math.Max(0, selectionRect.Value.X));
                int y = (int)Math.Round(Math.Max(0, selectionRect.Value.Y));
                int w = (int)Math.Round(Math.Min(source.Width - x, selectionRect.Value.Width));
                int h = (int)Math.Round(Math.Min(source.Height - y, selectionRect.Value.Height));
                roi = new SKRectI(x, y, x + w, y + h);

                // Create cropped version
                cropped = new SKBitmap(w, h);
                source.ExtractSubset(cropped, roi.Value);
                targetBmp = cropped;
            }

            try
            {
                SKBitmap processed;
                SKColor bgColor;

                // Try edge-based solid background detection first
                if (IsSolidBackground(targetBmp, out bgColor))
                {
                    AppLog.Debug("BackgroundRemovalService", $"Solid background detected via edges ({bgColor}). Using Manual Removal.");
                    processed = RemoveSolidBackground(targetBmp, bgColor);
                }
                // Try corner-based detection as fallback (better for cropped regions where logo touches edges)
                else if (TryGetCornerBackgroundColor(targetBmp, out bgColor))
                {
                    AppLog.Debug("BackgroundRemovalService", $"Solid background detected via corners ({bgColor}). Using Manual Removal.");
                    processed = RemoveSolidBackground(targetBmp, bgColor);
                }
                else
                {
                    AppLog.Debug("BackgroundRemovalService", "Complex background detected. Using AI Removal.");
                    var session = _session;
                    processed = RunAIInference(targetBmp, session!);
                }

                // Whole-image removal: the processed bitmap already covers the source, so hand it straight back.
                if (roi is not { } region)
                {
                    return processed;
                }

                using (processed)
                {
                    return ComposeSelection(source, processed, region);
                }
            }
            finally
            {
                cropped?.Dispose();
            }
        });
        }
        finally
        {
            _sessionGate.Release();
            // Start the idle-unload countdown from completion, so a slow removal can't trip it mid-run and a
            // fresh removal within the window keeps the model warm.
            _idleUnload.NotifyUse();
        }
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

        AppLog.Debug("BackgroundRemovalService", $"[BgDetection] Edge StdDev: {stdDev:F2} (Threshold: 15.0)");

        // Thresholds:
        // A truly solid digital image (mspaint) has StdDev ~ 0.
        // A clean screenshot might have StdDev < 5 (compression artifacts).
        // A photo of a wall/curtain (even if it looks solid) will often have StdDev > 20 due to shadows/noise.
        
        if (stdDev < 15.0) 
        {
            bgColor = new SKColor((byte)avgR, (byte)avgG, (byte)avgB);
            AppLog.Debug("BackgroundRemovalService", $"[BgDetection] Solid background confirmed. Color: {bgColor}");
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
        AppLog.Debug("BackgroundRemovalService", $"[CornerBg] Dominant color ratio: {ratio:P0}, Count: {groupCounts[dominantIndex]}/{opaqueCount}");

        if (ratio >= 0.6)
        {
            bgColor = groupColors[dominantIndex];
            AppLog.Debug("BackgroundRemovalService", $"[CornerBg] Background detected: {bgColor}");
            return true;
        }

        return false;
    }

    /// <summary>Clears pixels close to <paramref name="bgColor"/> to transparent. The returned bitmap is the
    /// caller's to dispose.</summary>
    internal static SKBitmap RemoveSolidBackground(SKBitmap original, SKColor bgColor)
    {
        int width = original.Width;
        int height = original.Height;
        var result = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        const int tolerance = 60;

        ForEachRowBand(height, (firstRow, endRow) =>
        {
            for (int y = firstRow; y < endRow; y++)
            {
                ReadOnlySpan<uint> srcRow = GetPixelRowWords(original, y);
                Span<uint> dstRow = GetPixelRowWords(result, y);
                for (int x = 0; x < dstRow.Length; x++)
                {
                    // A BGRA pixel read as one little-endian word is 0xAARRGGBB, SKColor's own layout.
                    uint pixel = srcRow[x];
                    dstRow[x] = ColorsAreSimilar(new SKColor(pixel), bgColor, tolerance) ? pixel & 0x00FFFFFFu : pixel;
                }
            }
        });

        return result;
    }

    /// <summary>A copy of <paramref name="source"/> with <paramref name="processed"/> in place of
    /// <paramref name="region"/>. The returned bitmap is the caller's to dispose.</summary>
    /// <remarks>
    /// The bytes are those of drawing both onto a canvas over the result: the source, then the region with Src.
    /// For BGRA bitmaps that fit, the usual case, they are copied through <see cref="DrawnChannels"/> instead,
    /// which skips the canvas's per-pixel work (0.08-0.17 s on a 4K pin). <c>BackgroundRemovalServiceTests</c>
    /// hold the two to each other.
    /// </remarks>
    internal static SKBitmap ComposeSelection(SKBitmap source, SKBitmap processed, SKRectI region)
    {
        var result = new SKBitmap(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        if (source.ColorType == SKColorType.Bgra8888 && source.AlphaType is SKAlphaType.Premul or SKAlphaType.Unpremul
            && processed.ColorType == SKColorType.Bgra8888 && processed.AlphaType == SKAlphaType.Unpremul
            && region.Left >= 0 && region.Top >= 0 && region.Right <= source.Width && region.Bottom <= source.Height
            && processed.Width == region.Width && processed.Height == region.Height)
        {
            CopyAsDrawn(source, result, 0, 0);
            CopyAsDrawn(processed, result, region.Left, region.Top);
            return result;
        }

        using (var canvas = new SKCanvas(result))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(source, 0, 0);
            // Use Src blend mode to replace the ROI with processed transparent version
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
            canvas.DrawBitmap(processed, region.Left, region.Top, paint);
        }

        return result;
    }

    /// <summary>Writes the BGRA <paramref name="bitmap"/> into <paramref name="target"/> at
    /// (<paramref name="left"/>, <paramref name="top"/>) as a canvas over it would draw it.</summary>
    private static void CopyAsDrawn(SKBitmap bitmap, SKBitmap target, int left, int top)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        var alphaType = bitmap.AlphaType;
        ForEachRowBand(height, (firstRow, endRow) =>
        {
            byte[]? drawn = null;
            for (int y = firstRow; y < endRow; y++)
            {
                ReadOnlySpan<uint> from = GetPixelRowWords(bitmap, y);
                Span<uint> to = GetPixelRowWords(target, top + y).Slice(left, width);
                int x = 0;
                while (x < width)
                {
                    // A draw leaves a pixel with alpha 255, a word of 0xFF000000 or more, as it is: copy those runs.
                    int opaqueRun = from[x..].IndexOfAnyInRange(0u, 0xFEFFFFFFu);
                    if (opaqueRun < 0)
                    {
                        opaqueRun = width - x;
                    }

                    from.Slice(x, opaqueRun).CopyTo(to.Slice(x, opaqueRun));
                    x += opaqueRun;
                    if (x == width)
                    {
                        break;
                    }

                    drawn ??= DrawnChannels(alphaType);
                    for (; x < width && from[x] < 0xFF000000u; x++)
                    {
                        uint pixel = from[x];
                        int alphaRow = (int)(pixel >> 24) << 8;
                        to[x] = (pixel & 0xFF000000u)
                            | ((uint)drawn[alphaRow | (int)((pixel >> 16) & 0xFF)] << 16)
                            | ((uint)drawn[alphaRow | (int)((pixel >> 8) & 0xFF)] << 8)
                            | drawn[alphaRow | (int)(pixel & 0xFF)];
                    }
                }
            }
        });
    }

    /// <summary>
    /// What a canvas over an unpremultiplied BGRA bitmap writes for a colour channel of a pixel it draws, at
    /// <c>[alpha * 256 + value]</c>, for bitmaps of <paramref name="alphaType"/>. The pixel's alpha comes through
    /// as it is.
    /// </summary>
    /// <remarks>
    /// The canvas converts to premultiplied alpha and back in floating point: where alpha is 0 the colour comes
    /// out as 0, and a premultiplied pixel is divided by its alpha, clamped and rounded the way this CPU's Skia
    /// code rounds. Rather than copy that arithmetic, the table is read back from Skia itself, from one draw of
    /// every alpha against every value, so it is the canvas's own answer.
    /// </remarks>
    private static byte[] DrawnChannels(SKAlphaType alphaType) =>
        alphaType == SKAlphaType.Premul ? s_drawnPremulChannels.Value : s_drawnUnpremulChannels.Value;

    private static byte[] BuildDrawnChannels(SKAlphaType alphaType)
    {
        using var everyPixel = new SKBitmap(256, 256, SKColorType.Bgra8888, alphaType);
        for (int alpha = 0; alpha < 256; alpha++)
        {
            Span<uint> row = GetPixelRowWords(everyPixel, alpha);
            for (int value = 0; value < 256; value++)
            {
                row[value] = ((uint)alpha << 24) | ((uint)value * 0x010101u);
            }
        }

        using var drawn = new SKBitmap(256, 256, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using (var canvas = new SKCanvas(drawn))
        using (var paint = new SKPaint { BlendMode = SKBlendMode.Src })
        {
            canvas.DrawBitmap(everyPixel, 0, 0, paint);
        }

        var table = new byte[256 * 256];
        for (int alpha = 0; alpha < 256; alpha++)
        {
            ReadOnlySpan<uint> row = GetPixelRowWords(drawn, alpha);
            for (int value = 0; value < 256; value++)
            {
                table[(alpha << 8) | value] = (byte)row[value];
            }
        }

        return table;
    }

    /// <summary>Runs U2Net over <paramref name="original"/>. The returned bitmap is the caller's to dispose.</summary>
    private SKBitmap RunAIInference(SKBitmap original, InferenceSession session)
    {
        // 1. Preprocess (Resize to 320x320 and Normalize)
        // Use High quality (Cubic) for better detail preservation -> Reverted to Linear for safety
        int inputSize = U2NetInputSize;
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
        const int planeSize = U2NetInputSize * U2NetInputSize;
        // Filled through the flat array rather than the tensor's indexer, which takes its indices as a params
        // array: an allocation per write, three per pixel.
        var data = new float[3 * planeSize];
        for (int y = 0; y < U2NetInputSize; y++)
        {
            ReadOnlySpan<byte> row = GetPixelRowSpan(bitmap, y);
            int index = y * U2NetInputSize;
            for (int x = 0; x < U2NetInputSize; x++, index++)
            {
                int offset = x * 4;
                float blue = row[offset] / 255f;
                float green = row[offset + 1] / 255f;
                float red = row[offset + 2] / 255f;
                data[index] = (red - 0.485f) / 0.229f;
                data[planeSize + index] = (green - 0.456f) / 0.224f;
                data[(2 * planeSize) + index] = (blue - 0.406f) / 0.225f;
            }
        }
        return new DenseTensor<float>(data, new[] { 1, 3, U2NetInputSize, U2NetInputSize });
    }

    internal static SKBitmap ProcessMask(Tensor<float> tensor, int width, int height, out float minOut, out float maxOut)
    {
        const int planeSize = U2NetInputSize * U2NetInputSize;
        var mask320 = new SKBitmap(U2NetInputSize, U2NetInputSize, SKColorType.Gray8, SKAlphaType.Opaque);
        float[] probabilities = ArrayPool<float>.Shared.Rent(planeSize);
        float minProb = float.MaxValue;
        float maxProb = float.MinValue;

        try
        {
            ReadOnlySpan<float> logits = GetMaskPlane(tensor, probabilities);
            for (int i = 0; i < planeSize; i++)
            {
                float prob = (float)(1.0 / (1.0 + Math.Exp(-logits[i])));
                probabilities[i] = prob;
                if (prob < minProb) minProb = prob;
                if (prob > maxProb) maxProb = prob;
            }

            AppLog.Debug("BackgroundRemovalService", $"AI Mask Prob Range: Min={minProb}, Max={maxProb}");
            minOut = minProb;
            maxOut = maxProb;

            float range = maxProb - minProb;
            if (range < 0.001f) range = 1.0f;

            for (int y = 0; y < U2NetInputSize; y++)
            {
                Span<byte> row = GetWritableGrayRowSpan(mask320, y);
                int rowOffset = y * U2NetInputSize;
                for (int x = 0; x < U2NetInputSize; x++)
                {
                    float normalized = (probabilities[rowOffset + x] - minProb) / range;
                    row[x] = (byte)(normalized * 255);
                }
            }

            return ResizeGrayMask(mask320, width, height);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(probabilities);
        }
    }

    /// <summary>The model output's first 320x320 plane, the mask's logits.</summary>
    /// <remarks>Read straight from the buffer of the dense tensor ONNX Runtime returns. Any other tensor is copied
    /// into <paramref name="scratch"/> through its indexer, which allocates a params array per read.</remarks>
    private static ReadOnlySpan<float> GetMaskPlane(Tensor<float> tensor, float[] scratch)
    {
        const int planeSize = U2NetInputSize * U2NetInputSize;
        if (tensor is DenseTensor<float> { IsReversedStride: false, Rank: 4 } dense
            && dense.Dimensions[2] == U2NetInputSize && dense.Dimensions[3] == U2NetInputSize)
        {
            return dense.Buffer.Span[..planeSize];
        }

        for (int y = 0; y < U2NetInputSize; y++)
        {
            for (int x = 0; x < U2NetInputSize; x++)
            {
                scratch[(y * U2NetInputSize) + x] = tensor[0, 0, y, x];
            }
        }

        return scratch.AsSpan(0, planeSize);
    }

    /// <summary>Applies <paramref name="mask"/> as the alpha channel of <paramref name="original"/>. The
    /// returned bitmap is the caller's to dispose.</summary>
    internal static SKBitmap ApplyMask(SKBitmap original, SKBitmap mask, float min, float max)
    {
        // Every size and colour type below is read once: each is a call into Skia, and reading them in the pixel
        // loop made a 4K removal spend most of its time there.
        int width = original.Width;
        int height = original.Height;
        if (mask.Width != width || mask.Height != height)
        {
            throw new ArgumentException("Mask dimensions must match the source image.", nameof(mask));
        }

        var result = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        var maskColorType = mask.ColorType;
        bool isByteMask = maskColorType is SKColorType.Gray8 or SKColorType.Alpha8;
        bool canReadMaskRow = isByteMask || maskColorType is SKColorType.Bgra8888 or SKColorType.Rgba8888;

        ForEachRowBand(height, (firstRow, endRow) =>
        {
            for (int y = firstRow; y < endRow; y++)
            {
                ReadOnlySpan<uint> srcRow = GetPixelRowWords(original, y);
                Span<uint> dstRow = GetPixelRowWords(result, y);

                if (isByteMask)
                {
                    ReadOnlySpan<byte> maskRow = GetGrayRowSpan(mask, y);
                    for (int x = 0; x < dstRow.Length; x++)
                    {
                        dstRow[x] = MaskPixel(srcRow[x], maskRow[x]);
                    }

                    continue;
                }

                ReadOnlySpan<byte> wideMaskRow = canReadMaskRow ? GetBitmapRowSpan(mask, y) : default;
                for (int x = 0; x < dstRow.Length; x++)
                {
                    dstRow[x] = MaskPixel(srcRow[x], ReadMaskAlpha(mask, maskColorType, wideMaskRow, x, y));
                }
            }
        });

        return result;
    }

    /// <summary>Runs <paramref name="body"/> over bands of rows [first, end) that together cover
    /// [0, <paramref name="rows"/>), on every core.</summary>
    /// <remarks>The full-size passes are memory-bound: one core spends ~30 ms on each at 4K, every core together
    /// ~6 ms. No row depends on another, so the bytes are the same either way.</remarks>
    private static void ForEachRowBand(int rows, Action<int, int> body)
    {
        if (rows <= 0)
        {
            return;
        }

        Parallel.ForEach(Partitioner.Create(0, rows), band => body(band.Item1, band.Item2));
    }

    /// <summary>One BGRA pixel with the mask applied: the lower of its alpha and the mask's, cut to transparent
    /// below 10, and back to the pixel's own alpha above 240.</summary>
    private static uint MaskPixel(uint pixel, uint maskAlpha)
    {
        uint sourceAlpha = pixel >> 24;
        uint alpha = Math.Min(sourceAlpha, maskAlpha);
        if (alpha < 10) alpha = 0;
        if (alpha > 240) alpha = sourceAlpha;
        return (pixel & 0x00FFFFFFu) | (alpha << 24);
    }

    private static SKBitmap ResizeGrayMask(SKBitmap source, int width, int height)
    {
        if (source.ColorType != SKColorType.Gray8)
        {
            throw new ArgumentException("Source mask must be Gray8.", nameof(source));
        }

        int sourceWidth = source.Width;
        int sourceHeight = source.Height;
        var resized = new SKBitmap(width, height, SKColorType.Gray8, SKAlphaType.Opaque);
        if (width == sourceWidth && height == sourceHeight)
        {
            source.CopyTo(resized);
            return resized;
        }

        double scaleX = sourceWidth / (double)width;
        double scaleY = sourceHeight / (double)height;

        // A target column reads the same two source columns at the same weight on every row: work them out once.
        var leftColumns = new int[width];
        var rightColumns = new int[width];
        var columnWeights = new double[width];
        for (int x = 0; x < width; x++)
        {
            double sourceX = ((x + 0.5d) * scaleX) - 0.5d;
            int x0 = Math.Clamp((int)Math.Floor(sourceX), 0, sourceWidth - 1);
            leftColumns[x] = x0;
            rightColumns[x] = Math.Min(x0 + 1, sourceWidth - 1);
            columnWeights[x] = Math.Clamp(sourceX - x0, 0d, 1d);
        }

        ForEachRowBand(height, (firstRow, endRow) =>
        {
            // Interpolating along a source row gives the same values for every target row that blends it in, and
            // a screen-sized target reads each of the 320 rows for several in a row: keep the two the current one
            // needs.
            double[] top = ArrayPool<double>.Shared.Rent(width);
            double[] bottom = ArrayPool<double>.Shared.Rent(width);
            int topRow = -1;
            int bottomRow = -1;

            try
            {
                for (int y = firstRow; y < endRow; y++)
                {
                    double sourceY = ((y + 0.5d) * scaleY) - 0.5d;
                    int y0 = Math.Clamp((int)Math.Floor(sourceY), 0, sourceHeight - 1);
                    int y1 = Math.Min(y0 + 1, sourceHeight - 1);
                    double fy = Math.Clamp(sourceY - y0, 0d, 1d);

                    if (y0 != topRow)
                    {
                        if (y0 == bottomRow)
                        {
                            (top, bottom) = (bottom, top);
                            bottomRow = topRow;
                        }
                        else
                        {
                            InterpolateRow(GetGrayRowSpan(source, y0), leftColumns, rightColumns, columnWeights, top);
                        }

                        topRow = y0;
                    }

                    if (y1 != bottomRow)
                    {
                        InterpolateRow(GetGrayRowSpan(source, y1), leftColumns, rightColumns, columnWeights, bottom);
                        bottomRow = y1;
                    }

                    Span<byte> resizedRow = GetWritableGrayRowSpan(resized, y);
                    for (int x = 0; x < resizedRow.Length; x++)
                    {
                        resizedRow[x] = (byte)Math.Clamp((int)Math.Round(top[x] + ((bottom[x] - top[x]) * fy)), 0, 255);
                    }
                }
            }
            finally
            {
                ArrayPool<double>.Shared.Return(top);
                ArrayPool<double>.Shared.Return(bottom);
            }
        });

        return resized;
    }

    /// <summary>Interpolates <paramref name="sourceRow"/> at every target column into the start of
    /// <paramref name="row"/>, which may be longer.</summary>
    private static void InterpolateRow(ReadOnlySpan<byte> sourceRow, int[] leftColumns, int[] rightColumns, double[] weights, double[] row)
    {
        for (int x = 0; x < leftColumns.Length; x++)
        {
            int x0 = leftColumns[x];
            row[x] = sourceRow[x0] + ((sourceRow[rightColumns[x]] - sourceRow[x0]) * weights[x]);
        }
    }

    private static byte ReadMaskAlpha(SKBitmap mask, SKColorType colorType, ReadOnlySpan<byte> row, int x, int y)
    {
        return colorType switch
        {
            SKColorType.Gray8 or SKColorType.Alpha8 => row[x],
            SKColorType.Bgra8888 => row[(x * 4) + 2],
            SKColorType.Rgba8888 => row[x * 4],
            _ => mask.GetPixel(x, y).Red
        };
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

    /// <summary>A 32-bit row as one word per pixel: a BGRA pixel reads as 0xAARRGGBB.</summary>
    private static unsafe Span<uint> GetPixelRowWords(SKBitmap bitmap, int row)
    {
        byte* rowPtr = (byte*)bitmap.GetPixels() + (row * bitmap.RowBytes);
        return new Span<uint>(rowPtr, bitmap.Width);
    }

    private static unsafe ReadOnlySpan<byte> GetGrayRowSpan(SKBitmap bitmap, int row)
    {
        byte* rowPtr = (byte*)bitmap.GetPixels() + (row * bitmap.RowBytes);
        return new ReadOnlySpan<byte>(rowPtr, bitmap.Width);
    }

    private static unsafe ReadOnlySpan<byte> GetBitmapRowSpan(SKBitmap bitmap, int row)
    {
        byte* rowPtr = (byte*)bitmap.GetPixels() + (row * bitmap.RowBytes);
        return new ReadOnlySpan<byte>(rowPtr, bitmap.RowBytes);
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

    // Frees the ONNX session — the idle-unload callback and Dispose both use it. Skips if a removal is in
    // flight (the gate is held); the next idle retries. A later RemoveBackgroundAsync re-initializes lazily.
    private void UnloadSession()
    {
        if (!_sessionGate.Wait(0))
        {
            return;
        }

        bool released;
        try
        {
            released = _session != null;
            _session?.Dispose();
            _session = null;
            _isInitialized = false;
        }
        finally
        {
            _sessionGate.Release();
        }

        if (released)
        {
            ProcessMemoryTrimService.RequestIdleTrimAsync("u2net-idle").Forget("MemoryTrim.U2NetIdle");
        }
    }

    /// <summary>True once disposed (e.g. by a global AI unload). A holder reusing an instance must recreate a
    /// fresh one when this is set, or its rebuilt session would no longer be subscribed to global unload.</summary>
    public bool IsDisposed { get; private set; }

    public void Dispose()
    {
        IsDisposed = true;
        AIResourceService.RequestGlobalUnload -= HandleGlobalUnload;
        _idleUnload.Cancel();
        UnloadSession();
    }
}
