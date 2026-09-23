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

/// <summary>
/// Owns the one SAM2 encoder/decoder session pair every pin shares.
///
/// Guarded exactly like <see cref="OcrRuntimeService"/>, and for the same reason: freeing an ONNX session while
/// another thread is inside <c>Run</c> on it faults the process with an access violation (0xC0000005) — not a
/// catchable .NET exception, so the app just vanishes. SAM2 used to hand out raw session references: the encoder
/// ran fire-and-forget on the thread pool while Esc or a tool switch released the last lease, and releasing the
/// last lease disposed the sessions on the spot. Now every native call happens inside a
/// <see cref="BeginSessionUse"/> scope, teardown waits for the scopes to drain, and inference is serialised
/// (two threads in <c>Run</c> on one session crash just the same).
/// </summary>
public sealed class SAM2RuntimeService : IDisposable
{
    /// <summary>How long an unload or variant swap waits for in-flight inference before giving up on it.</summary>
    private static readonly TimeSpan SwapTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Shorter at shutdown: past this, leaking the sessions beats stalling process exit.</summary>
    private static readonly TimeSpan ShutdownUnloadTimeout = TimeSpan.FromSeconds(5);

    private readonly AIPathService _pathService;
    private readonly NativeResolverService _resolverService;
    private InferenceSession? _cachedEncoder;
    private InferenceSession? _cachedDecoder;
    private SAM2Variant? _cachedVariant;
    private bool _isWarmedUp;
    private readonly SemaphoreSlim _modelLoadingLock = new(1, 1);
    private readonly object _leaseLock = new();
    private readonly HashSet<string> _activeLeases = new();
    private readonly ResourceUseGate _useGate = new();
    private readonly SemaphoreSlim _inferenceLock = new(1, 1);

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

