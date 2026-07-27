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
    private readonly AIResourceService _aiResourceService;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly object _leaseLock = new();
    private readonly HashSet<string> _activeLeases = new();
    private InferenceSession? _detSession;
    private InferenceSession? _recSession;
    private OCRLanguage? _loadedLanguage;
    private List<string> _dictionary = new();

    public OcrRuntimeService(AIResourceService aiResourceService)
    {
        _aiResourceService = aiResourceService ?? throw new ArgumentNullException(nameof(aiResourceService));
        AIResourceService.RequestGlobalUnload += HandleGlobalUnload;
    }

    public bool IsLoaded => _loadedLanguage.HasValue && _detSession != null && _recSession != null;
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

    public InferenceSession? DetectionSession => _detSession;
    public InferenceSession? RecognitionSession => _recSession;
    public IReadOnlyList<string> Dictionary => _dictionary;

    public string AcquireLease()
    {
        ProcessMemoryTrimService.NotifyActivity("ocr");
        var leaseId = Guid.NewGuid().ToString("N");
        lock (_leaseLock)
        {
            _activeLeases.Add(leaseId);
            Debug.WriteLine($"[OcrRuntime] AcquireLease {leaseId}; count={_activeLeases.Count}");
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
            Debug.WriteLine($"[OcrRuntime] ReleaseLease {leaseId}; count={_activeLeases.Count}; unloadWhenIdle={unloadWhenIdle}");
        }

        if (shouldUnload)
        {
            Unload();
        }
    }

    public async Task EnsureLoadedAsync(OCRLanguage language, CancellationToken ct = default)
    {
        // Resolved up front so LoadedLanguage always names the model actually in memory. Loading "Auto" would
        // record a language nobody can load, and every later comparison against a concrete language would miss.
        language = OcrLanguageResolver.Resolve(language);

        if (IsLoaded && _loadedLanguage == language)
            return;

        await _loadLock.WaitAsync(ct);
        try
        {
            if (IsLoaded && _loadedLanguage == language)
                return;

            bool ready = await _aiResourceService.EnsureOCRAsync(language, ct);
            if (!ready)
                return;

            var paths = _aiResourceService.GetOCRPaths(language);

            ForceUnload();
            (_detSession, _recSession) = CreateOcrSessionsWithCpuFallback(paths.Det, paths.Rec);
            _loadedLanguage = language;
            _dictionary = LoadDictionaryWithEncodingFallback(paths.Dict);
            _dictionary.Insert(0, string.Empty);
            Debug.WriteLine($"[OcrRuntime] Loaded OCR runtime for {language}.");
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <summary>
    /// Builds the detection + recognition sessions, attempting GPU execution providers (CUDA/DirectML) first and
    /// falling back to a CPU-only session if GPU session construction throws. DirectML can be appended without
    /// error yet fail at <see cref="InferenceSession"/> construction on some Windows 10 configurations (older
    /// system DirectML.dll / DX12 driver) — without this fallback OCR throws there and silently produces nothing
    /// (only a Debug line) while working on Windows 11. Both sessions share one provider path (GPU or CPU) so
    /// detection and recognition never split across backends.
    /// </summary>
    private static (InferenceSession Det, InferenceSession Rec) CreateOcrSessionsWithCpuFallback(string detPath, string recPath)
    {
        try
        {
            var gpuOptions = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
            OnnxProviderConfigurator.AppendGpuProvidersWithFallback(gpuOptions);
            var det = new InferenceSession(detPath, gpuOptions);
            InferenceSession rec;
            try
            {
                rec = new InferenceSession(recPath, gpuOptions);
            }
            catch
            {
                det.Dispose(); // don't leak the GPU detection session when recognition fails the same way
                throw;
            }
            AppLog.Information("OcrRuntime.SessionCreated: GPU providers attempted (DirectML/CUDA where available).");
            return (det, rec);
        }
        catch (Exception ex)
        {
            // GPU (typically DirectML) session construction failed — common on some Windows 10 setups. Retry
            // CPU-only so OCR still works instead of silently failing. Logged to the file sink so a Win10-vs-Win11
            // discrepancy is diagnosable without a debugger.
            AppLog.Warning("OcrRuntime.GpuSessionCreateFailed.CpuFallback", ex);
            var cpuOptions = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
            var det = new InferenceSession(detPath, cpuOptions);
            var rec = new InferenceSession(recPath, cpuOptions);
            AppLog.Information("OcrRuntime.SessionCreated: CPU fallback (GPU execution provider unavailable/failed).");
            return (det, rec);
        }
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
        // OCR is a one-shot scan, so reclaim promptly after it finishes. Debounced (5s): a re-scan within the
        // window cancels this rather than trimming then immediately reloading.
        ProcessMemoryTrimService.RequestIdleTrimAsync("ocr-unloaded", TimeSpan.FromSeconds(5))
            .Forget("MemoryTrim.OcrUnloaded");
        Debug.WriteLine("[OcrRuntime] Unloaded OCR runtime.");
    }

    public void Dispose()
    {
        AIResourceService.RequestGlobalUnload -= HandleGlobalUnload;
        ForceUnload();
        _loadLock.Dispose();
    }

    private void ForceUnload()
    {
        _detSession?.Dispose();
        _detSession = null;
        _recSession?.Dispose();
        _recSession = null;
        _loadedLanguage = null;
        _dictionary = new List<string>();
    }

    private void HandleGlobalUnload()
    {
        ForceUnload();
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
