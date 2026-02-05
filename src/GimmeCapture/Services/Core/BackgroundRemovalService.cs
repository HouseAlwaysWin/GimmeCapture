using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace GimmeCapture.Services.Core;

public class BackgroundRemovalService : IDisposable
{
    private InferenceSession? _session;
    private readonly AIResourceService _resourceService;
    private bool _isInitialized = false;

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
            var options = new SessionOptions();
            try
            {
                // Try CUDA first (4070 Super)
                options.AppendExecutionProvider_CUDA(0);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CUDA not available, falling back to CPU: {ex.Message}");
            }
            
            _session = new InferenceSession(modelPath, options);
            _isInitialized = true;
        });
    }

    private void SetupNativeResolver()
    {
        var runtimeDir = Path.Combine(_resourceService.GetAIResourcesPath(), "runtime");
        
        // Add runtime dir to PATH so Windows can find native DLLs like onnxruntime.dll, cublas64_11.dll etc.
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (!path.Contains(runtimeDir))
        {
            Environment.SetEnvironmentVariable("PATH", runtimeDir + Path.PathSeparator + path);
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

            // 1. Preprocess (Resize to 320x320 and Normalize)
            int inputSize = 320;
            using var resized = original.Resize(new SKImageInfo(inputSize, inputSize), new SKSamplingOptions(SKFilterMode.Linear));
            var inputTensor = ExtractPixels(resized);

            // 2. Inference
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input.1", inputTensor)
            };

            using var results = _session.Run(inputs);
            var outputTensor = results.First().AsTensor<float>();

            // 3. Postprocess
            // The output is a 320x320 mask (values roughly 0-1)
            using var mask = ProcessMask(outputTensor, original.Width, original.Height);
            
            // Apply mask to original
            return ApplyMask(original, mask);
        });
    }

    private DenseTensor<float> ExtractPixels(SKBitmap bitmap)
    {
        var tensor = new DenseTensor<float>(new[] { 1, 3, 320, 320 });
        
        for (int y = 0; y < 320; y++)
        {
            for (int x = 0; x < 320; x++)
            {
                var color = bitmap.GetPixel(x, y);
                // Normalization: (x / 255.0 - mean) / std
                // U2Net typically uses 0.485, 0.456, 0.406 and 0.229, 0.224, 0.225
                tensor[0, 0, y, x] = (float)((color.Red / 255.0 - 0.485) / 0.229);
                tensor[0, 1, y, x] = (float)((color.Green / 255.0 - 0.456) / 0.224);
                tensor[0, 2, y, x] = (float)((color.Blue / 255.0 - 0.406) / 0.225);
            }
        }
        return tensor;
    }

    private SKBitmap ProcessMask(Tensor<float> tensor, int width, int height)
    {
        var mask320 = new SKBitmap(320, 320, SKColorType.Gray8, SKAlphaType.Opaque);
        
        for (int y = 0; y < 320; y++)
        {
            for (int x = 0; x < 320; x++)
            {
                float val = tensor[0, 0, y, x];
                // Sigmoid if not already in 0-1 range (U2Net output is usually raw logits)
                float alpha = (float)(1.0 / (1.0 + Math.Exp(-val)));
                byte b = (byte)(alpha * 255);
                mask320.SetPixel(x, y, new SKColor(b, b, b));
            }
        }

        // Resize mask back to original dimensions
        return mask320.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKFilterMode.Linear));
    }

    private byte[] ApplyMask(SKBitmap original, SKBitmap mask)
    {
        using var result = new SKBitmap(original.Width, original.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        
        for (int y = 0; y < original.Height; y++)
        {
            for (int x = 0; x < original.Width; x++)
            {
                var color = original.GetPixel(x, y);
                var maskVal = mask.GetPixel(x, y).Red; // Gray value
                
                result.SetPixel(x, y, new SKColor(color.Red, color.Green, color.Blue, maskVal));
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
