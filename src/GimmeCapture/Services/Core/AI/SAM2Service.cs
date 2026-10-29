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

/// <summary>
/// What the SAM2 encoder made of one image (the Hiera embeddings and high-resolution features), with that image's size:
/// everything the decoder needs for a click on it.
/// </summary>
internal sealed record Sam2ImageEncoding(
    int Width,
    int Height,
    DenseTensor<float> ImageEmbeddings,
    DenseTensor<float> HighResFeat0,
    DenseTensor<float> HighResFeat1);

public class SAM2Service : GimmeCapture.Services.Abstractions.ISam2Service
{
    /// <summary>Fixed square input resolution the SAM2 encoder expects (image is stretched to this grid).</summary>
    internal const int Sam2InputSize = 1024;

    private readonly SAM2RuntimeService _runtimeService;
    private InferenceSession? _encoderSession;
    private InferenceSession? _decoderSession;
    private bool _isInitialized = false;
    private SAM2Variant? _initializedVariant;

    /// <summary>An image the encoder has made ready for clicks, and the key of the image it was made from.</summary>
    private sealed record PreparedImage(string Key, Sam2ImageEncoding Encoding);

    // The image prepared last, kept whole so that a click reads one image's embeddings with that image's size, and
    // nothing at all once an invalidation has cleared it. It used to be seven fields, and InvalidatePreparedImage zeroing
    // the size under a prepare still running left that prepare marking its image prepared at size 0.
    private PreparedImage? _preparedImage;
    // Moves on at every invalidation, under _preparedImageLock. A prepare publishes what the encoder made only if it has
    // not moved since the prepare began.
    private int _preparedImageGeneration;
    private readonly object _preparedImageLock = new();
    // Set by Dispose: the pin has let go of this service, which then prepares nothing more.
    private bool _disposed;

    private string? _encoderInputName;
    private bool _encoderInputIs5D;
    
    private string _lastIouInfo = "";
    private int _buildRev = 22; 

    public string LastIouInfo => _lastIouInfo;
    public string ModelInfo => GetModelInfo();
    public string ModelVariantName { get; private set; } = "Unknown";
    public bool IsImagePrepared => Volatile.Read(ref _preparedImage) != null;
    internal bool IsInitializedForTesting => _isInitialized;
    internal bool IsImagePreparedForTesting => IsImagePrepared;
    internal string? PreparedImageKeyForTesting => Volatile.Read(ref _preparedImage)?.Key;
    internal (int Width, int Height) PreparedImageSizeForTesting =>
        Volatile.Read(ref _preparedImage) is { } prepared ? (prepared.Encoding.Width, prepared.Encoding.Height) : (0, 0);

    private readonly IAppSettingsService _settingsService;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _prepareLock = new(1, 1);

    public SAM2Service(SAM2RuntimeService runtimeService, IAppSettingsService settingsService)
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


    public async Task EnsureImagePreparedAsync(SKBitmap original)
    {
        // A service the pin has let go of (Esc, another tool) prepares nothing: loading the model again for a click
        // that was still on its way would leave it loaded with no lease left to unload it.
        if (Volatile.Read(ref _disposed) || !await EnsureEncoderLoadedAsync()) return;

        string imageKey = BuildImagePreparationKey(original);
        if (IsPrepared(imageKey))
        {
            return;
        }

        await _prepareLock.WaitAsync();
        try
        {
            if (Volatile.Read(ref _disposed) || !await EnsureEncoderLoadedAsync()) return;

            if (IsPrepared(imageKey))
            {
                return;
            }

            // Taken after the load just above, which invalidates by itself when it switches the model. An invalidation
            // from here on (the pin changing, the model switched, the pin letting go of the service) means that what the
            // encoder is about to make of this image is no longer wanted.
            int generation;
            lock (_preparedImageLock)
            {
                if (_disposed) return;
                generation = _preparedImageGeneration;
            }

            Sam2ImageEncoding encoding = await EncodeImageAsync(original);
            lock (_preparedImageLock)
            {
                if (generation == _preparedImageGeneration)
                {
                    Volatile.Write(ref _preparedImage, new PreparedImage(imageKey, encoding));
                }
            }
        }
        finally
        {
            _prepareLock.Release();
        }
    }

