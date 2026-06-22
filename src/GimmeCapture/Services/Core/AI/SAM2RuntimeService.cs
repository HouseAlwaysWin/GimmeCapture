using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Infrastructure;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace GimmeCapture.Services.Core.AI;

public sealed class SAM2RuntimeService : IDisposable
{
    private readonly AIPathService _pathService;
    private readonly NativeResolverService _resolverService;
    private InferenceSession? _cachedEncoder;
    private InferenceSession? _cachedDecoder;
    private SAM2Variant? _cachedVariant;
    private bool _isWarmedUp;
    private readonly SemaphoreSlim _modelLoadingLock = new(1, 1);
    private readonly object _leaseLock = new();
    private readonly HashSet<string> _activeLeases = new();

    public SAM2RuntimeService(AIPathService pathService, NativeResolverService resolverService)
    {
        _pathService = pathService ?? throw new ArgumentNullException(nameof(pathService));
        _resolverService = resolverService ?? throw new ArgumentNullException(nameof(resolverService));
    }

    public bool IsLoaded => _cachedVariant.HasValue && _cachedEncoder != null && _cachedDecoder != null;
    public bool IsLoadedAndWarmed => IsLoaded && _isWarmedUp;
    public SAM2Variant? LoadedVariant => _cachedVariant;
    public bool HasActiveLeases
    {
        get
        {
            lock (_leaseLock)
            {
                return _activeLeases.Count > 0;
            }
        }
    }

    public string AcquireLease()
    {
        ProcessMemoryTrimService.NotifyActivity("sam2");
        var leaseId = Guid.NewGuid().ToString("N");
        lock (_leaseLock)
        {
            _activeLeases.Add(leaseId);
        }

        return leaseId;
    }

    public void ReleaseLease(string? leaseId, bool unloadWhenIdle = true)
    {
        if (string.IsNullOrWhiteSpace(leaseId))
            return;

        bool shouldUnload = false;
        lock (_leaseLock)
        {
            _activeLeases.Remove(leaseId);
            shouldUnload = unloadWhenIdle && _activeLeases.Count == 0;
        }

        if (shouldUnload)
        {
            UnloadModels();
        }
    }

    public async Task LoadModelsAsync(SAM2Variant variant)
    {
        ProcessMemoryTrimService.NotifyActivity("sam2");
        if (_cachedVariant == variant && _cachedEncoder != null && _cachedDecoder != null)
        {
            return;
        }

        await _modelLoadingLock.WaitAsync();
        try
        {
            if (_cachedVariant == variant && _cachedEncoder != null && _cachedDecoder != null)
            {
                return;
            }

            UnloadModels();
            _resolverService.SetupNativeResolvers();

            var paths = _pathService.GetSAM2Paths(variant);
            if (!File.Exists(paths.Encoder) || !File.Exists(paths.Decoder))
            {
                System.Diagnostics.Debug.WriteLine("[AI] Check Model files missing, cannot load.");
                return;
            }

            await Task.Run(() =>
            {
                try
                {
                    var options = new SessionOptions
                    {
                        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_BASIC,
                        LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
                    };

                    OnnxProviderConfigurator.AppendGpuProvidersWithFallback(options);

                    System.Diagnostics.Debug.WriteLine($"[AI] Loading Encoder: {paths.Encoder}");
                    _cachedEncoder = new InferenceSession(paths.Encoder, options);

                    System.Diagnostics.Debug.WriteLine($"[AI] Loading Decoder: {paths.Decoder}");
                    _cachedDecoder = new InferenceSession(paths.Decoder, options);

                    _cachedVariant = variant;
                    _isWarmedUp = false;
                    System.Diagnostics.Debug.WriteLine("[AI] Models Loaded Successfully");

                    WarmupSessions();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[AI] Model Load Error: {ex.Message}");
                    UnloadModels();
                    throw;
                }
            });
        }
        finally
        {
            _modelLoadingLock.Release();
        }
    }

    public async Task EnsureLoadedAndWarmedAsync(SAM2Variant variant)
    {
        await LoadModelsAsync(variant);
        if (_cachedVariant != variant || _cachedEncoder == null || _cachedDecoder == null || _isWarmedUp)
        {
            return;
        }

        await _modelLoadingLock.WaitAsync();
        try
        {
            if (_cachedVariant != variant || _cachedEncoder == null || _cachedDecoder == null || _isWarmedUp)
            {
                return;
            }

            await Task.Run(WarmupSessions);
        }
        finally
        {
            _modelLoadingLock.Release();
        }
    }

