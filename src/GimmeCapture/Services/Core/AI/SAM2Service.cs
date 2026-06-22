using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Threading;
using GimmeCapture.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace GimmeCapture.Services.Core.AI;

public class SAM2Service : IDisposable
{
    /// <summary>Fixed square input resolution the SAM2 encoder expects (image is stretched to this grid).</summary>
    private const int Sam2InputSize = 1024;

    private readonly SAM2RuntimeService _runtimeService;
    private InferenceSession? _encoderSession;
    private InferenceSession? _decoderSession;
    private bool _isInitialized = false;
    private bool _isImagePrepared = false;
    private string? _preparedImageKey;
    private SAM2Variant? _initializedVariant;

    // SAM2 Hiera Tensors
    private DenseTensor<float>? _imageEmbeddings;
    private DenseTensor<float>? _highResFeat0;
    private DenseTensor<float>? _highResFeat1;
    private string? _encoderInputName;
    private bool _encoderInputIs5D;
    
    private int _originalWidth;
    private int _originalHeight;
    private string _lastIouInfo = "";
    private int _buildRev = 22; 

    public string LastIouInfo => _lastIouInfo;
    public string ModelInfo => GetModelInfo();
    public string ModelVariantName { get; private set; } = "Unknown";
    internal bool IsInitializedForTesting => _isInitialized;
    internal bool IsImagePreparedForTesting => _isImagePrepared;
    internal string? PreparedImageKeyForTesting => _preparedImageKey;

    private readonly AppSettingsService _settingsService;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _prepareLock = new(1, 1);

    public SAM2Service(SAM2RuntimeService runtimeService, AppSettingsService settingsService)
    {
        _runtimeService = runtimeService;
        _settingsService = settingsService;
    }

