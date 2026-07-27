using System;
using System.Collections.Generic;
using GimmeCapture.Models;

namespace GimmeCapture.Services.OCR;

/// <summary>One sampled recognition: the raw recogniser output for a box plus its mean per-character confidence.</summary>
public sealed record OcrScriptSample(string Text, float Confidence);

/// <summary>Everything one candidate recogniser produced for the sampled boxes.</summary>
public sealed record OcrScriptCandidate(OCRLanguage Language, IReadOnlyList<OcrScriptSample> Samples);

/// <summary>
/// <see cref="Reason"/> names the rule that decided it ("kana", "score", "below-threshold", …). It is a log tag
/// only — never shown to the user and never branched on, so nothing depends on its exact wording.
/// </summary>
public sealed record OcrScriptDecision(OCRLanguage Language, bool Confident, string Reason);

/// <summary>
/// Picks the OCR language from what every candidate recogniser made of the same sampled boxes.
///
/// The one thing this must NOT do is compare raw confidence across models and take the maximum. Confidence here is
/// the mean per-character argmax probability, and the models have wildly different class counts (en_dict ~96 entries
/// vs chinese_cht_dict ~8400), so the English recogniser is systematically over-confident on input it cannot even
/// represent. Character-set evidence is therefore the primary signal — kana and hangul are emitted by exactly one
/// model each and by nothing else, so they are effectively zero-false-positive — and score only breaks ties the
/// character sets cannot (kanji-only Japanese vs Chinese).
///
/// Chinese is a single logical candidate: the traditional/simplified split has no character-set signal (each model
/// only ever emits its own variant), so the caller passes the variant it wants and this never tries to guess it.
/// </summary>
public static class OcrScriptDecisionPolicy
{
    /// <summary>Characters of a script needed before it counts as evidence — one stray glyph is noise.</summary>
    public const int MinScriptEvidence = 2;

    /// <summary>
    /// Below this a candidate is treated as having recognised nothing useful. Matches the 0.3 confidence floor the
    /// quick-OCR fragment filter already applies (0.3 * 100 = 30).
    /// </summary>
    public const double MinWinningScore = 30d;

    public static OcrScriptDecision Decide(
        IReadOnlyList<OcrScriptCandidate> candidates,
        OCRLanguage chineseVariant)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        if (candidates.Count == 0)
        {
            return new OcrScriptDecision(chineseVariant, false, "no-candidates");
        }

        var scores = new Dictionary<OCRLanguage, double>(candidates.Count);
        int cjkEvidence = 0;
        int kanaEvidence = 0;
        int hangulEvidence = 0;
        int latinEvidence = 0;

        foreach (var candidate in candidates)
        {
            scores[candidate.Language] = MeanScore(candidate);

            foreach (var sample in candidate.Samples)
            {
                string text = sample.Text ?? string.Empty;
                cjkEvidence = Math.Max(cjkEvidence, OcrScriptCharacters.CountCjk(text));

                switch (candidate.Language)
                {
                    case OCRLanguage.Japanese:
                        kanaEvidence += OcrScriptCharacters.CountKana(text);
                        break;
                    case OCRLanguage.Korean:
                        hangulEvidence += OcrScriptCharacters.CountHangul(text);
                        break;
                    case OCRLanguage.English:
                        latinEvidence += OcrScriptCharacters.CountLatinLetters(text);
                        break;
                }
            }
        }

        // Hard character-set evidence, strongest first. Each is gated on the candidate that produced it also being
        // a usable recognition overall, so a model hallucinating kana at throwaway confidence cannot hijack the pick.
        if (kanaEvidence >= MinScriptEvidence
            && kanaEvidence >= hangulEvidence
            && Score(scores, OCRLanguage.Japanese) >= MinWinningScore)
        {
            return new OcrScriptDecision(OCRLanguage.Japanese, true, "kana");
        }

        if (hangulEvidence >= MinScriptEvidence
            && Score(scores, OCRLanguage.Korean) >= MinWinningScore)
        {
            return new OcrScriptDecision(OCRLanguage.Korean, true, "hangul");
        }

        // Nothing produced CJK, so the capture is latin (or empty) — take the recogniser built for it.
        if (cjkEvidence < MinScriptEvidence
            && latinEvidence >= MinScriptEvidence
            && Score(scores, OCRLanguage.English) >= MinWinningScore)
        {
            return new OcrScriptDecision(OCRLanguage.English, true, "latin");
        }

        // Score tie-break. English is out of the running once the content is demonstrably CJK: it physically cannot
        // represent those characters, so any score it posts is measuring the wrong thing.
        bool excludeEnglish = cjkEvidence >= MinScriptEvidence;
        OCRLanguage best = chineseVariant;
        double bestScore = double.NegativeInfinity;

        foreach (var (language, score) in scores)
        {
            if (excludeEnglish && language == OCRLanguage.English)
            {
                continue;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = language;
            }
        }

        return bestScore >= MinWinningScore
            ? new OcrScriptDecision(NormalizeChinese(best, chineseVariant), true, "score")
            : new OcrScriptDecision(chineseVariant, false, "below-threshold");
    }

    private static double Score(IReadOnlyDictionary<OCRLanguage, double> scores, OCRLanguage language) =>
        scores.TryGetValue(language, out double score) ? score : double.NegativeInfinity;

    /// <summary>Any Chinese winner collapses to the caller's variant — the probe never picks traditional vs simplified.</summary>
    private static OCRLanguage NormalizeChinese(OCRLanguage language, OCRLanguage chineseVariant) =>
        language is OCRLanguage.TraditionalChinese or OCRLanguage.SimplifiedChinese ? chineseVariant : language;

    /// <summary>
    /// Mean of a per-sample score shaped like the engine's own candidate scoring: confidence dominates, a little
    /// credit for producing real characters, and a heavy penalty per character that belongs to no script at all
    /// (which is exactly what a mismatched recogniser emits). Empty samples score zero rather than being skipped —
    /// a model that returns nothing for a box has failed that box.
    /// </summary>
    private static double MeanScore(OcrScriptCandidate candidate)
    {
        if (candidate.Samples.Count == 0)
        {
            return double.NegativeInfinity;
        }

        double total = 0d;
        foreach (var sample in candidate.Samples)
        {
            string text = sample.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            int useful = OcrScriptCharacters.CountUseful(text);
            int suspicious = OcrScriptCharacters.CountSuspicious(text);
            total += (sample.Confidence * 100d) + (Math.Min(useful, 8) * 0.25d) - (suspicious * 6d);
        }

        return total / candidate.Samples.Count;
    }
}
