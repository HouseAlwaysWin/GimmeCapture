using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.AI;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Interfaces;
using SkiaSharp;

namespace GimmeCapture.Services.OCR;

/// <summary>
/// Cross-model language probe: detect boxes once, recognise a handful of them with every installed candidate
/// recogniser, and let <see cref="OcrScriptDecisionPolicy"/> judge which model actually fits the content.
///
/// Comparing models this way is the only approach that works across all five languages. The obvious cheaper trick —
/// recognise once, then count characters — cannot work, because a recogniser can only emit characters from its own
/// dictionary: the Chinese dictionaries contain five kana entries between them, so a Chinese-model pass over
/// Japanese text produces no kana to count, no matter how much kana is on screen.
///
/// One instance is meant to live for one snip session: the resolved language is cached because switching recognisers
/// rebuilds the ONNX sessions, which costs far more than the sampled inference does.
/// </summary>
public sealed class OcrScriptDetector : IOcrScriptDetector
{
    /// <summary>Boxes sampled per candidate. Enough for a stable verdict; the probe cost scales with this.</summary>
    private const int SampleBoxCount = 4;

    private readonly AIResourceService _aiResourceService;
    private readonly SemaphoreSlim _probeLock = new(1, 1);
    private OCRLanguage? _sessionLanguage;

    public OcrScriptDetector(AIResourceService aiResourceService)
    {
        _aiResourceService = aiResourceService ?? throw new ArgumentNullException(nameof(aiResourceService));
    }

    public bool HasInstalledCandidate => InstalledCandidates().Count > 0;

    public async Task<OcrScriptDetectionResult> DetectAsync(
        SKBitmap bitmap,
        IOCREngine engine,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentNullException.ThrowIfNull(engine);

        var candidates = InstalledCandidates();
        if (candidates.Count == 0)
        {
            return Unprobed(OcrLanguageResolver.PreferredChineseVariant, Array.Empty<SKRectI>());
        }

        // Serialised because probing swaps the shared OCR runtime's sessions; two probes interleaving would have
        // each recognising boxes with whichever model the other just loaded.
        await _probeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_sessionLanguage is { } cached && candidates.Contains(cached))
            {
                await engine.EnsureLoadedAsync(cached, ct).ConfigureAwait(false);
                return Unprobed(cached, engine.DetectText(bitmap));
            }

            if (candidates.Count == 1)
            {
                _sessionLanguage = candidates[0];
                await engine.EnsureLoadedAsync(candidates[0], ct).ConfigureAwait(false);
                return Unprobed(candidates[0], engine.DetectText(bitmap));
            }

            await engine.EnsureLoadedAsync(candidates[0], ct).ConfigureAwait(false);
            var boxes = engine.DetectText(bitmap);
            int sampleCount = Math.Min(SampleBoxCount, boxes.Count);
            if (sampleCount == 0)
            {
                // Nothing to judge. Don't cache: the next capture may well have text.
                return Unprobed(OcrLanguageResolver.PreferredChineseVariant, boxes);
            }

            var rawByLanguage = new Dictionary<OCRLanguage, IReadOnlyList<(string Text, float Confidence)>>(candidates.Count);
            var probed = new List<OcrScriptCandidate>(candidates.Count);

            foreach (var language in candidates)
            {
                ct.ThrowIfCancellationRequested();
                await engine.EnsureLoadedAsync(language, ct).ConfigureAwait(false);

                var raw = new List<(string Text, float Confidence)>(sampleCount);
                var samples = new List<OcrScriptSample>(sampleCount);
                for (int i = 0; i < sampleCount; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var recognition = engine.RecognizeText(bitmap, boxes[i], ct);
                    raw.Add(recognition);
                    samples.Add(new OcrScriptSample(recognition.text, recognition.confidence));
                }

                rawByLanguage[language] = raw;
                probed.Add(new OcrScriptCandidate(language, samples));
            }

            var decision = OcrScriptDecisionPolicy.Decide(probed, OcrLanguageResolver.PreferredChineseVariant);
            AppLog.Information(
                $"OcrScriptDetector.Detected: {decision.Language} (reason={decision.Reason}, confident={decision.Confident}) "
                + $"from {sampleCount} sampled boxes across {candidates.Count} recognisers.");

            // Cache even an unconfident verdict: re-probing every capture in a session that has nothing to go on
            // just repeats the same expensive model shuffle for the same answer.
            _sessionLanguage = decision.Language;
            await engine.EnsureLoadedAsync(decision.Language, ct).ConfigureAwait(false);

            var winnerSamples = rawByLanguage.TryGetValue(decision.Language, out var samplesForWinner)
                ? samplesForWinner
                : Array.Empty<(string Text, float Confidence)>();

            return new OcrScriptDetectionResult(decision.Language, boxes, winnerSamples, Probed: true);
        }
        finally
        {
            _probeLock.Release();
        }
    }

    private static OcrScriptDetectionResult Unprobed(OCRLanguage language, IReadOnlyList<SKRectI> boxes) =>
        new(language, boxes, Array.Empty<(string Text, float Confidence)>(), Probed: false);

    /// <summary>
    /// Probe candidates whose model files are actually on disk. A partially installed set degrades gracefully
    /// (fewer languages distinguished) instead of failing.
    /// </summary>
    private List<OCRLanguage> InstalledCandidates() =>
        OcrLanguageResolver.WhereReady(OcrLanguageResolver.ProbeCandidates, _aiResourceService.IsOCRReady);
}
