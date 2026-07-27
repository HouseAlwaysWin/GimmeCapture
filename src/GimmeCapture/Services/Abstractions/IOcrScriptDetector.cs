using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Interfaces;
using SkiaSharp;

namespace GimmeCapture.Services.OCR;

/// <summary>
/// What the probe resolved, plus the work it already did so the caller need not repeat it.
/// <paramref name="Boxes"/> are the detection boxes for the same bitmap (the detection model is shared by every
/// language, so they are valid whichever recogniser wins) and <paramref name="SampledRecognitions"/> is the winning
/// model's RAW output for <c>Boxes[0..N)</c> — unsanitised, exactly as <see cref="IOCREngine.RecognizeText"/>
/// returned it. Empty when no probe ran (single candidate, cached result, or nothing installed).
/// </summary>
public sealed record OcrScriptDetectionResult(
    OCRLanguage Language,
    IReadOnlyList<SKRectI> Boxes,
    IReadOnlyList<(string Text, float Confidence)> SampledRecognitions,
    bool Probed);

/// <summary>
/// Resolves <see cref="OCRLanguage.Auto"/> to a real language by running the installed recognisers against the same
/// sampled boxes and comparing what each made of them.
///
/// Implementations are expected to be scoped to one snip session and to cache the first result for that session:
/// swapping recogniser models means rebuilding ONNX sessions, so probing every capture would add seconds to each one.
/// </summary>
public interface IOcrScriptDetector
{
    /// <summary>False when no recogniser is installed at all, i.e. the caller should report a missing module.</summary>
    bool HasInstalledCandidate { get; }

    /// <summary>
    /// Leaves <paramref name="engine"/> loaded with the resolved language, so the caller can recognise immediately.
    /// </summary>
    Task<OcrScriptDetectionResult> DetectAsync(SKBitmap bitmap, IOCREngine engine, CancellationToken ct = default);
}
