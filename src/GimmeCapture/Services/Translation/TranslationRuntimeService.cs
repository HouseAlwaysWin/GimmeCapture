using System;

namespace GimmeCapture.Services.Translation;

/// <summary>
/// The translation model and the translations already made, kept for the whole run: every capture overlay's session
/// translates through the one engine held here. Each overlay used to build its own engine and cache and free the model
/// at the end of every translate press, so each press loaded the 2.5 GB model again and no translation was ever
/// answered from the cache of an earlier overlay.
/// </summary>
/// <remarks>
/// The model is freed once it has gone unused for <see cref="LlamaSharpTranslationEngine.UnloadIdleDelay"/>, when AI
/// is turned off (<see cref="ReleaseModel"/>), before a module's files are removed, and at exit
/// (<see cref="Dispose"/>).
/// </remarks>
public sealed class TranslationRuntimeService : IDisposable
{
    /// <summary>
    /// How long a module removal waits for a translation still generating before it gives up on freeing the model:
    /// as long as OCR waits for its sessions on the same occasion.
    /// </summary>
    private static readonly TimeSpan RemovalUnloadTimeout = TimeSpan.FromSeconds(30);

    private readonly Func<AIResourceService, IAppSettingsService, ITranslationCache, ITranslationModelEngine> _createEngine;
    private readonly ITranslationCache _cache;
    private readonly object _gate = new();
    private ITranslationModelEngine? _engine;
    private bool _disposed;

    public TranslationRuntimeService()
        : this(
            static (aiResourceService, settingsService, cache) =>
                new LlamaSharpTranslationEngine(aiResourceService, settingsService, cache),
            new InMemoryTranslationCache())
    {
    }

    internal TranslationRuntimeService(
        Func<AIResourceService, IAppSettingsService, ITranslationCache, ITranslationModelEngine> createEngine,
        ITranslationCache cache)
    {
        _createEngine = createEngine ?? throw new ArgumentNullException(nameof(createEngine));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));

        // A loaded GGUF file is mapped into memory, and Windows will not delete a mapped file, so a module removal has
        // to unload the model before it deletes anything.
        AIResourceService.RequestGlobalUnload += HandleGlobalUnload;
    }

    // None once disposed: a module removal already raising its unload when this unsubscribed still calls in.
    private ITranslationModelEngine? CurrentEngine
    {
        get
        {
            lock (_gate)
            {
                return _disposed ? null : _engine;
            }
        }
    }

    /// <summary>
    /// The engine every overlay translates through, built by the first overlay to ask. The AI resources and settings
    /// are the app's own, so every later overlay passes the same ones.
    /// </summary>
    internal ITranslationModelEngine GetEngine(AIResourceService aiResourceService, IAppSettingsService settingsService)
    {
        ArgumentNullException.ThrowIfNull(aiResourceService);
        ArgumentNullException.ThrowIfNull(settingsService);

        lock (_gate)
        {
            // Past exit nothing would ever free the model a new engine loads.
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _engine ??= _createEngine(aiResourceService, settingsService, _cache);
        }
    }

    /// <summary>
    /// Frees the model now, as turning AI off asks. A translation still running keeps it until the idle countdown that
    /// starts as it ends runs out. Never blocks.
    /// </summary>
    public void ReleaseModel()
    {
        CurrentEngine?.ReleaseModel();
    }

    // Raised on the removal's own thread, never the UI thread, right before a module's files are deleted: waiting
    // there for a translation to finish holds up nothing the user is looking at, and the delete fails while the model
    // is loaded.
    private void HandleGlobalUnload()
    {
        if (CurrentEngine is { } engine && !engine.UnloadModel(RemovalUnloadTimeout))
        {
            AppLog.Warning(
                "TranslationRuntime.UnloadTimedOut",
                $"Translation still running after {RemovalUnloadTimeout.TotalSeconds:0}s; the model stays loaded.");
        }
    }

    public void Dispose()
    {
        ITranslationModelEngine? engine;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            engine = _engine;
        }

        AIResourceService.RequestGlobalUnload -= HandleGlobalUnload;
        engine?.Dispose();
    }
}
