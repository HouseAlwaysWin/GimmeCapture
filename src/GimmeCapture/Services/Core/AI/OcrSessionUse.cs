using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.ML.OnnxRuntime;

namespace GimmeCapture.Services.Core.AI;

/// <summary>
/// The OCR sessions borrowed for the duration of one inference, plus the dictionary that belongs to them.
///
/// The three travel together on purpose: they are swapped as a set when the language changes, and a recognition
/// decoded against a different language's dictionary than the one that produced it is garbage. Holding this scope
/// blocks that swap, which is what stops <c>InferenceSession.Run</c> being called on a disposed native session —
/// a fault that terminates the process outright (0xC0000005) instead of raising a catchable exception.
///
/// <see cref="Detection"/> and <see cref="Recognition"/> are null when nothing is loaded yet; callers must check.
/// </summary>
public sealed class OcrSessionUse : IDisposable
{
    private Action? _release;
    private readonly Func<int, float[]>? _detectionInput;

    internal OcrSessionUse(
        Action? release,
        InferenceSession? detection,
        InferenceSession? recognition,
        IReadOnlyList<string> dictionary,
        Func<int, float[]>? detectionInput = null)
    {
        _release = release;
        Detection = detection;
        Recognition = recognition;
        Dictionary = dictionary;
        _detectionInput = detectionInput;
    }

    public InferenceSession? Detection { get; }
    public InferenceSession? Recognition { get; }
    public IReadOnlyList<string> Dictionary { get; }

    /// <summary>
    /// At least <paramref name="length"/> floats for detection's input: the runtime's own, kept while the detector stays
    /// loaded. Only this scope uses it, inference running one at a time, and only until it is disposed.
    /// </summary>
    public float[] DetectionInput(int length) => _detectionInput?.Invoke(length) ?? new float[length];

    /// <summary>Idempotent: releasing twice would hand a second caller into inference alongside the first.</summary>
    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}