    public async Task InitializeAsync()
    {
        var variant = _settingsService.Settings.SelectedSAM2Variant;
        if (_isInitialized && _initializedVariant == variant && _encoderSession != null && _decoderSession != null)
            return;

        await _initLock.WaitAsync();
        try
        {
            variant = _settingsService.Settings.SelectedSAM2Variant;
            if (_isInitialized && _initializedVariant == variant && _encoderSession != null && _decoderSession != null)
                return;

            if (_initializedVariant.HasValue && _initializedVariant != variant)
            {
                _encoderSession = null;
                _decoderSession = null;
                _isInitialized = false;
                _encoderInputName = null;
                _encoderInputIs5D = false;
                InvalidatePreparedImage();
            }

            ModelVariantName = variant.ToString();

            // Use shared runtime sessions so SAM2 callers reuse the same warmed-up model pair.
            await _runtimeService.EnsureLoadedAndWarmedAsync(variant);
            var sessions = _runtimeService.GetSessions();
            
            _encoderSession = sessions.Encoder;
            _decoderSession = sessions.Decoder;

            if (_encoderSession == null || _decoderSession == null)
                throw new Exception($"SAM2 models for {variant} failed to load from cache.");

            _initializedVariant = variant;
            _isInitialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }


    public async Task SetImageAsync(byte[] imageBytes)
    {
        using var original = SKBitmap.Decode(imageBytes);
        if (original == null) return;
        await EnsureImagePreparedAsync(original);
    }

    public async Task SetImageAsync(SKBitmap original)
    {
        await EnsureImagePreparedAsync(original);
    }

    public async Task EnsureImagePreparedAsync(SKBitmap original)
    {
        if (!_isInitialized) await InitializeAsync();
        if (_encoderSession == null) return;

        string imageKey = BuildImagePreparationKey(original);
        if (_isImagePrepared && string.Equals(_preparedImageKey, imageKey, StringComparison.Ordinal))
        {
            return;
        }

        await _prepareLock.WaitAsync();
        try
        {
            if (!_isInitialized) await InitializeAsync();
            if (_encoderSession == null) return;

            imageKey = BuildImagePreparationKey(original);
            if (_isImagePrepared && string.Equals(_preparedImageKey, imageKey, StringComparison.Ordinal))
            {
                return;
            }

            await PrepareImageCoreAsync(original);
            _preparedImageKey = imageKey;
            _isImagePrepared = true;
        }
        finally
        {
            _prepareLock.Release();
        }
    }

    public void InvalidatePreparedImage()
    {
        _imageEmbeddings = null;
        _highResFeat0 = null;
        _highResFeat1 = null;
        _originalWidth = 0;
        _originalHeight = 0;
        _preparedImageKey = null;
        _isImagePrepared = false;
    }

    private async Task PrepareImageCoreAsync(SKBitmap original)
    {
        var encoderSession = _encoderSession ?? throw new InvalidOperationException("SAM2 encoder session is not initialized.");

        await Task.Run(() =>
        {
            _originalWidth = original.Width;
            _originalHeight = original.Height;

            ResolveEncoderInputMetadata(encoderSession);
            string inputName = _encoderInputName ?? "image";
            int bufferSize = 3 * Sam2InputSize * Sam2InputSize;
            float[] buffer = new float[bufferSize];

            {
                using var resized = original.Resize(new SKImageInfo(Sam2InputSize, Sam2InputSize), new SKSamplingOptions(SKFilterMode.Linear));
                if (resized == null)
                    throw new InvalidOperationException("Failed to resize image for SAM2 prepare.");

                if (resized.ColorType == SKColorType.Bgra8888 || resized.ColorType == SKColorType.Rgba8888)
                {
                    unsafe
                    {
                        byte* ptr = (byte*)resized.GetPixels().ToPointer();
                        int rowBytes = resized.RowBytes;
                        bool isBgra = resized.ColorType == SKColorType.Bgra8888;
                        float inv255 = 1.0f / 255.0f;

                        Parallel.For(0, Sam2InputSize, y =>
                        {
                            byte* row = ptr + (y * rowBytes);
                            int blueOffset = y * Sam2InputSize;
                            int greenOffset = blueOffset + (Sam2InputSize * Sam2InputSize);
                            int redOffset = greenOffset + (Sam2InputSize * Sam2InputSize);

                            for (int x = 0; x < Sam2InputSize; x++)
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

                                buffer[blueOffset + x] = b * inv255;
                                buffer[greenOffset + x] = g * inv255;
                                buffer[redOffset + x] = r * inv255;
                            }
                        });
                    }
                }
                else
                {
                    float inv255 = 1.0f / 255.0f;
                    Parallel.For(0, Sam2InputSize, y =>
                    {
                        int blueOffset = y * Sam2InputSize;
                        int greenOffset = blueOffset + (Sam2InputSize * Sam2InputSize);
                        int redOffset = greenOffset + (Sam2InputSize * Sam2InputSize);
                        for (int x = 0; x < Sam2InputSize; x++)
                        {
                            var color = resized.GetPixel(x, y);
                            buffer[blueOffset + x] = color.Blue * inv255;
                            buffer[greenOffset + x] = color.Green * inv255;
                            buffer[redOffset + x] = color.Red * inv255;
                        }
                    });
                }

                // Keep the encoder input shape on the same proven 4D path as the
                // previously working implementation. Some SAM2 exports report extra
                // dimensions in metadata, but the runtime still expects the classic
                // NCHW image tensor here.
                var inputTensor = new DenseTensor<float>(buffer, new[] { 1, 3, Sam2InputSize, Sam2InputSize });

                var inputs = new List<NamedOnnxValue>(1) { NamedOnnxValue.CreateFromTensor(inputName, inputTensor) };
                using var results = encoderSession.Run(inputs);

                var outputNames = results.AsValueEnumerable().Select(r => r.Name).ToList();

                T? FindResult<T>(string[] aliases) where T : class
                {
                    var found = results.AsValueEnumerable().FirstOrDefault(r => aliases.AsValueEnumerable().Any(a => r.Name == a || r.Name.Contains(a)));
                    return found?.Value as T;
                }

                _imageEmbeddings = FindResult<Tensor<float>>(new[] { "image_embeddings", "image_embed", "embeddings" })?.ToDenseTensor()
                    ?? throw new Exception($"Encoder Error: Missing embeddings. Got: {string.Join(", ", outputNames)}");

                _highResFeat0 = FindResult<Tensor<float>>(new[] { "high_res_feats_0", "feat_0", "high_res_feat_0" })?.ToDenseTensor()
                    ?? throw new Exception($"Encoder Error: Missing feat_0. Got: {string.Join(", ", outputNames)}");

                _highResFeat1 = FindResult<Tensor<float>>(new[] { "high_res_feats_1", "feat_1", "high_res_feat_1" })?.ToDenseTensor()
                    ?? throw new Exception($"Encoder Error: Missing feat_1. Got: {string.Join(", ", outputNames)}");
            }

        });
    }

