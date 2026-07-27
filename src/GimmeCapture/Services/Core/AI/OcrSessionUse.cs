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

    internal OcrSessionUse(
        Action? release,
        InferenceSession? detection,
        InferenceSession? recognition,
        IReadOnlyList<string> dictionary)
    {
        _release = release;
        Detection = detection;
        Recognition = recognition;
        Dictionary = dictionary;
    }

    public InferenceSession? Detection { get; }
    public InferenceSession? Recognition { get; }
    public IReadOnlyList<string> Dictionary { get; }

    /// <summary>Idempotent: releasing twice would hand a second caller into inference alongside the first.</summary>
    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}