    private bool IsPrepared(string imageKey) =>
        Volatile.Read(ref _preparedImage) is { } prepared && string.Equals(prepared.Key, imageKey, StringComparison.Ordinal);

    /// <summary>Loads SAM2 unless it is loaded, and says whether its encoder is there.</summary>
    /// <remarks>Virtual, with <see cref="EncodeImageAsync"/>, so that tests can stand in for the model.</remarks>
    internal virtual async Task<bool> EnsureEncoderLoadedAsync()
    {
        if (!_isInitialized) await InitializeAsync();
        return _encoderSession != null;
    }

    public void InvalidatePreparedImage()
    {
        lock (_preparedImageLock)
        {
            _preparedImageGeneration++;
            Volatile.Write(ref _preparedImage, null);
        }
    }

    /// <summary>
    /// Runs the encoder on <paramref name="original"/>, off the caller's thread. Leaves the service's own state alone:
    /// whether what it makes is kept is <see cref="EnsureImagePreparedAsync"/>'s call.
    /// </summary>
    /// <remarks>Virtual so that tests can hold a prepare open (Sam2ServicePrepareTests).</remarks>
    internal virtual Task<Sam2ImageEncoding> EncodeImageAsync(SKBitmap original)
    {
        if (_encoderSession == null)
            throw new InvalidOperationException("SAM2 encoder session is not initialized.");

        return Task.Run(() =>
        {
            // The session comes from a use scope, never from the cached field: the scope keeps it alive (and
            // serialised) for the whole run. Esc or a tool switch releases the last lease; with the cached reference
            // the runtime disposed the session while this was still inside Run — a native access violation, i.e. the
            // pin window and the whole app vanishing.
            using var sessionUse = _runtimeService.BeginSessionUse();
            var encoderSession = sessionUse.Encoder
                ?? throw new InvalidOperationException("SAM2 was unloaded before the image could be prepared.");

            int width = original.Width;
            int height = original.Height;

            ResolveEncoderInputMetadata(encoderSession);
            string inputName = _encoderInputName ?? "image";
            int bufferSize = 3 * Sam2InputSize * Sam2InputSize;
            // 12 MB of floats per prepared image: pooled instead of a fresh large-object-heap allocation every
            // time. Rent rounds up to a power of two (4,194,304 floats for this request), so the tensor below is
            // given an exact-length slice, never the raw array. No clear is needed: both fills write every one of
            // the bufferSize entries before the encoder reads any.
            float[] buffer = ArrayPool<float>.Shared.Rent(bufferSize);

            try
            {
                using var resized = original.Resize(new SKImageInfo(Sam2InputSize, Sam2InputSize), new SKSamplingOptions(SKFilterMode.Linear));
                if (resized == null)
                    throw new InvalidOperationException("Failed to resize image for SAM2 prepare.");

                FillEncoderInput(resized, buffer);

                // Keep the encoder input shape on the same proven 4D path as the
                // previously working implementation. Some SAM2 exports report extra
                // dimensions in metadata, but the runtime still expects the classic
                // NCHW image tensor here.
                var inputTensor = new DenseTensor<float>(buffer.AsMemory(0, bufferSize), new[] { 1, 3, Sam2InputSize, Sam2InputSize });

                var inputs = new List<NamedOnnxValue>(1) { NamedOnnxValue.CreateFromTensor(inputName, inputTensor) };
                using var results = encoderSession.Run(inputs);

                var outputNames = results.AsValueEnumerable().Select(r => r.Name).ToList();

                T? FindResult<T>(string[] aliases) where T : class
                {
                    var found = results.AsValueEnumerable().FirstOrDefault(r => aliases.AsValueEnumerable().Any(a => r.Name == a || r.Name.Contains(a)));
                    return found?.Value as T;
                }

                var imageEmbeddings = FindResult<Tensor<float>>(new[] { "image_embeddings", "image_embed", "embeddings" })?.ToDenseTensor()
                    ?? throw new Exception($"Encoder Error: Missing embeddings. Got: {string.Join(", ", outputNames)}");

                var highResFeat0 = FindResult<Tensor<float>>(new[] { "high_res_feats_0", "feat_0", "high_res_feat_0" })?.ToDenseTensor()
                    ?? throw new Exception($"Encoder Error: Missing feat_0. Got: {string.Join(", ", outputNames)}");

                var highResFeat1 = FindResult<Tensor<float>>(new[] { "high_res_feats_1", "feat_1", "high_res_feat_1" })?.ToDenseTensor()
                    ?? throw new Exception($"Encoder Error: Missing feat_1. Got: {string.Join(", ", outputNames)}");

                return new Sam2ImageEncoding(width, height, imageEmbeddings, highResFeat0, highResFeat1);
            }
            finally
            {
                // Safe here and not before: Run has returned, and the three outputs above were copied out with
                // ToDenseTensor, so nothing still reads the input buffer.
                ArrayPool<float>.Shared.Return(buffer);
            }
        });
    }

