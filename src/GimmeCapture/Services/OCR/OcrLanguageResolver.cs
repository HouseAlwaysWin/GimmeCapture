using System;
using System.Collections.Generic;
using GimmeCapture.Models;

namespace GimmeCapture.Services.OCR;

/// <summary>
/// Turns <see cref="OCRLanguage.Auto"/> into a concrete language, and names the candidate set the cross-model probe
/// is allowed to choose from.
///
/// Auto used to have no mapping at all, so it fell through <c>AIPathService.GetOCRPaths</c>'s default arm to the
/// simplified-Chinese model — silently making every Auto capture a Chinese-only capture. Every path that resolves a
/// language for model loading goes through here so that cannot recur.
/// </summary>
public static class OcrLanguageResolver
{
    /// <summary>
    /// Which Chinese recogniser Auto means. Traditional and simplified are indistinguishable from the recognised
    /// text (each model only ever emits its own variant), so this is a preference rather than something detectable;
    /// it also decides which model the probe carries as its single Chinese candidate.
    /// </summary>
    public const OCRLanguage PreferredChineseVariant = OCRLanguage.TraditionalChinese;

    /// <summary>The concrete language to load for a (possibly Auto) setting, before any probing has run.</summary>
    public static OCRLanguage Resolve(OCRLanguage language) =>
        language == OCRLanguage.Auto ? PreferredChineseVariant : language;

    /// <summary>
    /// Languages the probe distinguishes between, in probe order. One Chinese entry only — see
    /// <see cref="PreferredChineseVariant"/>. Every other entry is separated from the rest by a character set no
    /// other model can emit (kana, hangul) or by being latin-only.
    /// </summary>
    public static IReadOnlyList<OCRLanguage> ProbeCandidates { get; } =
    [
        PreferredChineseVariant,
        OCRLanguage.Japanese,
        OCRLanguage.Korean,
        OCRLanguage.English
    ];

    /// <summary>Every language whose model files the OCR module installs, so Auto can always probe the full set.</summary>
    public static IReadOnlyList<OCRLanguage> InstallableLanguages { get; } =
    [
        OCRLanguage.TraditionalChinese,
        OCRLanguage.SimplifiedChinese,
        OCRLanguage.Japanese,
        OCRLanguage.Korean,
        OCRLanguage.English
    ];

    /// <summary>
    /// Whether every one of <paramref name="languages"/> has its models on disk. Lives next to the lists it walks so
    /// the installer, the module badge, and the probe all answer "are these installed?" the same way.
    /// </summary>
    public static bool AllReady(IReadOnlyList<OCRLanguage> languages, Func<OCRLanguage, bool> isReady)
    {
        ArgumentNullException.ThrowIfNull(languages);
        ArgumentNullException.ThrowIfNull(isReady);

        foreach (var language in languages)
        {
            if (!isReady(language)) return false;
        }

        return true;
    }

    /// <summary>The subset of <paramref name="languages"/> that is installed, preserving order.</summary>
    public static List<OCRLanguage> WhereReady(IReadOnlyList<OCRLanguage> languages, Func<OCRLanguage, bool> isReady)
    {
        ArgumentNullException.ThrowIfNull(languages);
        ArgumentNullException.ThrowIfNull(isReady);

        var installed = new List<OCRLanguage>(languages.Count);
        foreach (var language in languages)
        {
            if (isReady(language))
            {
                installed.Add(language);
            }
        }

        return installed;
    }
}
