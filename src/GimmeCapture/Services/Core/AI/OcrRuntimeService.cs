using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Models;
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
            Unload();
        }
    }

    public async Task EnsureLoadedAsync(OCRLanguage language, CancellationToken ct = default)
    {
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

            var options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
            };

            try { options.AppendExecutionProvider_CUDA(0); } catch { }
            try { options.AppendExecutionProvider_DML(0); } catch { }

            ForceUnload();
            _detSession = new InferenceSession(paths.Det, options);
            _recSession = new InferenceSession(paths.Rec, options);
            _loadedLanguage = language;
            _dictionary = LoadDictionaryWithEncodingFallback(paths.Dict);
            _dictionary.Insert(0, string.Empty);
        }
        finally
        {
            _loadLock.Release();
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
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
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
        catch { return File.ReadAllLines(path, Encoding.GetEncoding("GBK")).AsValueEnumerable().ToList(); }
    }
}