    /// <summary>
    /// Fills the planar encoder input from a <see cref="Sam2InputSize"/>-square bitmap: three planes of
    /// <c>Sam2InputSize * Sam2InputSize</c> floats, in B, G, R order, scaled to 0..1.
    /// </summary>
    /// <remarks>
    /// Writes every one of the first <c>3 * Sam2InputSize * Sam2InputSize</c> entries of <paramref name="buffer"/>
    /// and nothing past them — and the caller depends on the first half of that. The buffer comes from
    /// <see cref="ArrayPool{T}"/> uncleared, so an entry this skipped would feed the encoder whatever the pool last
    /// held, and the symptom (a wrong mask, only sometimes) would be about as hard to trace as a bug gets. Internal
    /// so <c>Sam2EncoderInputFillTests</c> can hold it to that. <paramref name="resized"/> must be exactly
    /// <see cref="Sam2InputSize"/> square: the fast path reads its rows through a raw pointer.
    /// </remarks>
    internal static void FillEncoderInput(SKBitmap resized, float[] buffer)
    {
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
    }

    public async Task<List<Avalonia.Rect>> AutoDetectObjectsAsync(int gridDensity = 32, int maxObjects = 20, int minSize = 20, CancellationToken cancellationToken = default)
    {
        // One prepared image for the whole pass, whatever an invalidation does meanwhile.
        var prepared = Volatile.Read(ref _preparedImage);
        if (!_isInitialized || prepared == null || _decoderSession == null) return new List<Avalonia.Rect>();
        var encoding = prepared.Encoding;
        int originalWidth = encoding.Width;
        int originalHeight = encoding.Height;

        return await Task.Run(() =>
        {
            var results = new List<Avalonia.Rect>();

            // Use scope: keeps the decoder alive for the whole detection pass (see EncodeImageAsync).
            using var sessionUse = _runtimeService.BeginSessionUse();
            var decoderSession = sessionUse.Decoder;
            if (decoderSession == null)
            {
                return results;
            }

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
            

            var decInputMetaData = decoderSession.InputMetadata;
            var decInputNames = decInputMetaData.Keys.AsValueEnumerable().ToList();

            // Process each point individually (batch=1)
            var lockObj = new object();

            // One point at a time: every iteration runs the SAME decoder session, and two threads inside Run on one
            // ONNX session is itself an access violation (see OcrRuntimeService). This used to run four-way parallel.
            Parallel.ForEach(points, new ParallelOptions { MaxDegreeOfParallelism = 1, CancellationToken = cancellationToken }, (pt, state) =>
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

                    var decInputMetaData = decoderSession.InputMetadata;
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

                    AddTensorInput(new[] { "image_embeddings", "image_embed", "embeddings", "image_embedding" }, encoding.ImageEmbeddings);
                    AddTensorInput(new[] { "high_res_feats_0", "feat_0", "high_res_feat_0" }, encoding.HighResFeat0);
                    AddTensorInput(new[] { "high_res_feats_1", "feat_1", "high_res_feat_1" }, encoding.HighResFeat1);
                    AddInput(new[] { "point_coords", "coords" }, System.Linq.Enumerable.ToArray(decCoords), decCoords.Dimensions.AsValueEnumerable().ToArray());
                    AddInput(new[] { "point_labels", "labels" }, System.Linq.Enumerable.ToArray(decLabels), decLabels.Dimensions.AsValueEnumerable().ToArray());
                    AddInput(new[] { "mask_input", "mask" }, System.Linq.Enumerable.ToArray(maskInput), maskInput.Dimensions.AsValueEnumerable().ToArray());
                    AddInput(new[] { "has_mask_input", "has_mask" }, System.Linq.Enumerable.ToArray(hasMaskInput), hasMaskInput.Dimensions.AsValueEnumerable().ToArray());
                    AddInput(new[] { "orig_im_size", "im_size" }, new float[] { 1024f, 1024f }, new[] { 2 });

                    using var result = decoderSession.Run(inputs);
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
                                    minX / mw * originalWidth,
                                    minY / mh * originalHeight,
                                    (maxX - minX) / mw * originalWidth,
                                    (maxY - minY) / mh * originalHeight);

                                // Filter noise
                                if (rect.Width >= minSize && rect.Height >= minSize &&
                                    rect.Width < originalWidth * 0.95 && rect.Height < originalHeight * 0.95)
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

    /// <summary>
    /// Runs the decoder for <paramref name="points"/> and returns the winning mask as a Gray8 bitmap at the
    /// original image size (white = selected), or null when the engine is not ready or produced nothing.
    /// </summary>
    /// <remarks>
    /// Hands the mask back as pixels rather than encoded PNG. The one consumer wants a Gray8 bitmap, so
    /// encoding the mask and decoding it straight back just to pull a single channel out again was pure
    /// overhead. The returned bitmap is the caller's to dispose.
    /// </remarks>
    public async Task<SKBitmap?> GetMaskBitmapAsync(IReadOnlyList<(double X, double Y, bool IsPositive)> points)
    {
        // One prepared image for the whole click: the pin changing meanwhile cannot hand the decoder one image's
        // embeddings with another image's size, or with none.
        var prepared = Volatile.Read(ref _preparedImage);
        if (!_isInitialized || prepared == null || _decoderSession == null)
            return null;

        var encoding = prepared.Encoding;
        // Copied before the caller's thread is left: the caller may change its list as soon as this returns.
        var pointList = points.AsValueEnumerable().ToList();

        return await Task.Run(() =>
        {
            int n = pointList.Count;
            if (n == 0) return (SKBitmap?)null;

            // Use scope: keeps the decoder alive and serialised for this click's run (see EncodeImageAsync).
            using var sessionUse = _runtimeService.BeginSessionUse();
            var decoderSession = sessionUse.Decoder;
            if (decoderSession == null) return (SKBitmap?)null;

            // SAM2 CRITICAL: Points must scale to the 1024-grid (since image is stretched)
            float scaleX = 1024f / (float)encoding.Width;
            float scaleY = 1024f / (float)encoding.Height;
            
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
                AppLog.Debug("SAM2Service", $"[AI MATH] Click({p.X:F0},{p.Y:F0}) -> AI_StretchGrid({aiX:F0},{aiY:F0}) Size=(1024x1024) BGR=1 Normal=0-1");
            }

            var maskInput = new DenseTensor<float>(new[] { 1, 1, 256, 256 });
            var hasMaskInput = new DenseTensor<float>(new[] { 1 });
            hasMaskInput[0] = 0f;

            var decInputMetaData = decoderSession.InputMetadata;
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

            AddTensorInput(new[] { "image_embeddings", "image_embed", "embeddings", "image_embedding" }, encoding.ImageEmbeddings);
            AddTensorInput(new[] { "high_res_feats_0", "feat_0", "high_res_feat_0" }, encoding.HighResFeat0);
            AddTensorInput(new[] { "high_res_feats_1", "feat_1", "high_res_feat_1" }, encoding.HighResFeat1);
            AddInput(new[] { "point_coords", "coords" }, System.Linq.Enumerable.ToArray(decCoords), decCoords.Dimensions.AsValueEnumerable().ToArray());
            AddInput(new[] { "point_labels", "labels" }, System.Linq.Enumerable.ToArray(decLabels), decLabels.Dimensions.AsValueEnumerable().ToArray());
            AddInput(new[] { "mask_input", "mask" }, System.Linq.Enumerable.ToArray(maskInput), maskInput.Dimensions.AsValueEnumerable().ToArray());
            AddInput(new[] { "has_mask_input", "has_mask" }, System.Linq.Enumerable.ToArray(hasMaskInput), hasMaskInput.Dimensions.AsValueEnumerable().ToArray());
            // SAM2 CRITICAL: orig_im_size must be [1024, 1024] when using stretching
            AddInput(new[] { "orig_im_size", "im_size" }, new float[] { 1024f, 1024f }, new[] { 2 });

            using var results = decoderSession.Run(inputs);
            var masksResult = results.AsValueEnumerable().FirstOrDefault(r => r.Name == "upsampled_masks") ?? results.AsValueEnumerable().FirstOrDefault(r => r.Name == "masks") ?? results.AsValueEnumerable().FirstOrDefault(r => r.Name == "mask_values") ?? results.AsValueEnumerable().FirstOrDefault(r => r.Name.Contains("mask") && !r.Name.Contains("low_res"));
            var iouResult = results.AsValueEnumerable().FirstOrDefault(r => r.Name == "iou_predictions" || r.Name == "iou_prediction" || r.Name.Contains("iou"));

            if (masksResult == null || iouResult == null) throw new Exception("Decoder Error: Missing masks/IOU.");

            // Extract result & RESIZE BACK TO ORIGINAL (Not 1024). Read before the results are disposed: the masks
            // are read straight from the output's buffer.
            using var maskFull = ReadBestMask(masksResult.AsTensor<float>(), iouResult.AsTensor<float>(), pointList, scaleX, scaleY, out var choice);
            string invTag = choice.Inverted ? "[INV!]" : "";
            _lastIouInfo = $"REV:{_buildRev} {invTag}{choice.PointStats} | D={choice.Density:F1}";

            // CRITICAL: Resize the mask back to the ORIGINAL dimensions so it aligns in the UI
            using var resizedMask = maskFull.Resize(new SKImageInfo(encoding.Width, encoding.Height), new SKSamplingOptions(SKFilterMode.Linear));
            if (resizedMask == null)
                return (SKBitmap?)null;

            // Resize targets the platform colour type, so fold the result down to the Gray8 the caller reads
            // a byte at a time.
            return NormalizeToGray8(resizedMask);
        });
    }

    /// <summary>What <see cref="ReadBestMask"/> picked, for the diagnostics line.</summary>
    internal readonly record struct MaskChoice(int Index, bool Inverted, float Density, string PointStats);

    /// <summary>
    /// Picks the decoder mask that best fits <paramref name="points"/> and returns it as a Gray8 bitmap at the mask's
    /// own size, white where selected. The bitmap is the caller's to dispose.
    /// </summary>
    /// <remarks>
    /// Reads the masks through the tensor's memory (<see cref="MaskPlanes"/>), never its indexer. Internal so
    /// <c>Sam2MaskReadoutTests</c> can hold it to the indexer's answer.
    /// </remarks>
    internal static SKBitmap ReadBestMask(
        Tensor<float> masksTensor,
        Tensor<float> iouTensor,
        IReadOnlyList<(double X, double Y, bool IsPositive)> points,
        float scaleX,
        float scaleY,
        out MaskChoice choice)
    {
        var masks = new MaskPlanes(masksTensor);
        int bestIdx = 0;
        float maxScore = -1000f;
        float bestDensity = 0;
        string bestPointStats = "";
        int numMasks = iouTensor.Dimensions[1];
        int mh = masks.Height, mw = masks.Width;
        float gxFactor = (float)mw / 1024f, gyFactor = (float)mh / 1024f;

        for(int i=0; i<numMasks; i++)
        {
            // REV22: 先計算此 mask 的值域來決定閾值
            float minVal = float.MaxValue, maxVal = float.MinValue;
            for(int sy=0; sy<mh; sy+=10) {
                for(int sx=0; sx<mw; sx+=10) {
                    float v = masks[i, sy, sx];
                    if (v < minVal) minVal = v;
                    if (v > maxVal) maxVal = v;
                }
            }
            float threshold = (maxVal > 10) ? 127.5f : (minVal < -1 ? 0f : 0.5f);

            float iou = iouTensor[0, i];
            int pointErrors = 0;
            string pointStats = $"R:{minVal:F0}~{maxVal:F0}T:{threshold:F1} ";
            for(int pIdx=0; pIdx < points.Count; pIdx++) {
                var p = points[pIdx];
                int gx = (int)Math.Clamp(p.X * scaleX * gxFactor, 0, mw - 1);
                int gy = (int)Math.Clamp(p.Y * scaleY * gyFactor, 0, mh - 1);

                float val = masks[i, gy, gx];
                float valSwap = masks[i, Math.Clamp(gx, 0, mh-1), Math.Clamp(gy, 0, mw-1)];

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

            float density = (float)masks.CountAbove(i, threshold) / Math.Max(1, mw * mh);
            float currentScore = iou;
            if (pointErrors > 0) currentScore -= (pointErrors * 50.0f);
            if (density > 0.45f) currentScore -= 10.0f;
            else if (density < 0.25f && density > 0.01f) currentScore += 2.0f;

            AppLog.Debug("SAM2Service", $"[AI BRAIN] Mask {i}: IOU={iou:F2} D={density:F2} Err={pointErrors} Score={currentScore:F2} {pointStats}");

            if (currentScore > maxScore) {
                maxScore = currentScore; bestIdx = i; bestDensity = density; bestPointStats = pointStats;
            }
        }

        // 根據最佳 mask 重新計算閾值
        float finalMinVal = float.MaxValue, finalMaxVal = float.MinValue;
        for(int sy=0; sy<mh; sy+=5) {
            for(int sx=0; sx<mw; sx+=5) {
                float v = masks[bestIdx, sy, sx];
                if (v < finalMinVal) finalMinVal = v;
                if (v > finalMaxVal) finalMaxVal = v;
            }
        }
        float finalThreshold = (finalMaxVal > 10) ? 127.5f : (finalMinVal < -1 ? 0f : 0.5f);

        bool finalTouchesLT = masks[bestIdx, 1, 1] > finalThreshold;
        bool finalTouchesRT = masks[bestIdx, 1, mw - 2] > finalThreshold;
        bool finalTouchesLB = masks[bestIdx, mh - 2, 1] > finalThreshold;
        bool finalTouchesRB = masks[bestIdx, mh - 2, mw - 2] > finalThreshold;
        bool isAllSelect = finalTouchesLT && finalTouchesRT && finalTouchesLB && finalTouchesRB;

        // 如果四角都選中且密度很高，嘗試反轉 mask
        bool invertMask = isAllSelect && bestDensity > 0.7f;

        AppLog.Debug("SAM2Service", $"[AI FINAL] Mask {bestIdx}: Min={finalMinVal:F1} Max={finalMaxVal:F1} Thresh={finalThreshold} Inv={invertMask}");

        var maskFull = new SKBitmap(mw, mh, SKColorType.Gray8, SKAlphaType.Opaque);
        // Gray8 is one byte per pixel, so write the rows straight through: SetPixel would run a colour
        // conversion for every pixel of a mask that is routinely 1024x1024.
        ReadOnlySpan<float> values = masks.Values;
        int columnStride = masks.ColumnStride;
        unsafe
        {
            byte* maskBase = (byte*)maskFull.GetPixels().ToPointer();
            int maskRowBytes = maskFull.RowBytes;
            for (int y = 0; y < mh; y++)
            {
                byte* maskRow = maskBase + (y * maskRowBytes);
                int at = masks.RowStart(bestIdx, y);
                for (int x = 0; x < mw; x++, at += columnStride)
                {
                    bool isSelected = values[at] > finalThreshold;
                    if (invertMask) isSelected = !isSelected;
                    maskRow[x] = isSelected ? (byte)255 : (byte)0;
                }
            }
        }

        choice = new MaskChoice(bestIdx, invertMask, bestDensity, bestPointStats);
        return maskFull;
    }

    /// <summary>
    /// The masks of a decoder output's first batch item, read through the tensor's memory at offsets worked out from
    /// its strides.
    /// </summary>
    /// <remarks>
    /// The tensor's own indexer takes its indices as a <c>params int[]</c>, so every read allocated an array, then went
    /// through virtual calls and ONNX Runtime's memory manager for the native buffer to find one float — and a click
    /// reads a 1024x1024 output about four million times.
    /// </remarks>
    private readonly ref struct MaskPlanes
    {
        private readonly int _maskStride;
        private readonly int _rowStride;

        public MaskPlanes(Tensor<float> tensor)
        {
            // ONNX Runtime returns its outputs as dense tensors over the native buffer; anything else is copied into one.
            var dense = tensor as DenseTensor<float> ?? tensor.ToDenseTensor();
            ReadOnlySpan<int> strides = dense.Strides;
            ReadOnlySpan<int> dimensions = dense.Dimensions;
            Values = dense.Buffer.Span;
            _maskStride = strides[1];
            _rowStride = strides[2];
            ColumnStride = strides[3];
            Height = dimensions[2];
            Width = dimensions[3];
        }

        public ReadOnlySpan<float> Values { get; }
        public int Height { get; }
        public int Width { get; }

        /// <summary>How far apart a row's neighbouring values lie in <see cref="Values"/>: 1 for a row-major tensor.</summary>
        public int ColumnStride { get; }

        public float this[int mask, int y, int x] => Values[RowStart(mask, y) + (x * ColumnStride)];

        /// <summary>Where row <paramref name="y"/> of mask <paramref name="mask"/> starts in <see cref="Values"/>.</summary>
        public int RowStart(int mask, int y) => (mask * _maskStride) + (y * _rowStride);

        /// <summary>How many of the values of mask <paramref name="mask"/> lie above <paramref name="threshold"/>.</summary>
        public int CountAbove(int mask, float threshold)
        {
            ReadOnlySpan<float> values = Values;
            int height = Height, width = Width, columnStride = ColumnStride;
            int count = 0;
            for (int y = 0; y < height; y++)
            {
                int at = RowStart(mask, y);
                for (int x = 0; x < width; x++, at += columnStride)
                {
                    if (values[at] > threshold) count++;
                }
            }

            return count;
        }
    }

    /// <summary>
    /// Copies <paramref name="source"/> into a Gray8 bitmap, reading the grey level from whichever channel its
    /// colour type keeps it in. The returned bitmap is the caller's to dispose.
    /// </summary>
    /// <remarks>
    /// Sizes, row strides and the colour type are read once: each is a call into Skia, and reading the width at every
    /// pixel cost 0.18 s of each click on a 4K pin. Internal so <c>Sam2MaskNormalizeTests</c> can check the channels.
    /// </remarks>
    internal static SKBitmap NormalizeToGray8(SKBitmap source)
    {
        var colorType = source.ColorType;
        if (colorType == SKColorType.Gray8)
            return source.Copy();

        int width = source.Width;
        int height = source.Height;
        var normalized = new SKBitmap(width, height, SKColorType.Gray8, SKAlphaType.Opaque);
        unsafe
        {
            byte* dstBase = (byte*)normalized.GetPixels().ToPointer();
            int dstRowBytes = normalized.RowBytes;

            if (colorType is SKColorType.Bgra8888 or SKColorType.Rgba8888)
            {
                // Byte 0 of a BGRA pixel, byte 2 of an RGBA one: blue either way.
                int channel = colorType == SKColorType.Bgra8888 ? 0 : 2;
                byte* srcBase = (byte*)source.GetPixels().ToPointer();
                int srcRowBytes = source.RowBytes;
                for (int y = 0; y < height; y++)
                {
                    byte* srcRow = srcBase + (y * srcRowBytes) + channel;
                    byte* dstRow = dstBase + (y * dstRowBytes);
                    for (int x = 0; x < width; x++)
                    {
                        dstRow[x] = srcRow[x * 4];
                    }
                }
            }
            else
            {
                for (int y = 0; y < height; y++)
                {
                    byte* dstRow = dstBase + (y * dstRowBytes);
                    for (int x = 0; x < width; x++)
                    {
                        var color = source.GetPixel(x, y);
                        dstRow[x] = color.Red;
                    }
                }
            }
        }

        return normalized;
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

        // Even reading metadata touches the native session, so it needs the same use scope as a Run.
        using var sessionUse = _runtimeService.BeginSessionUse();
        var encoder = sessionUse.Encoder;
        var decoder = sessionUse.Decoder;
        if (encoder == null || decoder == null) return "Not Loaded";

        // CRITICAL: Build Timestamp to verify DLL update
        var info = $"[BUILD: {DateTime.Now:HH:mm:ss}]\n";
        info += "Encoder Inputs:\n";
        foreach (var input in encoder.InputMetadata)
            info += $"  - {input.Key}: {string.Join("x", input.Value.Dimensions.AsValueEnumerable().ToArray())}\n";
        info += "Encoder Outputs:\n";
        foreach (var output in encoder.OutputMetadata)
            info += $"  - {output.Key}: {string.Join("x", output.Value.Dimensions.AsValueEnumerable().ToArray())}\n";
        info += "Decoder Inputs:\n";
        foreach (var input in decoder.InputMetadata)
            info += $"  - {input.Key}: {string.Join("x", input.Value.Dimensions.AsValueEnumerable().ToArray())}\n";
        info += "Decoder Outputs:\n";
        foreach (var output in decoder.OutputMetadata)
            info += $"  - {output.Key}: {string.Join("x", output.Value.Dimensions.AsValueEnumerable().ToArray())}\n";
        return info;
    }

    public void Dispose()
    {
        // 清除所有張量參考，允許 GC 回收大型記憶體區塊
        // The pin has let go of the service: a prepare still running publishes nothing, since it began under the
        // generation this invalidation moves on from, and one still to come prepares nothing.
        lock (_preparedImageLock)
        {
            _disposed = true;
        }

        InvalidatePreparedImage();

        // 清除 session 參考（不 Dispose，session 由 AIResourceService 管理）
        _encoderSession = null;
        _decoderSession = null;
        _isInitialized = false;
    }
}
