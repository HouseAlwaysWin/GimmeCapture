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
    private readonly AIResourceService _resourceService;
    private bool _isInitialized = false;

    private static bool _isResolverSet = false;

    public BackgroundRemovalService(AIResourceService resourceService)
    {
        _resourceService = resourceService;
    }

    public async Task InitializeAsync()
    {
        if (_isInitialized) return;

        // Ensure we load the native libraries from the custom path
        SetupNativeResolver();

        var baseDir = _resourceService.GetAIResourcesPath();
        var modelPath = Path.Combine(baseDir, "models", "u2net.onnx");

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

    private void SetupNativeResolver()
    {
        if (_isResolverSet) return;
        _isResolverSet = true;

        // Use .NET's modern DllImportResolver to point directly to the AI/runtime folder.
        // This is much more reliable than modifying PATH at runtime.
        NativeLibrary.SetDllImportResolver(typeof(InferenceSession).Assembly, (libraryName, assembly, searchPath) =>
        {
            if (libraryName == "onnxruntime")
            {
                var runtimeDir = Path.Combine(_resourceService.GetAIResourcesPath(), "runtime");
                var dllPath = Path.Combine(runtimeDir, "onnxruntime.dll");
                
                if (File.Exists(dllPath))
                {
                    System.Diagnostics.Debug.WriteLine($"[ONNX] Custom Resolver loading: {dllPath}");
                    return NativeLibrary.Load(dllPath);
                }
            }
            return IntPtr.Zero;
        });

        // Also add to PATH as fallback for dependencies of onnxruntime.dll (like zlib, etc)
        var runtimeDirFallback = Path.Combine(_resourceService.GetAIResourcesPath(), "runtime");
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (!path.Contains(runtimeDirFallback))
        {
            Environment.SetEnvironmentVariable("PATH", runtimeDirFallback + Path.PathSeparator + path);
        }
    }

    public async Task<byte[]> RemoveBackgroundAsync(byte[] imageBytes)
    {
        if (!_isInitialized) await InitializeAsync();
        if (_session == null) throw new InvalidOperationException("AI Session not initialized.");

        return await Task.Run(() =>
        {
            using var original = SKBitmap.Decode(imageBytes);
            if (original == null) return imageBytes;

            if (IsSolidBackground(original, out var bgColor))
            {
                System.Diagnostics.Debug.WriteLine($"Solid background detected ({bgColor}). Using Manual Removal.");
                return RemoveSolidBackground(original, bgColor);
            }
            
            System.Diagnostics.Debug.WriteLine("Complex background detected. Using AI Removal.");
            // Capture session locally to satisfy compiler nullability checks
            var session = _session;
            return RunAIInference(original, session);
        });
    }

    private bool IsSolidBackground(SKBitmap bmp, out SKColor bgColor)
    {
        // Probe closer to edge (2px)
        int margin = 2;
        int w = bmp.Width;
        int h = bmp.Height;

        if (w <= 10 || h <= 10) margin = 0;

        // Collect all 4 corners
        SKColor[] corners = new SKColor[4];
        corners[0] = bmp.GetPixel(margin, margin); // Top-Left
        corners[1] = bmp.GetPixel(w - 1 - margin, margin); // Top-Right
        corners[2] = bmp.GetPixel(margin, h - 1 - margin); // Bottom-Left
        corners[3] = bmp.GetPixel(w - 1 - margin, h - 1 - margin); // Bottom-Right

        bgColor = SKColors.Empty;

        // Find the "Consensus Color"
        // We compare every corner against every other corner to find the largest group of similar colors.
        int maxVotes = 0;
        SKColor bestCandidate = SKColors.Empty;

        for (int i = 0; i < 4; i++)
        {
            var candidate = corners[i];
            
            // Skip transparent corners
            if (candidate.Alpha < 250) continue;

            // Determine tolerance for this candidate
            int tolerance = 25;
            if (candidate.Red > 240 && candidate.Green > 240 && candidate.Blue > 240)
            {
                tolerance = 45; // Bright/White tolerance
            }

            int votes = 0;
            for (int j = 0; j < 4; j++)
            {
                if (ColorsAreSimilar(candidate, corners[j], tolerance))
                {
                    votes++;
                }
            }

            if (votes > maxVotes)
            {
                maxVotes = votes;
                bestCandidate = candidate;
            }
        }

        // We need at least 3 corners (including itself) to agree to call it a solid background.
        // This allows 1 corner to be different (watermark, UI element, noise) while still detecting the background.
        if (maxVotes >= 3)
        {
            bgColor = bestCandidate;
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
                     // Opaque
                     result.SetPixel(x, y, new SKColor(color.Red, color.Green, color.Blue, 255));
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
                byte alpha = maskVal;
                
                // Optional: Clip very low values to full transparent to clean up noise
                if (alpha < 10) alpha = 0;
                // Optional: Clip very high values to full opaque to ensure solid subject
                if (alpha > 240) alpha = 255;
                
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
        _session?.Dispose();
    }
}
