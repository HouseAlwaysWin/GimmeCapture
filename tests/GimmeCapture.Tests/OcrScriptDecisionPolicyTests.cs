using System;
using System.Collections.Generic;
using GimmeCapture.Models;
using GimmeCapture.Services.OCR;

namespace GimmeCapture.Tests;

/// <summary>
/// The cross-model language probe hands every candidate recogniser's sampled output to this policy. These tests
/// pin the two rules that make it work at all: character-set evidence (kana / hangul / latin) outranks raw score,
/// because confidence is NOT comparable across models with wildly different class counts (en_dict ~96 entries vs
/// chinese_cht_dict ~8400) — and Chinese is a single logical candidate whose script variant comes from the caller.
/// </summary>
public class OcrScriptDecisionPolicyTests
{
    private const string Kanji = "日本語";                // kanji only, no kana
    private const string KanaText = "こんにち";         // hiragana
    private const string Hangul = "안녕하";                 // hangul syllables
    private const string ChineseText = "測試文字";      // traditional Chinese
    private const string SingleKatakana = "ト";                     // one stray katakana
    private const string Garbage = "░▒▓■";          // block glyphs: no script at all

    [Fact]
    public void Decide_PrefersJapanese_WhenKanaAppears_EvenIfChineseScoresHigher()
    {
        var decision = OcrScriptDecisionPolicy.Decide(
            [
                Candidate(OCRLanguage.TraditionalChinese, (ChineseText, 0.95f)),
                Candidate(OCRLanguage.Japanese, (KanaText, 0.55f))
            ],
            OCRLanguage.TraditionalChinese);

        Assert.Equal(OCRLanguage.Japanese, decision.Language);
        Assert.True(decision.Confident);
    }

    [Fact]
    public void Decide_PrefersKorean_WhenHangulAppears()
    {
        var decision = OcrScriptDecisionPolicy.Decide(
            [
                Candidate(OCRLanguage.TraditionalChinese, (ChineseText, 0.9f)),
                Candidate(OCRLanguage.Korean, (Hangul, 0.5f))
            ],
            OCRLanguage.TraditionalChinese);

        Assert.Equal(OCRLanguage.Korean, decision.Language);
        Assert.True(decision.Confident);
    }

    [Fact]
    public void Decide_IgnoresStrayKana_BelowEvidenceThreshold()
    {
        // The Chinese recogniser occasionally emits a single kana for a small glyph; one character is not evidence.
        var decision = OcrScriptDecisionPolicy.Decide(
            [
                Candidate(OCRLanguage.TraditionalChinese, (ChineseText, 0.85f)),
                Candidate(OCRLanguage.Japanese, (ChineseText + SingleKatakana, 0.4f))
            ],
            OCRLanguage.TraditionalChinese);

        Assert.Equal(OCRLanguage.TraditionalChinese, decision.Language);
    }

    [Fact]
    public void Decide_IgnoresKana_WhenTheJapaneseCandidateIsItselfLowQuality()
    {
        // Kana produced at throwaway confidence is hallucination, not evidence.
        var decision = OcrScriptDecisionPolicy.Decide(
            [
                Candidate(OCRLanguage.TraditionalChinese, (ChineseText, 0.9f)),
                Candidate(OCRLanguage.Japanese, (KanaText, 0.05f))
            ],
            OCRLanguage.TraditionalChinese);

        Assert.Equal(OCRLanguage.TraditionalChinese, decision.Language);
    }

    [Fact]
    public void Decide_FallsBackToScore_ForKanjiOnlyJapanese()
    {
        // No kana anywhere, so the character-set rules abstain and the better-fitting model wins on score.
        var decision = OcrScriptDecisionPolicy.Decide(
            [
                Candidate(OCRLanguage.TraditionalChinese, (Kanji, 0.55f)),
                Candidate(OCRLanguage.Japanese, (Kanji, 0.92f))
            ],
            OCRLanguage.TraditionalChinese);

        Assert.Equal(OCRLanguage.Japanese, decision.Language);
    }

