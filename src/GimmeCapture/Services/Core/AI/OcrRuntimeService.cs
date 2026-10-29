using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.OCR;
using Microsoft.ML.OnnxRuntime;

namespace GimmeCapture.Services.Core.AI;

public sealed class OcrRuntimeService : IDisposable
{
    /// <summary>
    /// How long a model swap waits for in-flight inference before giving up. Generous: a detection pass on a
    /// full-screen capture takes a few hundred ms, and skipping a swap is a far better outcome than either
    /// blocking forever or freeing a session out from under a running inference.
    /// </summary>
    private static readonly TimeSpan SwapTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Shorter budget at shutdown: past this, leaking the sessions beats stalling process exit.</summary>
    private static readonly TimeSpan ShutdownUnloadTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long the sessions survive after the last consumer lets go. Deliberately NOT immediate: tearing a
    /// DirectML session down and building a new one seconds later is what precedes every observed crash, and
    /// back-to-back scans used to do exactly that.
    ///
    /// Five seconds turned out to be far shorter than the rhythm captures actually arrive in: a real session logs
    /// a session build at ~370 ms plus the model files being read again, and that was paid on every capture more
    /// than five seconds after the last one — which is most of them. Two minutes covers a working rhythm and
    /// still frees the sessions once the user has moved on; the memory trim then runs on its own, longer, idle
    /// schedule.
    /// </summary>
    private static readonly TimeSpan UnloadIdleDelay = TimeSpan.FromMinutes(2);

    private readonly AIResourceService _aiResourceService;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly object _leaseLock = new();
    private readonly HashSet<string> _activeLeases = new();
    // Guards the sessions against being disposed mid-inference. Coarser than _loadLock, which only serialises
    // loads against each other and never knew anything about inference already running on the old sessions.
    private readonly ResourceUseGate _useGate = new();
    // Serialises inference. The use gate deliberately allows concurrent users (it only separates use from
    // teardown), which is not enough: concurrent Run calls on one session are themselves fatal.
    private readonly SemaphoreSlim _inferenceLock = new(1, 1);
    private readonly IdleReleaseScheduler _idleUnload;
    // Keeps the DirectML device open across idle unloads, for the app's idle window after the last use. The sessions
    // are the last thing holding it, and reopening it took 170-230 ms of a cold load and 150 ms of its first detection
    // (a cold reload measured 340-430 ms plus 260-300 ms, against 170-200 ms plus 110-140 ms with the device held).
    private readonly GpuDeviceHold _gpuDevice = new(ProcessMemoryTrimService.IdleTrimDelay);
    // The detection model is the same file for every language; only the recognizer and its dictionary differ.
    private InferenceSession? _detSession;
    private InferenceSession? _recSession;
    private OCRLanguage? _loadedLanguage;
    private List<string> _dictionary = new();
    // Whether the sessions fell back to the CPU, so a recognizer built later joins the detector there.
    private bool _onCpu;

    public OcrRuntimeService(AIResourceService aiResourceService)
    {
        _aiResourceService = aiResourceService ?? throw new ArgumentNullException(nameof(aiResourceService));
        _idleUnload = new IdleReleaseScheduler(UnloadIdleDelay, Unload);
        AIResourceService.RequestGlobalUnload += HandleGlobalUnload;
    }

    /// <summary>Whether detection and recognition are both ready; <see cref="LoadedLanguage"/> names the recognizer.</summary>
    public bool IsLoaded => _loadedLanguage.HasValue && _detSession != null && _recSession != null;

    /// <summary>Whether detection is ready, which is all the overlay's scan needs.</summary>
    public bool IsDetectionLoaded => _detSession != null;

    public OCRLanguage? LoadedLanguage => _loadedLanguage;
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

    /// <summary>
    /// Takes the sessions for one inference and keeps them alive for its duration. ALWAYS use the returned scope's
    /// sessions rather than caching them: outside the scope they may already be disposed, and calling
    /// <c>Run</c> on a disposed session faults the process with an access violation rather than throwing.
    ///
    /// Inference is SERIALISED: only one caller runs at a time. Two threads calling <c>Run</c> on the same session
    /// crashed the process with an access violation, and the app produces overlapping callers routinely —
    /// <c>DetectText</c> cannot be cancelled mid-run, so cancelling a scan (Esc) leaves its inference finishing on
    /// the thread pool while the replacement scan starts.
    /// </summary>
    public OcrSessionUse BeginSessionUse()
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

