using System;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Services.Core.Interfaces;

namespace GimmeCapture.Services.Translation;

/// <summary>
/// A translation engine that keeps its model in memory between translations. The app keeps one for the whole run
/// (<see cref="TranslationRuntimeService"/>) and every capture overlay translates through it.
/// </summary>
public interface ITranslationModelEngine : ITranslationEngine, IDisposable
{
    bool IsModelLoaded { get; }

    /// <summary>
    /// Loads the selected model before the first translation needs it. A translation that starts meanwhile waits for
    /// this load instead of starting one of its own.
    /// </summary>
    Task PreloadAsync(CancellationToken ct = default);

    /// <summary>
    /// Frees the model now, unless a translation is using it; it is then freed once the engine's idle countdown, which
    /// starts as that translation ends, runs out. Never blocks, so the UI thread may call it.
    /// </summary>
    void ReleaseModel();

    /// <summary>
    /// Frees the model, waiting up to <paramref name="timeout"/> for a translation still using it. Blocks, so never
    /// call it on the UI thread. False when that translation outlasted the wait and the model is still loaded.
    /// </summary>
    bool UnloadModel(TimeSpan timeout);
}
