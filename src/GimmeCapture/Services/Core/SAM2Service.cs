using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace GimmeCapture.Services.Core;

public class SAM2Service : IDisposable
{
    private readonly AIResourceService _resourceService;
    private InferenceSession? _encoderSession;
    private InferenceSession? _decoderSession;
    private bool _isInitialized = false;

    // SAM2 Hiera Tensors
    private DenseTensor<float>? _imageEmbeddings;
    private DenseTensor<float>? _highResFeat0;
    private DenseTensor<float>? _highResFeat1;
    
    private int _originalWidth;
    private int _originalHeight;
    private string _lastIouInfo = "";
    private int _buildRev = 22; 

    public string LastIouInfo => _lastIouInfo;
    public string ModelInfo => GetModelInfo();
    public string ModelVariantName { get; private set; } = "Unknown";

    private readonly AppSettingsService _settingsService;

    public SAM2Service(AIResourceService resourceService, AppSettingsService settingsService)
    {
        _resourceService = resourceService;
        _settingsService = settingsService;
    }

    public async Task InitializeAsync()
    {
        if (_isInitialized) return;

        _resourceService.SetupNativeResolvers();

        var variant = _settingsService.Settings.SelectedSAM2Variant;
        ModelVariantName = variant.ToString();

        // Use cached sessions from Resource Service to avoid redundant loading
        await _resourceService.LoadSAM2ModelsAsync(variant);
        var sessions = _resourceService.GetSAM2Sessions();
        
        _encoderSession = sessions.Encoder;
        _decoderSession = sessions.Decoder;

        if (_encoderSession == null || _decoderSession == null)
            throw new Exception($"SAM2 models for {variant} failed to load from cache.");

        // New: Warmup models to avoid first-run lag (kernels initialization, etc)
        await WarmupAsync();

        _isInitialized = true;
    }