    public (InferenceSession? Encoder, InferenceSession? Decoder) GetSessions()
    {
        return (_cachedEncoder, _cachedDecoder);
    }

    public void UnloadModels()
    {
        lock (_leaseLock)
        {
            if (_activeLeases.Count > 0)
            {
                return;
            }
        }

        bool releasedResources = _cachedEncoder != null || _cachedDecoder != null;

        _cachedEncoder?.Dispose();
        _cachedEncoder = null;

        _cachedDecoder?.Dispose();
        _cachedDecoder = null;

        _cachedVariant = null;
        _isWarmedUp = false;
        if (releasedResources)
        {
            ProcessMemoryTrimService.RequestIdleTrimAsync("sam2-unloaded")
                .Forget("MemoryTrim.Sam2Unloaded");
        }
    }

    public void Dispose()
    {
        UnloadModels();
    }

    private void WarmupSessions()
    {
        if (_isWarmedUp || _cachedEncoder == null || _cachedDecoder == null)
        {
            return;
        }

        System.Diagnostics.Debug.WriteLine("[AI] Warming up SAM2 sessions centralized...");
        try
        {
            var encoderInput = new DenseTensor<float>(new[] { 1, 3, 1024, 1024 });
            var encInputMetaData = _cachedEncoder.InputMetadata;
            var encInputName = encInputMetaData.Keys.AsValueEnumerable().FirstOrDefault(k => k == "image" || k == "pixel_values") ?? "image";
            var encInputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(encInputName, encoderInput) };
            using var encResults = _cachedEncoder.Run(encInputs);

            var decInputMetaData = _cachedDecoder.InputMetadata;
            var decInputNames = decInputMetaData.Keys.AsValueEnumerable().ToList();
            var decInputs = new List<NamedOnnxValue>();

            void AddMock(string[] aliases, int[] dims, float val = 0f)
            {
                var name = decInputNames.AsValueEnumerable().FirstOrDefault(n => aliases.AsValueEnumerable().Any(a => n == a || n == a.Replace("_", "") || n.Contains(a)));
                if (name == null) return;

                var meta = decInputMetaData[name];
                if (meta.ElementType == typeof(int))
                {
                    var data = new int[dims.AsValueEnumerable().Aggregate(1, (a, b) => a * b)];
                    if (val != 0) Array.Fill(data, (int)val);
                    decInputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<int>(data, dims)));
                }
                else if (meta.ElementType == typeof(long))
                {
                    var data = new long[dims.AsValueEnumerable().Aggregate(1, (a, b) => a * b)];
                    if (val != 0) Array.Fill(data, (long)val);
                    decInputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(data, dims)));
                }
                else
                {
                    var data = new float[dims.AsValueEnumerable().Aggregate(1, (a, b) => a * b)];
                    if (val != 0) Array.Fill(data, val);
                    decInputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<float>(data, dims)));
                }
            }

            AddMock(new[] { "image_embeddings", "image_embed", "embeddings", "image_embedding" }, new[] { 1, 256, 64, 64 });
            AddMock(new[] { "high_res_feats_0", "feat_0", "high_res_feat_0" }, new[] { 1, 32, 256, 256 });
            AddMock(new[] { "high_res_feats_1", "feat_1", "high_res_feat_1" }, new[] { 1, 64, 128, 128 });
            AddMock(new[] { "point_coords", "coords" }, new[] { 1, 1, 2 });
            AddMock(new[] { "point_labels", "labels" }, new[] { 1, 1 }, 1f);
            AddMock(new[] { "mask_input", "mask" }, new[] { 1, 1, 256, 256 });
            AddMock(new[] { "has_mask_input", "has_mask" }, new[] { 1 }, 0f);
            AddMock(new[] { "orig_im_size", "im_size" }, new[] { 2 }, 1024f);

            using var decResults = _cachedDecoder.Run(decInputs);

            _isWarmedUp = true;
            System.Diagnostics.Debug.WriteLine("[AI] Centralized warmup complete.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AI] Session Warmup Warning (Non-fatal): {ex.Message}");
        }
    }
}
