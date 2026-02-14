using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace GimmeCapture.Services.Core;

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

    private bool IsSolidBackground(SKBitmap bmp, out SKColor bgColor)
    {
        bgColor = SKColors.Empty;

        // Minimum size check
        if (bmp.Width < 50 || bmp.Height < 50) return false;

        // Sample pixels along the edges (top, bottom, left, right)
        // We want to detect if the "frame" of the image is uniform.
        var samples = new List<SKColor>();
        
        int step = 10; // Sample every 10 pixels
        
        // Top & Bottom
        for (int x = 0; x < bmp.Width; x += step)
        {
            samples.Add(bmp.GetPixel(x, 0));
            samples.Add(bmp.GetPixel(x, bmp.Height - 1));
        }

        // Left & Right
        for (int y = 0; y < bmp.Height; y += step)
        {
            samples.Add(bmp.GetPixel(0, y));
            samples.Add(bmp.GetPixel(bmp.Width - 1, y));
        }

        if (samples.Count == 0) return false;

        // Filter out transparent pixels - if edges are transparent, it's not a solid COLOR background we can remove easily
        var opaqueSamples = samples.Where(c => c.Alpha > 250).ToList();
        
        // If too many edge pixels are transparent, we probably shouldn't use the solid color algorithm
        if ((double)opaqueSamples.Count / samples.Count < 0.8) 
        {
             return false;
        }

        // Calculate Average Color
        long sumR = 0, sumG = 0, sumB = 0;
        foreach (var c in opaqueSamples)
        {
            sumR += c.Red;
            sumG += c.Green;
            sumB += c.Blue;
        }

        double avgR = (double)sumR / opaqueSamples.Count;
        double avgG = (double)sumG / opaqueSamples.Count;
        double avgB = (double)sumB / opaqueSamples.Count;

        // Calculate Standard Deviation (Variance)
        // This tells us how much the edge pixels differ from the average.
        double sumSqDiff = 0;
        foreach (var c in opaqueSamples)
        {
            double dr = c.Red - avgR;
            double dg = c.Green - avgG;
            double db = c.Blue - avgB;
            // Distance squared in RGB space
            sumSqDiff += (dr*dr + dg*dg + db*db);
        }

        double meanSqDiff = sumSqDiff / opaqueSamples.Count;
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

    private bool ColorsAreSimilar(SKColor a, SKColor b, int tolerance)
    {
        return Math.Abs(a.Red - b.Red) <= tolerance &&
               Math.Abs(a.Green - b.Green) <= tolerance &&
               Math.Abs(a.Blue - b.Blue) <= tolerance;
    }

    /// <summary>
    /// Alternative background detection that samples from image corners with a small radius.
    /// More reliable than edge-only sampling when the image is cropped close to the subject.
    /// </summary>
    private bool TryGetCornerBackgroundColor(SKBitmap bmp, out SKColor bgColor)
    {
        bgColor = SKColors.Empty;
        if (bmp.Width < 20 || bmp.Height < 20) return false;

        // Sample from all four corners using a 5x5 region
        int sampleSize = 5;
        var cornerSamples = new List<SKColor>();

        // Top-left corner
        for (int y = 0; y < sampleSize && y < bmp.Height; y++)
            for (int x = 0; x < sampleSize && x < bmp.Width; x++)
                cornerSamples.Add(bmp.GetPixel(x, y));

        // Top-right corner
        for (int y = 0; y < sampleSize && y < bmp.Height; y++)
            for (int x = bmp.Width - sampleSize; x < bmp.Width && x >= 0; x++)
                cornerSamples.Add(bmp.GetPixel(x, y));

        // Bottom-left corner
        for (int y = bmp.Height - sampleSize; y < bmp.Height && y >= 0; y++)
            for (int x = 0; x < sampleSize && x < bmp.Width; x++)
                cornerSamples.Add(bmp.GetPixel(x, y));

        // Bottom-right corner
        for (int y = bmp.Height - sampleSize; y < bmp.Height && y >= 0; y++)
            for (int x = bmp.Width - sampleSize; x < bmp.Width && x >= 0; x++)
                cornerSamples.Add(bmp.GetPixel(x, y));

        if (cornerSamples.Count == 0) return false;

        // Filter opaque samples
        var opaqueSamples = cornerSamples.Where(c => c.Alpha > 250).ToList();
        if (opaqueSamples.Count < cornerSamples.Count * 0.7) return false;

        // Group by similar colors to find dominant color
        var groups = new List<(SKColor avg, int count)>();
        foreach (var sample in opaqueSamples)
        {
            bool found = false;
            for (int i = 0; i < groups.Count; i++)
            {
                if (ColorsAreSimilar(sample, groups[i].avg, 30))
                {
                    // Update running average (simplified)
                    groups[i] = (groups[i].avg, groups[i].count + 1);
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                groups.Add((sample, 1));
            }
        }

        if (groups.Count == 0) return false;

        // Find most common color group
        var dominant = groups.OrderByDescending(g => g.count).First();
        
        // Require at least 60% of samples to be this color to be considered uniform
        double ratio = (double)dominant.count / opaqueSamples.Count;
        System.Diagnostics.Debug.WriteLine($"[CornerBg] Dominant color ratio: {ratio:P0}, Count: {dominant.count}/{opaqueSamples.Count}");
        
        if (ratio >= 0.6)
        {
            bgColor = dominant.avg;
            System.Diagnostics.Debug.WriteLine($"[CornerBg] Background detected: {bgColor}");
            return true;
        }

        return false;
    }

    private byte[] RemoveSolidBackground(SKBitmap original, SKColor bgColor)
    {
         // Use Unpremul for manual pixel manipulation
         using var result = new SKBitmap(original.Width, original.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
         
         // Increased tolerance to 60 to match the successful "Debug Manual Threshold" test.
         // This helps remove anti-aliasing halos (pixels between 200-255).
         int tolerance = 60; 

         for (int y = 0; y < original.Height; y++)
         {
             for (int x = 0; x < original.Width; x++)
             {
                 var color = original.GetPixel(x, y);
                 
                 if (ColorsAreSimilar(color, bgColor, tolerance))
                 {
                     // Transparent
                     result.SetPixel(x, y, new SKColor(color.Red, color.Green, color.Blue, 0));
                 }
                 else
                 {
                     // Preserve original color (including alpha)
                     result.SetPixel(x, y, color);
                 }
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
        var outputTensor = results.First().AsTensor<float>();

        // 3. Postprocess
        float minVal, maxVal;
        using var mask = ProcessMask(outputTensor, original.Width, original.Height, out minVal, out maxVal);
        
        // Apply mask to original
        return ApplyMask(original, mask, minVal, maxVal);
    }

    private DenseTensor<float> ExtractPixels(SKBitmap bitmap)
    {
        var tensor = new DenseTensor<float>(new[] { 1, 3, 320, 320 });
        
        for (int y = 0; y < 320; y++)
        {
            for (int x = 0; x < 320; x++)
            {
                var color = bitmap.GetPixel(x, y);
                // Normalization: (x / 255 - mean) / std
                tensor[0, 0, y, x] = (float)((color.Red / 255.0 - 0.485) / 0.229);
                tensor[0, 1, y, x] = (float)((color.Green / 255.0 - 0.456) / 0.224);
                tensor[0, 2, y, x] = (float)((color.Blue / 255.0 - 0.406) / 0.225);
            }
        }
        return tensor;
    }

    private SKBitmap ProcessMask(Tensor<float> tensor, int width, int height, out float minOut, out float maxOut)
    {
        var mask320 = new SKBitmap(320, 320, SKColorType.Gray8, SKAlphaType.Opaque);
        
        // Pass 1: Compute Sigmoid and find Min/Max of the probabilities
        var probabilities = new float[320 * 320];
        float minProb = float.MaxValue;
        float maxProb = float.MinValue;

        for (int y = 0; y < 320; y++)
        {
            for (int x = 0; x < 320; x++)
            {
                float val = tensor[0, 0, y, x];
                // Sigmoid
                float prob = (float)(1.0 / (1.0 + Math.Exp(-val)));
                
                probabilities[y * 320 + x] = prob;
                
                if (prob < minProb) minProb = prob;
                if (prob > maxProb) maxProb = prob;
            }
        }

        System.Diagnostics.Debug.WriteLine($"AI Mask Prob Range: Min={minProb}, Max={maxProb}");
        minOut = minProb;
        maxOut = maxProb;

        // Avoid divide by zero if flat image
        float range = maxProb - minProb;
        if (range < 0.001f) range = 1.0f;

        // Pass 2: Normalize and set pixels (Auto-Levels)
        for (int y = 0; y < 320; y++)
        {
            for (int x = 0; x < 320; x++)
            {
                float prob = probabilities[y * 320 + x];
                
                // Normalize to 0..1 based on actual image range
                // This forces the darkest part to Black and lightest to White
                float normalized = (prob - minProb) / range;
                
                byte b = (byte)(normalized * 255);
                mask320.SetPixel(x, y, new SKColor(b, b, b));
            }
        }
        
        return mask320.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKFilterMode.Linear));
    }

    private byte[] ApplyMask(SKBitmap original, SKBitmap mask, float min, float max)
    {
        using var result = new SKBitmap(original.Width, original.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        
        for (int y = 0; y < original.Height; y++)
        {
            for (int x = 0; x < original.Width; x++)
            {
                var color = original.GetPixel(x, y);
                var maskVal = mask.GetPixel(x, y).Red; 
                
                // Use mask value directly as Alpha for soft edges (hair, anti-aliasing)
                // PRESERVE original transparency: new alpha is min(original_alpha, mask_alpha)
                byte alpha = (byte)Math.Min((int)color.Alpha, (int)maskVal);
                
                // Optional: Clip very low values to full transparent to clean up noise
                if (alpha < 10) alpha = 0;
                // Optional: Clip very high values to full opaque (up to original alpha)
                if (alpha > 240) alpha = color.Alpha;
                
                // Set pixel with new alpha
                // Note: SkiaSharp SKColor constructors usually imply Premultiplied if we aren't careful, 
                // but since we created the bitmap with SKAlphaType.Unpremul, raw values are preserved.
                result.SetPixel(x, y, new SKColor(color.Red, color.Green, color.Blue, alpha));
            }
        }
        
        using var image = SKImage.FromBitmap(result);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public void Dispose()
    {
        AIResourceService.RequestGlobalUnload -= HandleGlobalUnload;
        _session?.Dispose();
        _session = null;
    }
}