    [Fact]
    public void Decide_DisqualifiesEnglish_WhenTheContentIsClearlyCjk()
    {
        // The English recogniser cannot emit CJK, so it returns short latin garbage at a high confidence its ~96
        // output classes make cheap. Without the disqualification it would win every CJK capture.
        var decision = OcrScriptDecisionPolicy.Decide(
            [
                Candidate(OCRLanguage.TraditionalChinese, (ChineseText, 0.6f)),
                Candidate(OCRLanguage.English, ("Rltt", 0.99f))
            ],
            OCRLanguage.TraditionalChinese);

        Assert.Equal(OCRLanguage.TraditionalChinese, decision.Language);
    }

    [Fact]
    public void Decide_PicksEnglish_ForLatinOnlyContent()
    {
        var decision = OcrScriptDecisionPolicy.Decide(
            [
                Candidate(OCRLanguage.TraditionalChinese, ("Hello world", 0.5f)),
                Candidate(OCRLanguage.English, ("Hello world", 0.9f))
            ],
            OCRLanguage.TraditionalChinese);

        Assert.Equal(OCRLanguage.English, decision.Language);
        Assert.True(decision.Confident);
    }

    [Fact]
    public void Decide_ReturnsCallerChineseVariant_ForChineseContent()
    {
        var decision = OcrScriptDecisionPolicy.Decide(
            [Candidate(OCRLanguage.SimplifiedChinese, (ChineseText, 0.8f))],
            OCRLanguage.SimplifiedChinese);

        Assert.Equal(OCRLanguage.SimplifiedChinese, decision.Language);
    }

    [Fact]
    public void Decide_FallsBackToChinese_WhenThereAreNoCandidates()
    {
        var decision = OcrScriptDecisionPolicy.Decide([], OCRLanguage.TraditionalChinese);

        Assert.Equal(OCRLanguage.TraditionalChinese, decision.Language);
        Assert.False(decision.Confident);
    }

    [Fact]
    public void Decide_FallsBackToChinese_WhenEveryCandidateScoresBelowThreshold()
    {
        var decision = OcrScriptDecisionPolicy.Decide(
            [
                Candidate(OCRLanguage.Japanese, (KanaText, 0.02f)),
                Candidate(OCRLanguage.Korean, (Hangul, 0.01f)),
                Candidate(OCRLanguage.English, ("x", 0.05f))
            ],
            OCRLanguage.TraditionalChinese);

        Assert.Equal(OCRLanguage.TraditionalChinese, decision.Language);
        Assert.False(decision.Confident);
    }

    [Fact]
    public void Decide_FallsBackToChinese_WhenNoCandidateRecognisedAnything()
    {
        var decision = OcrScriptDecisionPolicy.Decide(
            [
                Candidate(OCRLanguage.TraditionalChinese, ("", 0f)),
                Candidate(OCRLanguage.Japanese, ("", 0f))
            ],
            OCRLanguage.TraditionalChinese);

        Assert.Equal(OCRLanguage.TraditionalChinese, decision.Language);
        Assert.False(decision.Confident);
    }

    [Fact]
    public void Decide_PenalisesGarbageSamples_SoTheCleanModelWins()
    {
        // Same confidence, but one model's output is littered with characters that belong to no script.
        var decision = OcrScriptDecisionPolicy.Decide(
            [
                Candidate(OCRLanguage.TraditionalChinese, (ChineseText, 0.7f)),
                Candidate(OCRLanguage.Japanese, (Kanji + Garbage, 0.7f))
            ],
            OCRLanguage.TraditionalChinese);

        Assert.Equal(OCRLanguage.TraditionalChinese, decision.Language);
    }

    [Fact]
    public void Decide_AveragesAcrossSamples_SoOneGoodBoxDoesNotCarryAModel()
    {
        var decision = OcrScriptDecisionPolicy.Decide(
            [
                Candidate(OCRLanguage.TraditionalChinese, (ChineseText, 0.7f), (ChineseText, 0.7f)),
                Candidate(OCRLanguage.Japanese, (Kanji, 0.99f), ("", 0f))
            ],
            OCRLanguage.TraditionalChinese);

        Assert.Equal(OCRLanguage.TraditionalChinese, decision.Language);
    }

    private static OcrScriptCandidate Candidate(
        OCRLanguage language,
        params (string Text, float Confidence)[] samples)
    {
        var list = new List<OcrScriptSample>(samples.Length);
        foreach (var (text, confidence) in samples)
        {
            list.Add(new OcrScriptSample(text, confidence));
        }

        return new OcrScriptCandidate(language, list);
    }
}