    public async Task<List<Avalonia.Rect>> AutoDetectObjectsAsync(int gridDensity = 32, int maxObjects = 20, int minSize = 20, CancellationToken cancellationToken = default)
    {
        if (!_isInitialized || !_isImagePrepared || _decoderSession == null || _imageEmbeddings == null) return new List<Avalonia.Rect>();

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
            

            var decInputMetaData = _decoderSession.InputMetadata;
            var decInputNames = decInputMetaData.Keys.AsValueEnumerable().ToList();

            // Process each point individually (batch=1) in PARALLEL
            var lockObj = new object();

            Parallel.ForEach(points, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, (pt, state) =>
            {
                if (cancellationToken.IsCancellationRequested) return;
                
                // Early exit if we already have enough objects
                lock (lockObj)
                {
                    if (results.Count >= maxObjects)
                    {
                        state.Stop();
                        return;
                    }
                }
                
                try
                {
                    // Create batch=1 tensors
                    var decCoords = new DenseTensor<float>(new[] { 1, 1, 2 });
                    decCoords[0, 0, 0] = pt.X;
                    decCoords[0, 0, 1] = pt.Y;

                    var decLabels = new DenseTensor<float>(new[] { 1, 1 });
                    decLabels[0, 0] = 1f;

                    var maskInput = new DenseTensor<float>(new[] { 1, 1, 256, 256 });
                    var hasMaskInput = new DenseTensor<float>(new[] { 1 });

                    var decInputMetaData = _decoderSession.InputMetadata;
                    var decInputNames = decInputMetaData.Keys.AsValueEnumerable().ToList();
                    var inputs = new List<NamedOnnxValue>();

                    void AddInput(string[] aliases, float[] data, int[] dimensions) {
                        var name = decInputNames.AsValueEnumerable().FirstOrDefault(n => aliases.AsValueEnumerable().Any(a => n == a || n == a.Replace("_", "") || n.Contains(a)));
                        if (name == null) return;
                        
                        var meta = decInputMetaData[name];
                        var expectedDims = meta.Dimensions;
                        int[] finalDims = dimensions;
                        
                        // Handle batching/extra dimension if model expects it
                        if (expectedDims.Length == dimensions.Length + 1 && expectedDims.Length > 2)
                        {
                            finalDims = new int[expectedDims.Length];
                            finalDims[0] = dimensions[0];
                            finalDims[1] = 1;
                            for (int i = 1; i < dimensions.Length; i++) finalDims[i + 1] = dimensions[i];
                        }

                        if (meta.ElementType == typeof(int)) inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<int>(data.AsValueEnumerable().Select(x => (int)x).ToArray(), finalDims)));
                        else if (meta.ElementType == typeof(long)) inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(data.AsValueEnumerable().Select(x => (long)x).ToArray(), finalDims)));
                        else inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<float>(data, finalDims)));
                    }

                    void AddTensorInput(string[] aliases, DenseTensor<float> tensor) {
                        var name = decInputNames.AsValueEnumerable().FirstOrDefault(n => aliases.AsValueEnumerable().Any(a => n == a || n == a.Replace("_", "") || n.Contains(a)));
                        if (name == null) return;
                        
                        var expectedDims = decInputMetaData[name].Dimensions;
                        if (expectedDims.Length == 5 && tensor.Dimensions.Length == 4) {
                            var reshaped = new DenseTensor<float>(System.Linq.Enumerable.ToArray(tensor), new int[5] { 1, 1, tensor.Dimensions[1], tensor.Dimensions[2], tensor.Dimensions[3] });
                            inputs.Add(NamedOnnxValue.CreateFromTensor(name, reshaped));
                        } else inputs.Add(NamedOnnxValue.CreateFromTensor(name, tensor));
                    }

                    AddTensorInput(new[] { "image_embeddings", "image_embed", "embeddings", "image_embedding" }, _imageEmbeddings);
                    AddTensorInput(new[] { "high_res_feats_0", "feat_0", "high_res_feat_0" }, _highResFeat0!);
                    AddTensorInput(new[] { "high_res_feats_1", "feat_1", "high_res_feat_1" }, _highResFeat1!);
                    AddInput(new[] { "point_coords", "coords" }, System.Linq.Enumerable.ToArray(decCoords), decCoords.Dimensions.AsValueEnumerable().ToArray());
                    AddInput(new[] { "point_labels", "labels" }, System.Linq.Enumerable.ToArray(decLabels), decLabels.Dimensions.AsValueEnumerable().ToArray());
                    AddInput(new[] { "mask_input", "mask" }, System.Linq.Enumerable.ToArray(maskInput), maskInput.Dimensions.AsValueEnumerable().ToArray());
                    AddInput(new[] { "has_mask_input", "has_mask" }, System.Linq.Enumerable.ToArray(hasMaskInput), hasMaskInput.Dimensions.AsValueEnumerable().ToArray());
                    AddInput(new[] { "orig_im_size", "im_size" }, new float[] { 1024f, 1024f }, new[] { 2 });

                    using var result = _decoderSession.Run(inputs);
                    var masksOutput = result.AsValueEnumerable().FirstOrDefault(r => r.Name.Contains("mask") && !r.Name.Contains("low_res"))?.AsTensor<float>();
                    var iouOutput = result.AsValueEnumerable().FirstOrDefault(r => r.Name.Contains("iou"))?.AsTensor<float>();

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
                                if (rect.Width >= minSize && rect.Height >= minSize &&
                                    rect.Width < _originalWidth * 0.95 && rect.Height < _originalHeight * 0.95)
                                {
                                    lock (lockObj)
                                    {
                                        if (!results.AsValueEnumerable().Any(existing => IoU(existing, rect) > 0.5f))
                                        {
                                            if (results.Count < maxObjects)
                                            {
                                                results.Add(rect);
                                            }
                                            else
                                            {
                                                state.Stop();
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }

                }
                catch (Exception ex)
                {
                    AppLog.Warning("SAM2.GridPoint", ex);
                }
            });

            return results;
        });
    }

    public async Task<byte[]> GetMaskAsync(IEnumerable<(double X, double Y, bool IsPositive)> points)
    {
        if (!_isInitialized || !_isImagePrepared || _decoderSession == null || _imageEmbeddings == null || _highResFeat0 == null || _highResFeat1 == null) 
            return Array.Empty<byte>();

        return await Task.Run(() =>
        {
            var pointList = points.AsValueEnumerable().ToList();
            int n = pointList.Count;
            if (n == 0) return Array.Empty<byte>();

            // SAM2 CRITICAL: Points must scale to the 1024-grid (since image is stretched)
            float scaleX = 1024f / (float)_originalWidth;
            float scaleY = 1024f / (float)_originalHeight;
            
            var decCoords = new DenseTensor<float>(new[] { 1, n, 2 });
            var decLabels = new DenseTensor<float>(new[] { 1, n });

            for (int i = 0; i < n; i++)
            {
                var p = pointList[i];
                float aiX = (float)(p.X * scaleX);
                float aiY = (float)(p.Y * scaleY);
                decCoords[0, i, 0] = aiX;
                decCoords[0, i, 1] = aiY;
                decLabels[0, i] = p.IsPositive ? 1f : 0f;
                System.Diagnostics.Debug.WriteLine($"[AI MATH] Click({p.X:F0},{p.Y:F0}) -> AI_StretchGrid({aiX:F0},{aiY:F0}) Size=(1024x1024) BGR=1 Normal=0-1");
            }

            var maskInput = new DenseTensor<float>(new[] { 1, 1, 256, 256 });
            var hasMaskInput = new DenseTensor<float>(new[] { 1 });
            hasMaskInput[0] = 0f;

            var decInputMetaData = _decoderSession.InputMetadata;
            var decInputNames = decInputMetaData.Keys.AsValueEnumerable().ToList();
            var inputs = new List<NamedOnnxValue>();
            
            void AddInput(string[] aliases, float[] data, int[] dimensions) {
                var name = decInputNames.AsValueEnumerable().FirstOrDefault(n => aliases.AsValueEnumerable().Any(a => n == a || n == a.Replace("_", "") || n.Contains(a)));
                if (name == null) return;
                
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
                if (meta.ElementType == typeof(int)) inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<int>(data.AsValueEnumerable().Select(x => (int)x).ToArray(), finalDims)));
                else if (meta.ElementType == typeof(long)) inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(data.AsValueEnumerable().Select(x => (long)x).ToArray(), finalDims)));
                else inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<float>(data, finalDims)));
            }

            void AddTensorInput(string[] aliases, DenseTensor<float> tensor) {
               var name = decInputNames.AsValueEnumerable().FirstOrDefault(n => aliases.AsValueEnumerable().Any(a => n == a || n == a.Replace("_", "") || n.Contains(a)));
               if (name == null) return;
               
               var expectedDims = decInputMetaData[name].Dimensions;
               if (expectedDims.Length == 5 && tensor.Dimensions.Length == 4) {
                   var reshaped = new DenseTensor<float>(System.Linq.Enumerable.ToArray(tensor), new int[5] { 1, 1, tensor.Dimensions[1], tensor.Dimensions[2], tensor.Dimensions[3] });
                   inputs.Add(NamedOnnxValue.CreateFromTensor(name, reshaped));
               } else inputs.Add(NamedOnnxValue.CreateFromTensor(name, tensor));
            }

            AddTensorInput(new[] { "image_embeddings", "image_embed", "embeddings", "image_embedding" }, _imageEmbeddings);
            AddTensorInput(new[] { "high_res_feats_0", "feat_0", "high_res_feat_0" }, _highResFeat0!);
            AddTensorInput(new[] { "high_res_feats_1", "feat_1", "high_res_feat_1" }, _highResFeat1!);
            AddInput(new[] { "point_coords", "coords" }, System.Linq.Enumerable.ToArray(decCoords), decCoords.Dimensions.AsValueEnumerable().ToArray());
            AddInput(new[] { "point_labels", "labels" }, System.Linq.Enumerable.ToArray(decLabels), decLabels.Dimensions.AsValueEnumerable().ToArray());
            AddInput(new[] { "mask_input", "mask" }, System.Linq.Enumerable.ToArray(maskInput), maskInput.Dimensions.AsValueEnumerable().ToArray());
            AddInput(new[] { "has_mask_input", "has_mask" }, System.Linq.Enumerable.ToArray(hasMaskInput), hasMaskInput.Dimensions.AsValueEnumerable().ToArray());
            // SAM2 CRITICAL: orig_im_size must be [1024, 1024] when using stretching
            AddInput(new[] { "orig_im_size", "im_size" }, new float[] { 1024f, 1024f }, new[] { 2 });

            using var results = _decoderSession.Run(inputs);
            var masksResult = results.AsValueEnumerable().FirstOrDefault(r => r.Name == "upsampled_masks") ?? results.AsValueEnumerable().FirstOrDefault(r => r.Name == "masks") ?? results.AsValueEnumerable().FirstOrDefault(r => r.Name == "mask_values") ?? results.AsValueEnumerable().FirstOrDefault(r => r.Name.Contains("mask") && !r.Name.Contains("low_res"));
            var iouResult = results.AsValueEnumerable().FirstOrDefault(r => r.Name == "iou_predictions" || r.Name == "iou_prediction" || r.Name.Contains("iou"));

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

    public async Task<SKBitmap?> GetMaskBitmapAsync(IReadOnlyList<(double X, double Y, bool IsPositive)> points)
    {
        if (!_isInitialized || !_isImagePrepared || points.Count == 0)
            return null;

        var maskBytes = await GetMaskAsync(points);
        if (maskBytes.Length == 0)
            return null;

        return await Task.Run(() =>
        {
            using var decoded = SKBitmap.Decode(maskBytes);
            if (decoded == null)
                return (SKBitmap?)null;

            if (decoded.ColorType == SKColorType.Gray8)
                return decoded.Copy();

            var normalized = new SKBitmap(decoded.Width, decoded.Height, SKColorType.Gray8, SKAlphaType.Opaque);
            unsafe
            {
                byte* dstBase = (byte*)normalized.GetPixels().ToPointer();
                bool isBgra = decoded.ColorType == SKColorType.Bgra8888;
                bool isRgba = decoded.ColorType == SKColorType.Rgba8888;

                if (isBgra || isRgba)
                {
                    byte* srcBase = (byte*)decoded.GetPixels().ToPointer();
                    for (int y = 0; y < decoded.Height; y++)
                    {
                        byte* srcRow = srcBase + (y * decoded.RowBytes);
                        byte* dstRow = dstBase + (y * normalized.RowBytes);
                        for (int x = 0; x < decoded.Width; x++)
                        {
                            int pixelOffset = x * 4;
                            byte value = isBgra
                                ? srcRow[pixelOffset + 0]
                                : srcRow[pixelOffset + 2];
                            dstRow[x] = value;
                        }
                    }
                }
                else
                {
                    for (int y = 0; y < decoded.Height; y++)
                    {
                        byte* dstRow = dstBase + (y * normalized.RowBytes);
                        for (int x = 0; x < decoded.Width; x++)
                        {
                            var color = decoded.GetPixel(x, y);
                            dstRow[x] = color.Red;
                        }
                    }
                }
            }

            return normalized;
        });
    }

    private void ResolveEncoderInputMetadata(InferenceSession encoderSession)
    {
        if (!string.IsNullOrEmpty(_encoderInputName))
            return;

        var inputMetadata = encoderSession.InputMetadata;
        foreach (var name in inputMetadata.Keys)
        {
            if (name == "image" || name == "pixel_values")
            {
                _encoderInputName = name;
                _encoderInputIs5D = inputMetadata[name].Dimensions.AsValueEnumerable().Count() == 5;
                return;
            }
        }

        foreach (var name in inputMetadata.Keys)
        {
            _encoderInputName = name;
            _encoderInputIs5D = inputMetadata[name].Dimensions.AsValueEnumerable().Count() == 5;
            return;
        }

        _encoderInputName = "image";
        _encoderInputIs5D = false;
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

    private static string BuildImagePreparationKey(SKBitmap bitmap)
    {
        static string PixelKey(SKBitmap bmp, int x, int y)
        {
            var color = bmp.GetPixel(x, y);
            return $"{color.Red:X2}{color.Green:X2}{color.Blue:X2}{color.Alpha:X2}";
        }

        int maxX = Math.Max(bitmap.Width - 1, 0);
        int maxY = Math.Max(bitmap.Height - 1, 0);
        int midX = bitmap.Width / 2;
        int midY = bitmap.Height / 2;
        int thirdX = bitmap.Width / 3;
        int thirdY = bitmap.Height / 3;
        int twoThirdX = (bitmap.Width * 2) / 3;
        int twoThirdY = (bitmap.Height * 2) / 3;

        return string.Join("|",
            bitmap.Width,
            bitmap.Height,
            bitmap.ColorType,
            bitmap.AlphaType,
            PixelKey(bitmap, 0, 0),
            PixelKey(bitmap, maxX, 0),
            PixelKey(bitmap, 0, maxY),
            PixelKey(bitmap, maxX, maxY),
            PixelKey(bitmap, midX, midY),
            PixelKey(bitmap, thirdX, thirdY),
            PixelKey(bitmap, twoThirdX, twoThirdY));
    }


    public string GetModelInfo()
    {
        if (!_isInitialized) return "Not Initialized";
        // CRITICAL: Build Timestamp to verify DLL update
        var info = $"[BUILD: {DateTime.Now:HH:mm:ss}]\n";
        info += "Encoder Inputs:\n";
        foreach (var input in _encoderSession!.InputMetadata)
            info += $"  - {input.Key}: {string.Join("x", input.Value.Dimensions.AsValueEnumerable().ToArray())}\n";
        info += "Encoder Outputs:\n";
        foreach (var output in _encoderSession!.OutputMetadata)
            info += $"  - {output.Key}: {string.Join("x", output.Value.Dimensions.AsValueEnumerable().ToArray())}\n";
        info += "Decoder Inputs:\n";
        foreach (var input in _decoderSession!.InputMetadata)
            info += $"  - {input.Key}: {string.Join("x", input.Value.Dimensions.AsValueEnumerable().ToArray())}\n";
        info += "Decoder Outputs:\n";
        foreach (var output in _decoderSession!.OutputMetadata)
            info += $"  - {output.Key}: {string.Join("x", output.Value.Dimensions.AsValueEnumerable().ToArray())}\n";
        return info;
    }

    public void Dispose()
    {
        // 清除所有張量參考，允許 GC 回收大型記憶體區塊
        _imageEmbeddings = null;
        _highResFeat0 = null;
        _highResFeat1 = null;
        
        // 清除 session 參考（不 Dispose，session 由 AIResourceService 管理）
        _encoderSession = null;
        _decoderSession = null;
        _isInitialized = false;
    }
}