    public async Task WarmupAsync()
    {
        if (_encoderSession == null || _decoderSession == null) return;

        System.Diagnostics.Debug.WriteLine("[AI] Warming up SAM2 models...");
        await Task.Run(() =>
        {
            try
            {
                // Encoder Warmup
                var encoderInput = new DenseTensor<float>(new[] { 1, 3, 1024, 1024 });
                var encInputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("image", encoderInput) };
                
                // Note: Models might have different input names, but InitializeAsync handles finding them
                // Here we just use a generic set to trigger kernel loading for typical shapes
                using var encResults = _encoderSession.Run(encInputs);

                // Decoder Warmup (Requires mock embeddings and points)
                var decInputMetaData = _decoderSession.InputMetadata;
                var decInputNames = decInputMetaData.Keys.ToList();
                var decInputs = new List<NamedOnnxValue>();

                void AddMock(string[] aliases, int[] dims)
                {
                    var name = decInputNames.FirstOrDefault(n => aliases.Any(a => n == a || n.Contains(a)));
                    if (name == null) return;
                    decInputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<float>(dims)));
                }

                AddMock(new[] { "image_embeddings" }, new[] { 1, 256, 64, 64 });
                AddMock(new[] { "high_res_feats_0" }, new[] { 1, 32, 256, 256 });
                AddMock(new[] { "high_res_feats_1" }, new[] { 1, 64, 128, 128 });
                AddMock(new[] { "point_coords" }, new[] { 1, 1, 2 });
                AddMock(new[] { "point_labels" }, new[] { 1, 1 });
                AddMock(new[] { "mask_input" }, new[] { 1, 1, 256, 256 });
                AddMock(new[] { "has_mask_input" }, new[] { 1 });
                AddMock(new[] { "orig_im_size" }, new[] { 2 });

                using var decResults = _decoderSession.Run(decInputs);
                System.Diagnostics.Debug.WriteLine("[AI] SAM2 Warmup complete.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AI] Warmup Warning (Non-fatal): {ex.Message}");
            }
        });
    }

    public async Task SetImageAsync(byte[] imageBytes)
    {
        using var original = SKBitmap.Decode(imageBytes);
        if (original == null) return;
        await SetImageAsync(original);
    }

    public async Task SetImageAsync(SKBitmap original)
    {
        if (!_isInitialized) await InitializeAsync();
        if (_encoderSession == null) return;

        await Task.Run(() =>
        {
            _originalWidth = original.Width;
            _originalHeight = original.Height;

            var inputMetaData = _encoderSession.InputMetadata;
            var inputNames = inputMetaData.Keys.ToList();
            string inputName = inputNames.FirstOrDefault(n => n == "image" || n == "pixel_values") ?? inputNames.FirstOrDefault() ?? "image";
            
            var expectedDims = inputMetaData[inputName].Dimensions.ToArray();
            bool is5D = expectedDims.Length == 5;
            int bufferSize = 3 * 1024 * 1024;
            float[] buffer = new float[bufferSize];

            // Optimization: Resize using SkiaSharp once
            using var resized = original.Resize(new SKImageInfo(1024, 1024), new SKSamplingOptions(SKFilterMode.Linear));
            
            // Optimization: Fast parallelized pixel access
            if (resized.ColorType == SKColorType.Bgra8888 || resized.ColorType == SKColorType.Rgba8888)
            {
                unsafe
                {
                    byte* ptr = (byte*)resized.GetPixels().ToPointer();
                    int rowBytes = resized.RowBytes;
                    bool isBgra = resized.ColorType == SKColorType.Bgra8888;
                    float inv255 = 1.0f / 255.0f;

                    // Parallel processing for all 1024 rows
                    Parallel.For(0, 1024, y =>
                    {
                        byte* row = ptr + (y * rowBytes);
                        int blueOffset = y * 1024;
                        int greenOffset = blueOffset + (1024 * 1024);
                        int redOffset = greenOffset + (1024 * 1024);

                        for (int x = 0; x < 1024; x++)
                        {
                            byte b, g, r;
                            if (isBgra)
                            {
                                b = row[x * 4 + 0];
                                g = row[x * 4 + 1];
                                r = row[x * 4 + 2];
                            }
                            else
                            {
                                r = row[x * 4 + 0];
                                g = row[x * 4 + 1];
                                b = row[x * 4 + 2];
                            }

                            // SAM2 Order: [B, G, R]
                            buffer[blueOffset + x] = b * inv255;
                            buffer[greenOffset + x] = g * inv255;
                            buffer[redOffset + x] = r * inv255;
                        }
                    });
                }
            }
            else
            {
                // Fallback for non-8888 (Rare)
                float inv255 = 1.0f / 255.0f;
                Parallel.For(0, 1024, y =>
                {
                    int blueOffset = y * 1024;
                    int greenOffset = blueOffset + (1024 * 1024);
                    int redOffset = greenOffset + (1024 * 1024);
                    for (int x = 0; x < 1024; x++)
                    {
                        var color = resized.GetPixel(x, y);
                        buffer[blueOffset + x] = color.Blue * inv255;
                        buffer[greenOffset + x] = color.Green * inv255;
                        buffer[redOffset + x] = color.Red * inv255;
                    }
                });
            }

            // Zero-copy tensor creation
            var inputTensor = is5D 
                ? new DenseTensor<float>(buffer, new[] { 1, 1, 3, 1024, 1024 })
                : new DenseTensor<float>(buffer, new[] { 1, 3, 1024, 1024 });

            var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(inputName, inputTensor) };
            using var results = _encoderSession.Run(inputs);
            
            var outputNames = results.Select(r => r.Name).ToList();
            
            T? FindResult<T>(string[] aliases) where T : class {
                var found = results.FirstOrDefault(r => aliases.Any(a => r.Name == a || r.Name.Contains(a)));
                return found?.Value as T;
            }

            _imageEmbeddings = FindResult<Tensor<float>>(new[] { "image_embeddings", "image_embed", "embeddings" })?.ToDenseTensor()
                ?? throw new Exception($"Encoder Error: Missing embeddings. Got: {string.Join(", ", outputNames)}");
                
            _highResFeat0 = FindResult<Tensor<float>>(new[] { "high_res_feats_0", "feat_0", "high_res_feat_0" })?.ToDenseTensor()
                ?? throw new Exception($"Encoder Error: Missing feat_0. Got: {string.Join(", ", outputNames)}");

            _highResFeat1 = FindResult<Tensor<float>>(new[] { "high_res_feats_1", "feat_1", "high_res_feat_1" })?.ToDenseTensor()
                ?? throw new Exception($"Encoder Error: Missing feat_1. Got: {string.Join(", ", outputNames)}");
            
        });
    }

    public async Task<List<Avalonia.Rect>> AutoDetectObjectsAsync(int gridDensity = 32, CancellationToken cancellationToken = default)
    {
        if (!_isInitialized || _decoderSession == null || _imageEmbeddings == null) return new List<Avalonia.Rect>();

        return await Task.Run(() =>
        {
            var results = new List<Avalonia.Rect>();

            // 1. Generate Grid Points (use lower density to avoid too many inference calls)
            var points = new List<(float X, float Y)>();
            // Limit to 32 = 1024 points max for maximum coverage of small icons
            int effectiveDensity = Math.Min(gridDensity, 32);
            for (int r = 1; r < effectiveDensity; r++)
            {
                for (int c = 1; c < effectiveDensity; c++)
                {
                    points.Add(((float)c / effectiveDensity * 1024f, (float)r / effectiveDensity * 1024f));
                }
            }
            
            Console.WriteLine($"[AI Scan] Processing {points.Count} grid points...");

            var decInputMetaData = _decoderSession.InputMetadata;
            var decInputNames = decInputMetaData.Keys.ToList();

            // Process each point individually (batch=1) in PARALLEL
            int processedCount = 0;
            var lockObj = new object();

            Parallel.ForEach(points, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, (pt) =>
            {
                if (cancellationToken.IsCancellationRequested) return;
                
                try
                {
                    // Create batch=1 tensors
                    var coords = new DenseTensor<float>(new[] { 1, 1, 2 });
                    coords[0, 0, 0] = pt.X;
                    coords[0, 0, 1] = pt.Y;

                    var maskInput = new DenseTensor<float>(new[] { 1, 1, 256, 256 });
                    var hasMaskInput = new DenseTensor<float>(new[] { 1 });

                    var inputs = new List<NamedOnnxValue>();

                     // Add embeddings (batch=1, no tiling needed)
                    // SAM2 CRITICAL: Check for 5D input [B, 1, C, H, W] required by some ONNX exports
                    void AddTensorInput(string[] aliases, DenseTensor<float> tensor)
                    {
                        var name = decInputNames.FirstOrDefault(n => aliases.Any(a => n == a || n.Contains(a)));
                        if (name == null) return;

                        var expectedDims = decInputMetaData[name].Dimensions;
                        if (expectedDims.Length == 5 && tensor.Dimensions.Length == 4)
                        {
                            var reshaped = new DenseTensor<float>(tensor.ToArray(), new[] { 1, 1, tensor.Dimensions[1], tensor.Dimensions[2], tensor.Dimensions[3] });
                            inputs.Add(NamedOnnxValue.CreateFromTensor(name, reshaped));
                        }
                        else
                        {
                            inputs.Add(NamedOnnxValue.CreateFromTensor(name, tensor));
                        }
                    }

                    AddTensorInput(new[] { "image_embeddings", "image_embed" }, _imageEmbeddings);
                    if (_highResFeat0 != null) AddTensorInput(new[] { "high_res_feats_0", "feat_0" }, _highResFeat0);
                    if (_highResFeat1 != null) AddTensorInput(new[] { "high_res_feats_1", "feat_1" }, _highResFeat1);

                    // Add point inputs
                    var coordName = decInputNames.FirstOrDefault(n => n.Contains("point_coords") || n.Contains("coords"));
                    if (coordName != null) inputs.Add(NamedOnnxValue.CreateFromTensor(coordName, coords));

                    // Labels with type conversion
                    var labelName = decInputNames.FirstOrDefault(n => n.Contains("point_labels") || n.Contains("labels"));
                    if (labelName != null)
                    {
                        var meta = decInputMetaData[labelName];
                        if (meta.ElementType == typeof(int))
                        {
                            var intLabels = new DenseTensor<int>(new[] { 1, 1 });
                            intLabels[0, 0] = 1;
                            inputs.Add(NamedOnnxValue.CreateFromTensor(labelName, intLabels));
                        }
                        else
                        {
                            var labels = new DenseTensor<float>(new[] { 1, 1 });
                            labels[0, 0] = 1f;
                            inputs.Add(NamedOnnxValue.CreateFromTensor(labelName, labels));
                        }
                    }

                    // Mask inputs
                    var maskInName = decInputNames.FirstOrDefault(n => n.Contains("mask_input") && !n.Contains("has_mask"));
                    if (maskInName != null) inputs.Add(NamedOnnxValue.CreateFromTensor(maskInName, maskInput));

                    var hasMaskName = decInputNames.FirstOrDefault(n => n.Contains("has_mask"));
                    if (hasMaskName != null) inputs.Add(NamedOnnxValue.CreateFromTensor(hasMaskName, hasMaskInput));

                    // orig_im_size with type conversion
                    var sizeName = decInputNames.FirstOrDefault(n => n.Contains("orig_im_size") || n.Contains("im_size"));
                    if (sizeName != null)
                    {
                        var sizeMeta = decInputMetaData[sizeName];
                        if (sizeMeta.ElementType == typeof(int))
                        {
                            var intSizes = new DenseTensor<int>(new[] { 2 });
                            intSizes[0] = 1024; intSizes[1] = 1024;
                            inputs.Add(NamedOnnxValue.CreateFromTensor(sizeName, intSizes));
                        }
                        else
                        {
                            var sizes = new DenseTensor<float>(new[] { 2 });
                            sizes[0] = 1024f; sizes[1] = 1024f;
                            inputs.Add(NamedOnnxValue.CreateFromTensor(sizeName, sizes));
                        }
                    }

                    using var result = _decoderSession.Run(inputs);
                    var masksOutput = result.FirstOrDefault(r => r.Name.Contains("mask") && !r.Name.Contains("low_res"))?.AsTensor<float>();
                    var iouOutput = result.FirstOrDefault(r => r.Name.Contains("iou"))?.AsTensor<float>();

                    if (masksOutput != null && iouOutput != null)
                    {
                        int mh = masksOutput.Dimensions[2], mw = masksOutput.Dimensions[3];

                        // Pick best mask
                        int bestM = 0;
                        float bestIou = -1;
                        for (int m = 0; m < iouOutput.Dimensions[1]; m++)
                        {
                            if (iouOutput[0, m] > bestIou) { bestIou = iouOutput[0, m]; bestM = m; }
                        }

                        if (bestIou >= 0.60f) 
                        {
                            // Extract bounding box from mask
                            float minX = mw, minY = mh, maxX = 0, maxY = 0;
                            int count = 0;
                            for (int y = 0; y < mh; y += 4)
                            for (int x = 0; x < mw; x += 4)
                            {
                                if (masksOutput[0, bestM, y, x] > 0)
                                {
                                    if (x < minX) minX = x;
                                    if (x > maxX) maxX = x;
                                    if (y < minY) minY = y;
                                    if (y > maxY) maxY = y;
                                    count++;
                                }
                            }

                            if (count > 5)
                            {
                                var rect = new Avalonia.Rect(
                                    minX / mw * _originalWidth,
                                    minY / mh * _originalHeight,
                                    (maxX - minX) / mw * _originalWidth,
                                    (maxY - minY) / mh * _originalHeight);

                                // Filter noise 
                                if (rect.Width >= 20 && rect.Height >= 20 &&
                                    rect.Width < _originalWidth * 0.95 && rect.Height < _originalHeight * 0.95)
                                {
                                    lock (lockObj)
                                    {
                                        if (!results.Any(existing => IoU(existing, rect) > 0.5f))
                                        {
                                            results.Add(rect);
                                        }
                                    }
                                }
                            }
                        }
                    }

                    lock (lockObj)
                    {
                        processedCount++;
                        if (processedCount % 10 == 0)
                            Console.WriteLine($"[AI Scan] Processed {processedCount}/{points.Count} points, found {results.Count} objects");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AI Scan] Point ({pt.X:F0},{pt.Y:F0}) failed: {ex.Message}");
                }
            });

            Console.WriteLine($"[AI Scan] Complete: {results.Count} objects found");
            return results;
        });
    }

    public async Task<byte[]> GetMaskAsync(IEnumerable<(double X, double Y, bool IsPositive)> points)
    {
        if (!_isInitialized || _decoderSession == null || _imageEmbeddings == null || _highResFeat0 == null || _highResFeat1 == null) 
            return Array.Empty<byte>();

        return await Task.Run(() =>
        {
            var pointList = points.ToList();
            int n = pointList.Count;
            if (n == 0) return Array.Empty<byte>();

            // SAM2 CRITICAL: Points must scale to the 1024-grid (since image is stretched)
            float scaleX = 1024f / (float)_originalWidth;
            float scaleY = 1024f / (float)_originalHeight;
            
            var coords = new DenseTensor<float>(new[] { 1, n, 2 });
            var labels = new DenseTensor<float>(new[] { 1, n });

            for (int i = 0; i < n; i++)
            {
                var p = pointList[i];
                float aiX = (float)(p.X * scaleX);
                float aiY = (float)(p.Y * scaleY);
                coords[0, i, 0] = aiX;
                coords[0, i, 1] = aiY;
                labels[0, i] = p.IsPositive ? 1f : 0f;
                System.Diagnostics.Debug.WriteLine($"[AI MATH] Click({p.X:F0},{p.Y:F0}) -> AI_StretchGrid({aiX:F0},{aiY:F0}) Size=(1024x1024) BGR=1 Normal=0-1");
            }

            var maskInput = new DenseTensor<float>(new[] { 1, 1, 256, 256 });
            var hasMaskInput = new DenseTensor<float>(new[] { 1 });
            hasMaskInput[0] = 0f;

            var decInputMetaData = _decoderSession.InputMetadata;
            var decInputNames = decInputMetaData.Keys.ToList();
            var inputs = new List<NamedOnnxValue>();
            
            void AddInput(string[] aliases, float[] data, int[] dimensions) {
                var name = decInputNames.FirstOrDefault(n => aliases.Any(a => n == a || n == a.Replace("_", "") || n.Contains(a)));
                if (name == null) {
                    System.Diagnostics.Debug.WriteLine($"[AI] Skipping input {aliases.First()} - Not required by model");
                    return;
                }
                
                var meta = decInputMetaData[name];
                var expectedDims = meta.Dimensions;
                int[] finalDims = dimensions;
                if (expectedDims.Length == dimensions.Length + 1 && expectedDims.Length > 2)
                {
                    finalDims = new int[expectedDims.Length];
                    finalDims[0] = dimensions[0];
                    finalDims[1] = 1;
                    for (int i = 1; i < dimensions.Length; i++) finalDims[i + 1] = dimensions[i];
                }
                if (meta.ElementType == typeof(int)) inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<int>(data.Select(x => (int)x).ToArray(), finalDims)));
                else if (meta.ElementType == typeof(long)) inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(data.Select(x => (long)x).ToArray(), finalDims)));
                else inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<float>(data, finalDims)));
            }

            void AddTensorInput(string[] aliases, DenseTensor<float> tensor) {
               var name = decInputNames.FirstOrDefault(n => aliases.Any(a => n == a || n == a.Replace("_", "") || n.Contains(a)));
               if (name == null) {
                   System.Diagnostics.Debug.WriteLine($"[AI] Skipping tensor {aliases.First()} - Not required by model");
                   return;
               }
               
               var expectedDims = decInputMetaData[name].Dimensions;
               if (expectedDims.Length == 5 && tensor.Dimensions.Length == 4) {
                   var reshaped = new DenseTensor<float>(tensor.ToArray(), new int[5] { 1, 1, tensor.Dimensions[1], tensor.Dimensions[2], tensor.Dimensions[3] });
                   inputs.Add(NamedOnnxValue.CreateFromTensor(name, reshaped));
               } else inputs.Add(NamedOnnxValue.CreateFromTensor(name, tensor));
            }

            AddTensorInput(new[] { "image_embeddings", "image_embed", "embeddings" }, _imageEmbeddings);
            AddTensorInput(new[] { "high_res_feats_0", "feat_0", "high_res_feat_0" }, _highResFeat0);
            AddTensorInput(new[] { "high_res_feats_1", "feat_1", "high_res_feat_1" }, _highResFeat1);
            AddInput(new[] { "point_coords", "coords" }, coords.ToArray(), coords.Dimensions.ToArray());
            AddInput(new[] { "point_labels", "labels" }, labels.ToArray(), labels.Dimensions.ToArray());
            AddInput(new[] { "mask_input", "mask" }, maskInput.ToArray(), maskInput.Dimensions.ToArray());
            AddInput(new[] { "has_mask_input", "has_mask" }, hasMaskInput.ToArray(), hasMaskInput.Dimensions.ToArray());
            // SAM2 CRITICAL: orig_im_size must be [1024, 1024] when using stretching
            AddInput(new[] { "orig_im_size", "im_size" }, new float[] { 1024f, 1024f }, new[] { 2 });

            using var results = _decoderSession.Run(inputs);
            var masksResult = results.FirstOrDefault(r => r.Name == "upsampled_masks") ?? results.FirstOrDefault(r => r.Name == "masks") ?? results.FirstOrDefault(r => r.Name == "mask_values") ?? results.FirstOrDefault(r => r.Name.Contains("mask") && !r.Name.Contains("low_res"));
            var iouResult = results.FirstOrDefault(r => r.Name == "iou_predictions" || r.Name == "iou_prediction" || r.Name.Contains("iou"));

            if (masksResult == null || iouResult == null) throw new Exception("Decoder Error: Missing masks/IOU.");

            var masksTensor = masksResult.AsTensor<float>();
            var iouTensor = iouResult.AsTensor<float>();

            int bestIdx = 0;
            float maxScore = -1000f;
            float bestIou = 0, bestDensity = 0;
            string bestPointStats = "";
            int numMasks = iouTensor.Dimensions[1];
            int mh = masksTensor.Dimensions[2], mw = masksTensor.Dimensions[3];
            float gxFactor = (float)mw / 1024f, gyFactor = (float)mh / 1024f;
            int activeMw = mw, activeMh = mh;

            for(int i=0; i<numMasks; i++)
            {
                // REV22: 先計算此 mask 的值域來決定閾值
                float minVal = float.MaxValue, maxVal = float.MinValue;
                for(int sy=0; sy<mh; sy+=10) {
                    for(int sx=0; sx<mw; sx+=10) {
                        float v = masksTensor[0, i, sy, sx];
                        if (v < minVal) minVal = v;
                        if (v > maxVal) maxVal = v;
                    }
                }
                float threshold = (maxVal > 10) ? 127.5f : (minVal < -1 ? 0f : 0.5f);
                
                float iou = iouTensor[0, i];
                int pointErrors = 0;
                string pointStats = $"R:{minVal:F0}~{maxVal:F0}T:{threshold:F1} ";
                for(int pIdx=0; pIdx < pointList.Count; pIdx++) {
                    var p = pointList[pIdx];
                    int gx = (int)Math.Clamp(p.X * scaleX * gxFactor, 0, mw - 1);
                    int gy = (int)Math.Clamp(p.Y * scaleY * gyFactor, 0, mh - 1);

                    float val = masksTensor[0, i, gy, gx];
                    float valSwap = masksTensor[0, i, Math.Clamp(gx, 0, mh-1), Math.Clamp(gy, 0, mw-1)];
                    
                    bool hasMask = val > threshold;
                    if (p.IsPositive && !hasMask) pointErrors++;
                    if (!p.IsPositive && hasMask) pointErrors++;
                    
                    string xyTag = (val <= threshold && valSwap > threshold) ? "[SWP?]" : "";
                    pointStats += $"P{pIdx}({gx},{gy})={val:F0}{xyTag}|";
                    
                    // Nuclear Debug: Only first point, first mask
                    if (i == 0 && pIdx == 0) {
                        bestPointStats = pointStats;
                    }
                }

                int count = 0;
                for(int y=0; y<activeMh; y++)
                    for(int x=0; x<activeMw; x++)
                        if (masksTensor[0, i, Math.Min(y, mh-1), Math.Min(x, mw-1)] > threshold) count++;
                
                float density = (float)count / Math.Max(1, activeMw * activeMh);
                float currentScore = iou;
                if (pointErrors > 0) currentScore -= (pointErrors * 50.0f); 
                if (density > 0.45f) currentScore -= 10.0f;
                else if (density < 0.25f && density > 0.01f) currentScore += 2.0f; 

                // NOTE: Corner check moved outside loop

                System.Diagnostics.Debug.WriteLine($"[AI BRAIN] Mask {i}: IOU={iou:F2} D={density:F2} Err={pointErrors} Score={currentScore:F2} {pointStats}");

                if (currentScore > maxScore) {
                    maxScore = currentScore; bestIdx = i; bestIou = iou; bestDensity = density; bestPointStats = pointStats;
                }
            }

            // 根據最佳 mask 重新計算閾值
            float finalMinVal = float.MaxValue, finalMaxVal = float.MinValue;
            for(int sy=0; sy<mh; sy+=5) {
                for(int sx=0; sx<mw; sx+=5) {
                    float v = masksTensor[0, bestIdx, sy, sx];
                    if (v < finalMinVal) finalMinVal = v;
                    if (v > finalMaxVal) finalMaxVal = v;
                }
            }
            float finalThreshold = (finalMaxVal > 10) ? 127.5f : (finalMinVal < -1 ? 0f : 0.5f);

            bool finalTouchesLT = masksTensor[0, bestIdx, 1, 1] > finalThreshold;
            bool finalTouchesRT = masksTensor[0, bestIdx, 1, mw - 2] > finalThreshold;
            bool finalTouchesLB = masksTensor[0, bestIdx, mh - 2, 1] > finalThreshold;
            bool finalTouchesRB = masksTensor[0, bestIdx, mh - 2, mw - 2] > finalThreshold;
            bool isAllSelect = finalTouchesLT && finalTouchesRT && finalTouchesLB && finalTouchesRB;
            
            // 如果四角都選中且密度很高，嘗試反轉 mask
            bool invertMask = isAllSelect && bestDensity > 0.7f;
            string invTag = invertMask ? "[INV!]" : "";

            System.Diagnostics.Debug.WriteLine($"[AI FINAL] Mask {bestIdx}: Min={finalMinVal:F1} Max={finalMaxVal:F1} Thresh={finalThreshold} Inv={invertMask}");

            _lastIouInfo = $"REV:{_buildRev} {invTag}{bestPointStats} | D={bestDensity:F1}";

            // Extract result & RESIZE BACK TO ORIGINAL (Not 1024)
            using var maskFull = new SKBitmap(mw, mh, SKColorType.Gray8, SKAlphaType.Opaque);
            for (int y = 0; y < mh; y++)
            {
                for (int x = 0; x < mw; x++)
                {
                    bool isSelected = masksTensor[0, bestIdx, y, x] > finalThreshold;
                    if (invertMask) isSelected = !isSelected;
                    maskFull.SetPixel(x, y, isSelected ? SKColors.White : SKColors.Black);
                }
            }

            // CRITICAL: Resize the mask back to the ORIGINAL dimensions so it aligns in the UI
            using var resizedMask = maskFull.Resize(new SKImageInfo(_originalWidth, _originalHeight), new SKSamplingOptions(SKFilterMode.Linear));

            using var ms = new MemoryStream();
            resizedMask.Encode(ms, SKEncodedImageFormat.Png, 100);
            return ms.ToArray();
        });
    }


    private float IoU(Avalonia.Rect a, Avalonia.Rect b)
    {
        var intersect = a.Intersect(b);
        if (intersect.Width <= 0 || intersect.Height <= 0) return 0;
        float areaA = (float)(a.Width * a.Height);
        float areaB = (float)(b.Width * b.Height);
        float areaI = (float)(intersect.Width * intersect.Height);
        return areaI / (areaA + areaB - areaI);
    }


    public string GetModelInfo()
    {
        if (!_isInitialized) return "Not Initialized";
        // CRITICAL: Build Timestamp to verify DLL update
        var info = $"[BUILD: {DateTime.Now:HH:mm:ss}]\n";
        info += "Encoder Inputs:\n";
        foreach (var input in _encoderSession!.InputMetadata)
            info += $"  - {input.Key}: {string.Join("x", input.Value.Dimensions.ToArray())}\n";
        info += "Encoder Outputs:\n";
        foreach (var output in _encoderSession!.OutputMetadata)
            info += $"  - {output.Key}: {string.Join("x", output.Value.Dimensions.ToArray())}\n";
        info += "Decoder Inputs:\n";
        foreach (var input in _decoderSession!.InputMetadata)
            info += $"  - {input.Key}: {string.Join("x", input.Value.Dimensions.ToArray())}\n";
        info += "Decoder Outputs:\n";
        foreach (var output in _decoderSession!.OutputMetadata)
            info += $"  - {output.Key}: {string.Join("x", output.Value.Dimensions.ToArray())}\n";
        return info;
    }

    public void Dispose()
    {
        // Do NOT dispose shared inference sessions as they are owned by AIResourceService now
        // _imageEmbeddings is managed memory, no need to dispose
        _imageEmbeddings = null; 
    }
}
