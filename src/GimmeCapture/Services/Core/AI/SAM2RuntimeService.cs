using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Infrastructure;
using Microsoft.ML.OnnxRuntime;

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
    private InferenceSession? _cachedEncoder;
    private InferenceSession? _cachedDecoder;
    private SAM2Variant? _cachedVariant;
    private readonly SemaphoreSlim _modelLoadingLock = new(1, 1);
    private readonly object _leaseLock = new();
    private readonly HashSet<string> _activeLeases = new();
    private readonly ResourceUseGate _useGate = new();
    private readonly SemaphoreSlim _inferenceLock = new(1, 1);
    private readonly object _queuedUnloadsLock = new();
    // The unloads UnloadModelsAsync started, chained so this one finishes after every one before it.
    private Task _queuedUnloads = Task.CompletedTask;

    public SAM2RuntimeService(AIPathService pathService)
    {
        _pathService = pathService ?? throw new ArgumentNullException(nameof(pathService));
    }

    public bool IsLoaded => _cachedVariant.HasValue && _cachedEncoder != null && _cachedDecoder != null;
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
            UnloadModelsAsync().Forget("Sam2Runtime.IdleUnload");
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

        // An unload asked for before this load finishes first. Otherwise turning AI off and on again, or leaving smart
        // selection and going straight back in, could find the sessions that unload was about to free, take them,
        // and be left with none a moment later.
        Task queuedUnloads;
        lock (_queuedUnloadsLock)
        {
            queuedUnloads = _queuedUnloads;
        }

        await queuedUnloads;

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
                        var stopwatch = Stopwatch.StartNew();
                        (_cachedEncoder, _cachedDecoder) = CreateSessions(paths.Encoder, paths.Decoder);
                        _cachedVariant = variant;
                        AppLog.Information(
                            "Sam2Runtime.Loaded",
                            $"{variant} on {OnnxProviderConfigurator.CpuProvider} in {stopwatch.ElapsedMilliseconds} ms{(releasedOldPair ? " (replaced the previous variant)" : string.Empty)}");
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warning("Sam2Runtime.Load", ex);
                        DisposeSessions();
                        throw;
                    }
                }
            });
        }
        finally
        {
            _modelLoadingLock.Release();
        }
    }

    /// <summary>
    /// Builds the encoder and decoder on the CPU, with ONNX Runtime's memory arena off and every graph optimization on.
    /// </summary>
    /// <remarks>
    /// They ran on DirectML, which took the process up by 2.9 GB once the encoder had run and kept 370-380 MB of it
    /// for good after the unload (SAM2 Tiny, an RTX 4070 SUPER). On the CPU with the arena, which kept what each run
    /// allocated, 2.4 GB; without it about 215 MB, and under 40 once unloaded. The encoder takes 1.4 s instead of 0.17
    /// (1.7 s on its first run), the decoder 40-45 ms a click instead of 15-50 (Ryzen 7 5700X), and the load 2.7 s
    /// instead of 3.3-4.2. The full graph optimizations, which <c>ORT_ENABLE_BASIC</c> left out, take 0.2 s off the
    /// encoder's 1.6. There is no warm-up run any more: on the CPU it took 1.7 s and saved the first real run 0.3.
    /// <c>SAM2RuntimeServiceTests</c> has the measurement.
    /// </remarks>
    private static (InferenceSession Encoder, InferenceSession Decoder) CreateSessions(string encoderPath, string decoderPath)
    {
        using var options = new SessionOptions
        {
            EnableCpuMemArena = false,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
        };
        return OnnxProviderConfigurator.CreateSessionPair(options, encoderPath, decoderPath);
    }

    /// <summary>
    /// The current sessions, for "is SAM2 loaded" checks only. Never call <c>Run</c> or read metadata on these:
    /// outside a <see cref="BeginSessionUse"/> scope they can be disposed at any moment.
    /// </summary>
    public (InferenceSession? Encoder, InferenceSession? Decoder) GetSessions()
    {
        return (_cachedEncoder, _cachedDecoder);
    }

    /// <summary>
    /// Frees the sessions as <see cref="UnloadModels()"/> does, for callers that must not wait. Returns at once; the
    /// unload runs on the thread pool, after any unload started before it, and a load asked for meanwhile waits for it.
    /// </summary>
    /// <remarks>
    /// The unload waits for an inference still running, up to <see cref="SwapTimeout"/>. Turning AI off ran it on the
    /// UI thread, which froze the settings window for as long. The returned task never faults: a failed unload is logged.
    /// </remarks>
    public Task UnloadModelsAsync()
    {
        lock (_queuedUnloadsLock)
        {
            _queuedUnloads = UnloadAfterAsync(_queuedUnloads);
            return _queuedUnloads;
        }

        async Task UnloadAfterAsync(Task previous)
        {
            // Forced to yield even when nothing is queued, so the caller's thread is left before the unload waits.
            await previous.ConfigureAwait(ConfigureAwaitOptions.ForceYielding | ConfigureAwaitOptions.SuppressThrowing);
            try
            {
                UnloadModels(SwapTimeout);
            }
            catch (Exception ex)
            {
                AppLog.Warning("Sam2Runtime.Unload", ex);
            }
        }
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
        return releasedResources;
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
