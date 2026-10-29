using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Models;

namespace GimmeCapture.Tests;

/// <summary>
/// A translation engine without a model: it answers with <see cref="Answer"/>, records every text it is asked to
/// translate, and counts what it is told to do with the model it pretends to hold.
/// </summary>
internal sealed class FakeTranslationModelEngine : ITranslationModelEngine
{
    private readonly object _lock = new();
    private readonly List<string> _requests = new();
    private int _preloadCount;
    private int _releaseCount;
    private int _unloadCount;
    private int _disposeCount;

    /// <summary>The translation of each text; by default the text itself, untranslated.</summary>
    public Func<string, string> Answer { get; set; } = static text => text;

    public TranslationEngine EngineType => TranslationEngine.LlamaSharp;

    public bool IsModelLoaded { get; set; }

    /// <summary>Every text asked for, in order.</summary>
    public IReadOnlyList<string> Requests
    {
        get
        {
            lock (_lock)
            {
                return _requests.ToArray();
            }
        }
    }

    public int PreloadCount => Volatile.Read(ref _preloadCount);
    public int ReleaseCount => Volatile.Read(ref _releaseCount);
    public int UnloadCount => Volatile.Read(ref _unloadCount);
    public int DisposeCount => Volatile.Read(ref _disposeCount);

    /// <summary>The timeout the last <see cref="UnloadModel"/> was given.</summary>
    public TimeSpan? LastUnloadTimeout { get; private set; }

    public Task<string> TranslateAsync(
        string text,
        OCRLanguage sourceLang,
        TranslationLanguage targetLang,
        CancellationToken ct = default)
    {
        lock (_lock)
        {
            _requests.Add(text);
        }

        return Task.FromResult(Answer(text));
    }

    public Task PreloadAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref _preloadCount);
        return Task.CompletedTask;
    }

    public void ReleaseModel() => Interlocked.Increment(ref _releaseCount);

    public bool UnloadModel(TimeSpan timeout)
    {
        LastUnloadTimeout = timeout;
        Interlocked.Increment(ref _unloadCount);
        return true;
    }

    public void Dispose() => Interlocked.Increment(ref _disposeCount);
}