        // Read once both gates are held: a swap can only run when no use is open, so these cannot change under us.
        return new OcrSessionUse(
            () =>
            {
                _inferenceLock.Release();
                scope.Dispose();
            },
            _detSession,
            _recSession,
            _dictionary);
    }

    public string AcquireLease()
    {
        ProcessMemoryTrimService.NotifyActivity("ocr");
        // In use again — drop any pending unload so a follow-up capture reuses the live sessions rather than
        // triggering a teardown-and-rebuild.
        _idleUnload.Cancel();
        var leaseId = Guid.NewGuid().ToString("N");
        lock (_leaseLock)
        {
            _activeLeases.Add(leaseId);
            AppLog.Debug("OcrRuntimeService", $"[OcrRuntime] AcquireLease {leaseId}; count={_activeLeases.Count}");
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
            AppLog.Debug("OcrRuntimeService", $"[OcrRuntime] ReleaseLease {leaseId}; count={_activeLeases.Count}; unloadWhenIdle={unloadWhenIdle}");
        }

        if (shouldUnload)
        {
            // Scheduled, not immediate: an unload here followed by the next capture's reload seconds later is the
            // teardown-then-rebuild sequence that precedes the crashes. A capture within the idle window cancels
            // this and reuses the sessions; a genuinely idle app still gets the memory back.
            _idleUnload.NotifyUse();
            _gpuDevice.NotifyUse();
        }
    }

    /// <summary>Loads detection and the recognizer for <paramref name="language"/>, whichever of them is not loaded yet.</summary>
    public Task EnsureLoadedAsync(OCRLanguage language, CancellationToken ct = default) =>
        EnsureLoadedAsync(language, recognition: true, ct);

    /// <summary>
    /// Loads detection alone, which is all the overlay's scan uses: the recognizer and its dictionary wait for the first
    /// <see cref="EnsureLoadedAsync(OCRLanguage, CancellationToken)"/>. Building them took 135 ms of every cold load on
    /// DirectML that a scan never used. <paramref name="language"/> only picks the model install that is checked.
    /// </summary>
    public Task EnsureDetectionLoadedAsync(OCRLanguage language, CancellationToken ct = default) =>
        EnsureLoadedAsync(language, recognition: false, ct);

    private bool HasLoaded(OCRLanguage language, bool recognition) =>
        _detSession != null && (!recognition || (_recSession != null && _loadedLanguage == language));

    private async Task EnsureLoadedAsync(OCRLanguage language, bool recognition, CancellationToken ct)
    {
        // Resolved up front so LoadedLanguage always names the model actually in memory. Loading "Auto" would
        // record a language nobody can load, and every later comparison against a concrete language would miss.
        language = OcrLanguageResolver.Resolve(language);

        if (HasLoaded(language, recognition))
            return;

        // From here on, never on the caller's thread. The overlay's text scan calls this from the UI thread, and
        // when the model files are installed every await below completes synchronously — so building the two
        // ONNX sessions (~0.4 s on DirectML) and a language swap's wait for a running inference (up to
        // SwapTimeout) all ran there: the capture overlay froze whenever OCR had been idle-unloaded.
        await _loadLock.WaitAsync(ct).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            if (HasLoaded(language, recognition))
                return;

            bool ready = await _aiResourceService.EnsureOCRAsync(language, ct);
            if (!ready)
                return;

            var paths = _aiResourceService.GetOCRPaths(language);

            // Exclusivity is taken only now — after the (possibly long) download above — and held across the whole
            // teardown+rebuild, so a concurrent inference waits for the new sessions instead of seeing none.
            if (!_useGate.TryBeginExclusive(SwapTimeout, out var swap))
            {
                // Deliberately keep the currently loaded language rather than free sessions someone is running on.
                // Wrong-language output is recoverable; an access violation is not.
                AppLog.Warning(
                    "OcrRuntime.LanguageSwapTimedOut",
                    $"OCR still busy after {SwapTimeout.TotalSeconds:0}s; staying on {_loadedLanguage?.ToString() ?? "none"} instead of loading {language}.");
                return;
            }

            using (swap)
            {
                // A language change replaces only the recognizer. The detector, the same for every language, stays, and
                // with it the GPU device the new recognizer then takes over. The language auto-detect swaps once per
                // installed language.
                _detSession ??= CreateSession(paths.Det, "detection");
                if (recognition && (_recSession == null || _loadedLanguage != language))
                {
                    DisposeRecognizer();
                    bool detectionOnGpu = !_onCpu;
                    _recSession = CreateSession(paths.Rec, $"recognition {language}");
                    if (detectionOnGpu && _onCpu)
                    {
                        // The recognizer could not be built on the GPU: detection joins it on the CPU. Cleared first, so a
                        // failed rebuild leaves no disposed session behind for an inference to run on.
                        _detSession.Dispose();
                        _detSession = null;
                        _detSession = CreateSession(paths.Det, "detection");
                    }

                    _loadedLanguage = language;
                    _dictionary = LoadDictionaryWithEncodingFallback(paths.Dict);
                    _dictionary.Insert(0, string.Empty);
                }
            }

            AppLog.Information($"OcrRuntime.Loaded: {(recognition ? language.ToString() : "detection")}");
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <summary>
    /// Builds one session on the GPU (DirectML on Windows) and, when a GPU provider was appended but the session fails to
    /// build, again on the CPU alone. DirectML can be appended without error yet fail at <see cref="InferenceSession"/>
    /// construction on some Windows 10 configurations (older system DirectML.dll / DX12 driver) — without the CPU attempt
    /// OCR throws there and silently produces nothing while working on Windows 11. Once a session is on the CPU, the
    /// next is built there directly, so detection and recognition never split across backends. Caller MUST hold
    /// exclusive access via <see cref="_useGate"/>.
    /// </summary>
    private InferenceSession CreateSession(string modelPath, string part)
    {
        var stopwatch = Stopwatch.StartNew();
        var session = OnnxProviderConfigurator.CreateWithCpuFallback(
            () => new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL },
            options => new InferenceSession(modelPath, options),
            "OcrRuntime",
            _onCpu ? static _ => null : OnnxProviderConfigurator.AppendGpuProvidersWithFallback,
            out string provider);
        // Logged to the file sink so a Win10-vs-Win11 discrepancy is diagnosable without a debugger.
        AppLog.Information("OcrRuntime.SessionCreated", $"{part}: {provider} in {stopwatch.ElapsedMilliseconds} ms");
        _onCpu = provider == OnnxProviderConfigurator.CpuProvider;
        _gpuDevice.SessionBuilt(provider);
        return session;
    }

    public void Unload()
    {
        lock (_leaseLock)
        {
            if (_activeLeases.Count > 0)
            {
                return;
            }
        }

        ForceUnload();
        // Logged to the file sink because the unload is the interesting half of the load/unload cycle: without it
        // the log showed sessions being created with nothing explaining why, which is exactly what made the crash
        // pattern hard to read.
        AppLog.Information("OcrRuntime.Unloaded");
        // Hand the freed native memory back once the app is genuinely idle — the trim service owns that idle
        // window now, so a re-scan in the meantime cancels this instead of trimming and immediately reloading.
        ProcessMemoryTrimService.RequestIdleTrimAsync("ocr-unloaded")
            .Forget("MemoryTrim.OcrUnloaded");
    }

    public void Dispose()
    {
        AIResourceService.RequestGlobalUnload -= HandleGlobalUnload;
        _idleUnload.Cancel();
        ForceUnload(ShutdownUnloadTimeout);
        _gpuDevice.Release();
        _loadLock.Dispose();
        // _inferenceLock is deliberately not disposed: if the unload above timed out, a thread may still be waiting
        // on it, and disposing it under a waiter throws. SemaphoreSlim needs no disposal unless its wait handle was
        // materialised, which this one's never is.
    }

    /// <summary>
    /// Disposes the sessions once nothing is running on them. If inference is still in flight after
    /// <paramref name="timeout"/> the sessions are deliberately LEAKED — the process reclaims them at exit, whereas
    /// disposing them under a running inference faults it with an access violation.
    /// </summary>
    private void ForceUnload(TimeSpan? timeout = null)
    {
        if (!_useGate.TryBeginExclusive(timeout ?? SwapTimeout, out var swap))
        {
            AppLog.Warning(
                "OcrRuntime.UnloadTimedOut",
                "OCR sessions still in use; skipping unload rather than disposing them mid-inference.");
            return;
        }

        using (swap)
        {
            DisposeSessions();
        }
    }

    /// <summary>Caller MUST hold exclusive access via <see cref="_useGate"/>.</summary>
    private void DisposeSessions()
    {
        _detSession?.Dispose();
        _detSession = null;
        DisposeRecognizer();
        // The next load tries the GPU again, after a driver reset say.
        _onCpu = false;
    }

    /// <summary>Caller MUST hold exclusive access via <see cref="_useGate"/>.</summary>
    private void DisposeRecognizer()
    {
        _recSession?.Dispose();
        _recSession = null;
        _loadedLanguage = null;
        _dictionary = new List<string>();
    }

    // AI turned off or a module removed: the user is asking for the memory back, the device's included.
    private void HandleGlobalUnload()
    {
        ForceUnload();
        _gpuDevice.Release();
    }

    private static List<string> LoadDictionaryWithEncodingFallback(string path)
    {
        try { return File.ReadAllLines(path, Encoding.UTF8).AsValueEnumerable().ToList(); }
        catch (Exception ex)
        {
            AppLog.Warning("OcrRuntime.LoadDictionary.Utf8Fallback", ex);
            return File.ReadAllLines(path, Encoding.GetEncoding("GBK")).AsValueEnumerable().ToList();
        }
    }
}