    /// <summary>In-flight session uses. Diagnostics and tests only.</summary>
    internal int ActiveSessionUses => _useGate.ActiveUses;

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
            // Off the caller's thread: the last lease is typically released from the UI (Esc, tool switch, pin
            // closing), and the unload now waits for an inference still in flight — seconds for an encoder run on CPU.
            Task.Run(() => UnloadModels()).Forget("Sam2Runtime.IdleUnload");
        }
    }

    /// <summary>
    /// Takes the sessions for one inference and keeps them alive for its duration. ALWAYS use the scope's sessions
    /// rather than caching them: outside a scope they may already be disposed, and calling <c>Run</c> — or even
    /// reading metadata — on a disposed session is an access violation, not an exception.
    ///
    /// Inference is SERIALISED: one scope at a time. The sessions may be null (never loaded, or unloaded while the
    /// caller waited) — callers treat that as "SAM2 is not available right now".
    /// Do not nest scopes on one thread: the inference lock is not re-entrant.
    /// </summary>
    public Sam2SessionUse BeginSessionUse()
    {
        var scope = _useGate.BeginUse();
        try
        {
            _inferenceLock.Wait();
        }
        catch
        {
            scope.Dispose();
            throw;
        }

        // Read once both gates are held: a swap or unload only runs when no use is open, so these cannot change
        // under the caller.
        return new Sam2SessionUse(
            () =>
            {
                _inferenceLock.Release();
                scope.Dispose();
            },
            _cachedEncoder,
            _cachedDecoder);
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

            _resolverService.SetupNativeResolvers();

            var paths = _pathService.GetSAM2Paths(variant);
            if (!File.Exists(paths.Encoder) || !File.Exists(paths.Decoder))
            {
                AppLog.Warning("Sam2Runtime.Load", $"SAM2 {variant} model files are missing; cannot load.");
                return;
            }

            await Task.Run(() =>
            {
                // The old pair is replaced under exclusive access. The previous code called the lease-gated
                // UnloadModels here, which returned early whenever a pin held a lease — and then simply overwrote
                // the cached sessions, orphaning the old pair (hundreds of MB to ~1 GB) until the process exited.
                if (!_useGate.TryBeginExclusive(SwapTimeout, out var swap))
                {
                    AppLog.Warning(
                        "Sam2Runtime.SwapTimedOut",
                        $"SAM2 still busy after {SwapTimeout.TotalSeconds:0}s; staying on {_cachedVariant?.ToString() ?? "none"} instead of loading {variant}.");
                    return;
                }

                using (swap)
                {
                    bool releasedOldPair = DisposeSessions();
                    try
                    {
                        var options = new SessionOptions
                        {
                            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_BASIC,
                            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
                        };

                        OnnxProviderConfigurator.AppendGpuProvidersWithFallback(options);

                        _cachedEncoder = new InferenceSession(paths.Encoder, options);
                        _cachedDecoder = new InferenceSession(paths.Decoder, options);
                        _cachedVariant = variant;
                        _isWarmedUp = false;
                        AppLog.Information($"Sam2Runtime.Loaded: {variant}{(releasedOldPair ? " (replaced the previous variant)" : string.Empty)}");
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warning("Sam2Runtime.Load", ex);
                        DisposeSessions();
                        throw;
                    }
                }

                WarmupSessions();
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

    /// <summary>
    /// The current sessions, for "is SAM2 loaded" checks only. Never call <c>Run</c> or read metadata on these:
    /// outside a <see cref="BeginSessionUse"/> scope they can be disposed at any moment.
    /// </summary>
    public (InferenceSession? Encoder, InferenceSession? Decoder) GetSessions()
    {
        return (_cachedEncoder, _cachedDecoder);
    }

    public void UnloadModels() => UnloadModels(SwapTimeout);

    /// <summary>
    /// Frees the sessions once no pin holds a lease and nothing is running on them. If inference is still in flight
    /// after <paramref name="timeout"/> the sessions are deliberately LEAKED — the process reclaims them at exit,
    /// whereas disposing them under a running inference faults it.
    /// </summary>
    private void UnloadModels(TimeSpan timeout)
    {
        if (HasActiveLeases)
        {
            return;
        }

        if (!_useGate.TryBeginExclusive(timeout, out var exclusive))
        {
            AppLog.Warning(
                "Sam2Runtime.UnloadTimedOut",
                "SAM2 sessions still in use; skipping unload rather than disposing them mid-inference.");
            return;
        }

        bool released;
        using (exclusive)
        {
            // A lease taken while this waited for the in-flight run means the sessions are wanted again.
            if (HasActiveLeases)
            {
                return;
            }

            released = DisposeSessions();
        }

        if (released)
        {
            ProcessMemoryTrimService.RequestIdleTrimAsync("sam2-unloaded")
                .Forget("MemoryTrim.Sam2Unloaded");
        }
    }

    public void Dispose()
    {
        UnloadModels(ShutdownUnloadTimeout);
    }

    /// <summary>Caller MUST hold exclusive access via <see cref="_useGate"/>. Returns whether anything was freed.</summary>
    private bool DisposeSessions()
    {
        bool releasedResources = _cachedEncoder != null || _cachedDecoder != null;

        _cachedEncoder?.Dispose();
        _cachedEncoder = null;

        _cachedDecoder?.Dispose();
        _cachedDecoder = null;

        _cachedVariant = null;
        _isWarmedUp = false;
        return releasedResources;
    }

    /// <summary>Runs one throwaway inference so the first real click is not the one paying for kernel setup.</summary>
    private void WarmupSessions()
    {
        if (_isWarmedUp)
        {
            return;
        }

        using var sessionUse = BeginSessionUse();
        var encoder = sessionUse.Encoder;
        var decoder = sessionUse.Decoder;
        if (encoder == null || decoder == null)
        {
            return;
        }

        try
        {
            var encoderInput = new DenseTensor<float>(new[] { 1, 3, 1024, 1024 });
            var encInputMetaData = encoder.InputMetadata;
            var encInputName = encInputMetaData.Keys.AsValueEnumerable().FirstOrDefault(k => k == "image" || k == "pixel_values") ?? "image";
            var encInputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(encInputName, encoderInput) };
            using var encResults = encoder.Run(encInputs);

            var decInputMetaData = decoder.InputMetadata;
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

            using var decResults = decoder.Run(decInputs);

            _isWarmedUp = true;
        }
        catch (Exception ex)
        {
            // Non-fatal: the first real inference simply pays the setup cost instead.
            AppLog.Warning("Sam2Runtime.Warmup", ex);
        }
    }
}

/// <summary>
/// One inference's hold on the SAM2 sessions. Dispose it (use <c>using</c>) as soon as the native calls are done:
/// while it is open, the sessions cannot be unloaded or swapped — and no other inference can start.
/// </summary>
public sealed class Sam2SessionUse : IDisposable
{
    private Action? _release;

    internal Sam2SessionUse(Action release, InferenceSession? encoder, InferenceSession? decoder)
    {
        _release = release;
        Encoder = encoder;
        Decoder = decoder;
    }

    public InferenceSession? Encoder { get; }
    public InferenceSession? Decoder { get; }

    public void Dispose()
    {
        Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
