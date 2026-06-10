using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using Avalonia;
using GimmeCapture.Models; // This using statement is already present, indicating the target namespace
using GimmeCapture.Services.Core.Interfaces;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.OCR;
using GimmeCapture.Services.Translation;
using SkiaSharp;
using SKRectI = SkiaSharp.SKRectI;

namespace GimmeCapture.Services.Translation;

public class TranslationService : IDisposable
{
    private readonly AppSettingsService _settingsService;
    private readonly OcrRuntimeService _ocrRuntimeService;
    private readonly IEnumerable<ITranslationEngine> _translationEngines;
    private readonly AIResourceService _aiResourceService;
    private IOCREngine? _ocrEngine;
    private bool _keepOcrWarm;

    private AppSettings _settings => _settingsService.Settings;

    public TranslationService(
        AIResourceService aiResourceService,
        AppSettingsService settingsService,
        OcrRuntimeService ocrRuntimeService)
    {
        _aiResourceService = aiResourceService ?? throw new ArgumentNullException(nameof(aiResourceService));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _ocrRuntimeService = ocrRuntimeService ?? throw new ArgumentNullException(nameof(ocrRuntimeService));
        
        // Manual DI for now as the app doesn't use a container in constructor injection here
        var translationCache = new InMemoryTranslationCache();

        _translationEngines = new List<ITranslationEngine>
        {
            new LlamaSharpTranslationEngine(_aiResourceService, settingsService, translationCache)
        };
    }

    internal bool IsOcrLoaded => _ocrRuntimeService.IsLoaded;
    internal OCRLanguage? LoadedOcrLanguage => _ocrRuntimeService.LoadedLanguage;
    internal bool IsLlamaLoaded => GetLlamaEngine()?.IsModelLoaded == true;

    public async Task WarmUpAsync(CancellationToken ct = default)
    {
        var warmupSourceLanguage = _settings.SourceLanguage == OCRLanguage.Auto
            ? OCRLanguage.TraditionalChinese
            : _settings.SourceLanguage;

        TranslationMemoryDiagnostics.Log(
            "translation-warmup-before",
            ocrLoaded: IsOcrLoaded,
            ocrLanguage: LoadedOcrLanguage?.ToString(),
            llamaLoaded: IsLlamaLoaded);

        try
        {
            bool ocrReady = await _aiResourceService.EnsureOCRAsync(warmupSourceLanguage, ct);
            if (ocrReady)
            {
                await GetOrCreateOcrEngine().EnsureLoadedAsync(warmupSourceLanguage, ct);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TranslationService] Warm-up OCR failed: {ex.Message}");
        }

        TranslationMemoryDiagnostics.Log(
            "translation-warmup-after",
            ocrLoaded: IsOcrLoaded,
            ocrLanguage: LoadedOcrLanguage?.ToString(),
            llamaLoaded: IsLlamaLoaded);
    }

    public Task<ResourceReadyResult> CheckEngineReadyAsync()
    {
        var engineType = _settings.SelectedTranslationEngine;
        if (engineType == TranslationEngine.LlamaSharp)
        {
            if (!_aiResourceService.IsLlamaModelReady())
            {
                return Task.FromResult(ResourceReadyResult.NotReady("StatusLlamaModelNotReady"));
            }
        }

        return Task.FromResult(ResourceReadyResult.Ready());
    }

    public async Task<(List<TranslatedBlock> Blocks, string ErrorKey)> AnalyzeAndTranslateAsync(SKBitmap bitmap, double scale = 1.0, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            TranslationMemoryDiagnostics.Log(
                "translation-analyze-begin",
                ocrLoaded: IsOcrLoaded,
                ocrLanguage: LoadedOcrLanguage?.ToString(),
                llamaLoaded: IsLlamaLoaded,
                bitmap: bitmap);

            var ocrLang = _settings.SourceLanguage;
            Console.WriteLine($"[TranslationService] SourceLanguage={ocrLang}, TargetLanguage={_settings.TargetLanguage}");
            if (ocrLang == OCRLanguage.Auto)
            {
                ocrLang = await DetectScriptLanguageAsync(bitmap, ct);
                Console.WriteLine($"[TranslationService] Auto-detected source: {ocrLang}");
            }

            var ocrEngine = GetOrCreateOcrEngine();
            await ocrEngine.EnsureLoadedAsync(ocrLang, ct);
            TranslationMemoryDiagnostics.Log(
                "translation-ocr-ready",
                ocrLoaded: IsOcrLoaded,
                ocrLanguage: LoadedOcrLanguage?.ToString(),
                llamaLoaded: IsLlamaLoaded,
                bitmap: bitmap);
            var boxes = ocrEngine.DetectText(bitmap);
            TranslationMemoryDiagnostics.Log(
                "translation-ocr-detect-complete",
                ocrLoaded: IsOcrLoaded,
                ocrLanguage: LoadedOcrLanguage?.ToString(),
                llamaLoaded: IsLlamaLoaded,
                selectionCount: boxes.Count,
                bitmap: bitmap);

            var recognizedBlocks = new List<(SKRectI Box, string Text, float Confidence)>();
            foreach (var box in boxes)
            {
                ct.ThrowIfCancellationRequested();
                var (text, confidence) = ocrEngine.RecognizeText(bitmap, box, ct);
                string cleanedText = SanitizeRecognizedOcrText(text);
                if (IsUsefulOcrText(cleanedText, confidence))
                {
                    recognizedBlocks.Add((box, cleanedText, confidence));
                }
            }

            if (recognizedBlocks.Count == 0) return (new List<TranslatedBlock>(), "StatusTranslateNoText");

            recognizedBlocks.Sort(static (a, b) =>
            {
                int topCompare = (a.Box.Top / 16).CompareTo(b.Box.Top / 16);
                return topCompare != 0 ? topCompare : a.Box.Left.CompareTo(b.Box.Left);
            });

            string mergedText = BuildMergedText(recognizedBlocks);
            var unionBox = GetUnionBox(recognizedBlocks);

            TranslationMemoryDiagnostics.Log(
                "translation-llama-before",
                ocrLoaded: IsOcrLoaded,
                ocrLanguage: LoadedOcrLanguage?.ToString(),
                llamaLoaded: IsLlamaLoaded,
                selectionCount: recognizedBlocks.Count,
                bitmap: bitmap);
            var translated = SanitizeTranslationCandidate(
                mergedText,
                await TranslateAsync(mergedText, ocrLang, ct),
                _settings.TargetLanguage);
            string bestEffortTranslation = TryPromoteBestEffortTranslation(mergedText, translated, _settings.TargetLanguage);
            TranslationMemoryDiagnostics.Log(
                "translation-llama-after",
                ocrLoaded: IsOcrLoaded,
                ocrLanguage: LoadedOcrLanguage?.ToString(),
                llamaLoaded: IsLlamaLoaded,
                selectionCount: recognizedBlocks.Count,
                bitmap: bitmap);

            if (string.IsNullOrEmpty(translated))
            {
                System.Diagnostics.Debug.WriteLine($"[TranslationService] FAILURE: Engine returned empty result for OCR: '{mergedText}'");
                var perBlockFallback = await TranslateRecognizedBlocksSeparatelyAsync(
                    recognizedBlocks,
                    ocrLang,
                    _settings.TargetLanguage,
                    scale,
                    includeOriginalOnlyBlocksOnFailure: false,
                    ct);
                if (perBlockFallback.Count > 0)
                {
                    return (perBlockFallback, string.Empty);
                }

                return (CreateOriginalOnlyFallbackBlocks(recognizedBlocks, scale), "StatusTranslateFailedEngine");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[TranslationService] Raw OCR: {mergedText}");
                System.Diagnostics.Debug.WriteLine($"[TranslationService] Result: {translated}");
            }

            double inferredFontSize = 12.0;
            if (recognizedBlocks.Count > 0)
            {
                double totalHeight = 0;
                foreach (var block in recognizedBlocks)
                {
                    totalHeight += block.Box.Height;
                }

                double averagePixelHeight = totalHeight / recognizedBlocks.Count;
                inferredFontSize = (averagePixelHeight / scale) * 0.85;
                inferredFontSize = Math.Clamp(inferredFontSize, 8.0, 72.0);
            }

            bool acceptable = IsTranslationAcceptable(mergedText, translated, _settings.TargetLanguage);
            if (!acceptable)
            {
                string retriedTranslation = SanitizeTranslationCandidate(
                    mergedText,
                    await ForceTranslateAsync(mergedText, ocrLang, _settings.TargetLanguage, ct),
                    _settings.TargetLanguage);
                string retriedBestEffort = TryPromoteBestEffortTranslation(mergedText, retriedTranslation, _settings.TargetLanguage);
                if (retriedBestEffort.Length > bestEffortTranslation.Length)
                {
                    bestEffortTranslation = retriedBestEffort;
                }

                translated = retriedTranslation;
                acceptable = IsTranslationAcceptable(mergedText, translated, _settings.TargetLanguage);
            }

            List<TranslatedBlock>? perBlockTranslation = null;
            if (!acceptable)
            {
                perBlockTranslation = await TranslateRecognizedBlocksSeparatelyAsync(
                    recognizedBlocks,
                    ocrLang,
                    _settings.TargetLanguage,
                    scale,
                    includeOriginalOnlyBlocksOnFailure: false,
                    ct);
            }

            var result = new List<TranslatedBlock>();

            var logicalBounds = new Rect(
                unionBox.Left / scale,
                unionBox.Top / scale,
                unionBox.Width / scale,
                unionBox.Height / scale);

            result.Add(new TranslatedBlock
            {
                OriginalText = mergedText,
                TranslatedText = acceptable ? translated : bestEffortTranslation,
                Bounds = logicalBounds,
                InferredFontSize = inferredFontSize,
                DisplayFontSize = inferredFontSize
            });

            if (acceptable)
            {
                return (result, string.Empty);
            }

            if (perBlockTranslation is { Count: > 0 })
            {
                System.Diagnostics.Debug.WriteLine("[TranslationService] Falling back to per-block translation.");
                return (perBlockTranslation, string.Empty);
            }

            if (!string.IsNullOrWhiteSpace(bestEffortTranslation))
            {
                System.Diagnostics.Debug.WriteLine("[TranslationService] Using best-effort translation fallback.");
                return (result, string.Empty);
            }

            string originalFallbackText = BuildFailureFallbackText(mergedText, _settings.TargetLanguage);
            if (!string.IsNullOrWhiteSpace(originalFallbackText))
            {
                result[0].TranslatedText = string.Empty;
                result[0].OriginalText = originalFallbackText;
                System.Diagnostics.Debug.WriteLine("[TranslationService] Falling back to visible sanitized OCR text.");

                return (result, "StatusTranslateFailedAcceptable");
            }

            System.Diagnostics.Debug.WriteLine("[TranslationService] Final translation effort failed or was unacceptable. Returning OCR-only block.");
            return (result, "StatusTranslateFailedAcceptable");
        }
        finally
        {
            if (!_keepOcrWarm)
            {
                ReleaseOcrResources();
            }

            TranslationMemoryDiagnostics.Log(
                "translation-analyze-end",
                ocrLoaded: IsOcrLoaded,
                ocrLanguage: LoadedOcrLanguage?.ToString(),
                llamaLoaded: IsLlamaLoaded);
        }
    }

    public async Task<string> TranslatePlainTextAsync(string text, OCRLanguage sourceLang, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        string translated = SanitizeTranslationCandidate(text, await TranslateAsync(text, sourceLang, ct), _settings.TargetLanguage);
        string bestEffortTranslation = TryPromoteBestEffortTranslation(text, translated, _settings.TargetLanguage);
        if (IsTranslationAcceptable(text, translated, _settings.TargetLanguage))
        {
            return translated;
        }

        // Retry once with a likely source guess when Auto/ambiguous source may hurt engine routing.
        var guessedSource = GuessSourceLanguageFromText(text);
        if (guessedSource != sourceLang)
        {
            string retried = SanitizeTranslationCandidate(text, await TranslateAsync(text, guessedSource, ct), _settings.TargetLanguage);
            string retriedBestEffort = TryPromoteBestEffortTranslation(text, retried, _settings.TargetLanguage);
            if (retriedBestEffort.Length > bestEffortTranslation.Length)
            {
                bestEffortTranslation = retriedBestEffort;
            }

            if (IsTranslationAcceptable(text, retried, _settings.TargetLanguage))
            {
                return retried;
            }
        }

        // Fallback: try another engine if selected one returns unacceptable output.
        foreach (var engine in _translationEngines)
        {
            if (engine.EngineType == _settings.SelectedTranslationEngine)
            {
                continue;
            }

            string fallback = SanitizeTranslationCandidate(
                text,
                await engine.TranslateAsync(text, guessedSource, _settings.TargetLanguage, ct),
                _settings.TargetLanguage);
            string fallbackBestEffort = TryPromoteBestEffortTranslation(text, fallback, _settings.TargetLanguage);
            if (fallbackBestEffort.Length > bestEffortTranslation.Length)
            {
                bestEffortTranslation = fallbackBestEffort;
            }

            if (IsTranslationAcceptable(text, fallback, _settings.TargetLanguage))
            {
                return fallback;
            }
        }

        return bestEffortTranslation;
    }

    private async Task<List<TranslatedBlock>> TranslateRecognizedBlocksSeparatelyAsync(
        List<(SKRectI Box, string Text, float Confidence)> recognizedBlocks,
        OCRLanguage sourceLanguage,
        TranslationLanguage targetLanguage,
        double scale,
        bool includeOriginalOnlyBlocksOnFailure,
        CancellationToken ct)
    {
        var results = new List<TranslatedBlock>(recognizedBlocks.Count);

        foreach (var block in recognizedBlocks)
        {
            ct.ThrowIfCancellationRequested();

            string originalText = block.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(originalText))
            {
                continue;
            }

            string translatedText = await TranslatePlainTextAsync(originalText, sourceLanguage, ct);
            if (string.IsNullOrWhiteSpace(translatedText))
            {
                if (includeOriginalOnlyBlocksOnFailure)
                {
                    translatedText = string.Empty;
                }
            }

            if (string.IsNullOrWhiteSpace(translatedText) && !includeOriginalOnlyBlocksOnFailure)
            {
                continue;
            }

            double inferredFontSize = Math.Clamp((block.Box.Height / scale) * 0.85, 8.0, 72.0);
            results.Add(new TranslatedBlock
            {
                OriginalText = originalText,
                TranslatedText = translatedText,
                Bounds = new Rect(
                    block.Box.Left / scale,
                    block.Box.Top / scale,
                    block.Box.Width / scale,
                    block.Box.Height / scale),
                InferredFontSize = inferredFontSize,
                DisplayFontSize = inferredFontSize
            });
        }

        return results;
    }

    private static List<TranslatedBlock> CreateOriginalOnlyFallbackBlocks(
        List<(SKRectI Box, string Text, float Confidence)> recognizedBlocks,
        double scale)
    {
        var results = new List<TranslatedBlock>(recognizedBlocks.Count);
        foreach (var block in recognizedBlocks)
        {
            string originalText = block.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(originalText))
            {
                continue;
            }

            double inferredFontSize = Math.Clamp((block.Box.Height / scale) * 0.85, 8.0, 72.0);
            results.Add(new TranslatedBlock
            {
                OriginalText = originalText,
                TranslatedText = string.Empty,
                Bounds = new Rect(
                    block.Box.Left / scale,
                    block.Box.Top / scale,
                    block.Box.Width / scale,
                    block.Box.Height / scale),
                InferredFontSize = inferredFontSize,
                DisplayFontSize = inferredFontSize
            });
        }

        return results;
    }

    private async Task<OCRLanguage> DetectScriptLanguageAsync(SKBitmap bitmap, CancellationToken ct)
    {
        var ocrEngine = GetOrCreateOcrEngine();
        await ocrEngine.EnsureLoadedAsync(OCRLanguage.TraditionalChinese, ct);
        var boxes = ocrEngine.DetectText(bitmap);
        int sampleCount = Math.Min(8, boxes.Count);
        int kanaCount = 0;
        int meaningfulCount = 0;

        for (int i = 0; i < sampleCount; i++)
        {
            var box = boxes[i];
            var (text, _) = ocrEngine.RecognizeText(bitmap, box, ct);
            text = SanitizeRecognizedOcrText(text);
            kanaCount += CountJapaneseKana(text);
            meaningfulCount += CountMeaningfulChars(text);
        }

        // The Chinese recognizer can occasionally emit one stray kana for small
        // Traditional Chinese glyphs. Require strong aggregate evidence before
        // swapping to the Japanese model, otherwise the second OCR pass degrades.
        return kanaCount >= 3 && meaningfulCount > 0 && (kanaCount * 5) >= meaningfulCount
            ? OCRLanguage.Japanese
            : OCRLanguage.TraditionalChinese;
    }

    private static int CountJapaneseKana(string text)
    {
        int count = 0;
        foreach (char ch in text)
        {
            if (ch >= '\u3040' && ch <= '\u30FF')
            {
                count++;
            }
        }

        return count;
    }

    public void ReleaseOcrResources()
    {
        _ocrEngine?.Dispose();
        _ocrEngine = null;
    }

    public void SetOcrWarmHold(bool hold)
    {
        _keepOcrWarm = hold;
        if (!hold)
        {
            ReleaseOcrResources();
        }
    }

    internal void ReleaseHeavyResources(bool trimProcessWorkingSet = false)
    {
        SetOcrWarmHold(false);
        ReleaseOcrResources();
        GetLlamaEngine()?.ReleaseModel();

        if (trimProcessWorkingSet)
        {
            _ = ProcessMemoryTrimService.RequestIdleTrimAsync("translation-resources-released");
        }
    }

    private async Task<string> TranslateAsync(string text, OCRLanguage sourceLang, CancellationToken ct)
    {
        var engine = _translationEngines.AsValueEnumerable().FirstOrDefault(e => e.EngineType == _settings.SelectedTranslationEngine);
        if (engine == null) 
        {
            System.Diagnostics.Debug.WriteLine($"[TranslationService] NO ENGINE FOUND for {_settings.SelectedTranslationEngine}");
            return string.Empty; 
        }
        Console.WriteLine($"[TranslationService] Using engine: {engine.EngineType} for {sourceLang} -> {_settings.TargetLanguage}");
        var result = await engine.TranslateAsync(text, sourceLang, _settings.TargetLanguage, ct);
        if (string.IsNullOrEmpty(result))
        {
             System.Diagnostics.Debug.WriteLine($"[TranslationService] Engine {engine.EngineType} failed to translate.");
        }
        return result ?? string.Empty;
    }

    private async Task<string> ForceTranslateAsync(string text, OCRLanguage sourceLang, TranslationLanguage targetLang, CancellationToken ct)
    {
        // Force the use of LlamaSharp for retry with a stricter target language prompt
        var llm = _translationEngines.AsValueEnumerable().OfType<LlamaSharpTranslationEngine>().FirstOrDefault();
        if (llm != null) return await llm.TranslateAsync(text, sourceLang, targetLang, ct);
        return string.Empty;
    }

    private bool IsUsefulOcrText(string text, float confidence)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (confidence < 0.10f) return false;

        ReadOnlySpan<char> trimmed = text.AsSpan().Trim();
        if (trimmed.IsEmpty) return false;

        bool sawUseful = false;
        bool allPlaceholder = true;
        int usefulCount = 0;
        int suspiciousCount = 0;
        foreach (char ch in trimmed)
        {
            if (IsInvalidOcrCharacter(ch))
            {
                return false;
            }

            bool isPlaceholder = ch is '?' or '.' or '-' or '_' or '*';
            allPlaceholder &= isPlaceholder;

            if (char.IsLetterOrDigit(ch)
                || (ch >= 0x4E00 && ch <= 0x9FFF)
                || (ch >= 0x3040 && ch <= 0x309F)
                || (ch >= 0x30A0 && ch <= 0x30FF)
                || (ch >= 0xAC00 && ch <= 0xD7AF))
            {
                sawUseful = true;
                usefulCount++;
            }
            else if (!char.IsWhiteSpace(ch) && !char.IsPunctuation(ch))
            {
                suspiciousCount++;
            }
        }

        if (!sawUseful || allPlaceholder)
        {
            return false;
        }

        if (suspiciousCount > usefulCount)
        {
            return false;
        }

        if (usefulCount <= 2 && confidence < 0.30f)
        {
            return false;
        }

        return true;
    }

    private bool IsTranslationAcceptable(string original, string translated, TranslationLanguage target)
    {
        if (string.IsNullOrWhiteSpace(translated))
        {
            return false;
        }

        string trimmed = translated.Trim();
        if (IsObviousPromptOrControlText(trimmed) || LooksLikeTranslationMetaOrAssistantReply(trimmed, target))
        {
            return false;
        }

        if (LooksLikeRunawayRepetition(original, trimmed))
        {
            return false;
        }

        if (HasUnexpectedTranslationLineCount(original, trimmed))
        {
            return false;
        }

        string normalizedOriginal = NormalizeTranslationComparisonText(original);
        string normalizedTranslated = NormalizeTranslationComparisonText(trimmed);
        if (normalizedTranslated.Length >= 4
            && string.Equals(normalizedOriginal, normalizedTranslated, StringComparison.Ordinal))
        {
            return false;
        }

        return target switch
        {
            TranslationLanguage.English => ContainsLatinLetter(trimmed),
            TranslationLanguage.Korean => ContainsHangul(trimmed),
            TranslationLanguage.Japanese => IsJapaneseTranslationAcceptable(original, trimmed, normalizedOriginal, normalizedTranslated),
            TranslationLanguage.TraditionalChinese or TranslationLanguage.SimplifiedChinese => IsChineseTranslationAcceptable(original, trimmed, normalizedOriginal, normalizedTranslated),
            _ => true
        };
    }

    private static string SanitizeTranslationCandidate(string original, string translated, TranslationLanguage target)
    {
        if (string.IsNullOrWhiteSpace(translated))
        {
            return string.Empty;
        }

        string normalizedOriginal = NormalizeTranslationComparisonText(original);
        string[] rawLines = translated.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<string>();
        foreach (var rawLine in rawLines)
        {
            string line = SanitizeTranslationCandidateLine(original, rawLine, target);
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            string normalizedLine = NormalizeTranslationComparisonText(line);
            if (normalizedLine.Length == 0 || !seen.Add(normalizedLine))
            {
                continue;
            }

            candidates.Add(line);
        }

        if (candidates.Count == 0)
        {
            return string.Empty;
        }

        if (candidates.Count > 1)
        {
            candidates.RemoveAll(line =>
            {
                string normalized = NormalizeTranslationComparisonText(line);
                return normalized.Length >= 4
                    && string.Equals(normalized, normalizedOriginal, StringComparison.Ordinal);
            });
        }

        if (candidates.Count == 0)
        {
            return string.Empty;
        }

        var targetPreferred = SelectTargetPreferredLines(candidates, original, target);
        if (targetPreferred.Count > 0)
        {
            candidates = targetPreferred;
        }
        else
        {
            var strippedCandidates = StripSourceLanguageArtifacts(candidates, original, target);
            if (strippedCandidates.Count > 0)
            {
                var strippedPreferred = SelectTargetPreferredLines(strippedCandidates, original, target);
                candidates = strippedPreferred.Count > 0 ? strippedPreferred : strippedCandidates;
            }
        }

        return string.Join(Environment.NewLine, candidates).Trim();
    }

    private static string SanitizeTranslationCandidateLine(string original, string line, TranslationLanguage target)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return string.Empty;
        }

        string cleaned = CollapseRepeatedTranslationArtifacts(original, line.Trim());
        cleaned = StripRomanizedGlosses(cleaned, target);
        cleaned = TrimTranslationArtifacts(cleaned);
        if (IsObviousPromptOrControlText(cleaned) || LooksLikeTranslationMetaOrAssistantReply(cleaned, target))
        {
            return string.Empty;
        }

        return cleaned;
    }

    private static List<string> SelectTargetPreferredLines(List<string> lines, string original, TranslationLanguage target)
    {
        static List<string> KeepWhere(IEnumerable<string> source, Func<string, bool> predicate)
        {
            var kept = source.AsValueEnumerable().Where(predicate).ToList();
            return kept;
        }

        return target switch
        {
            TranslationLanguage.English => KeepWhere(lines, ContainsLatinLetter),
            TranslationLanguage.Korean => KeepWhere(lines, ContainsHangul),
            TranslationLanguage.Japanese => KeepWhere(
                lines,
                line => (ContainsJapaneseKana(line) || ContainsCjk(line)) && !LooksLikeMostlyLatinInstruction(line)),
            TranslationLanguage.TraditionalChinese or TranslationLanguage.SimplifiedChinese when ContainsJapaneseKana(original) =>
                KeepWhere(lines, line => ContainsCjk(line) && !ContainsJapaneseKana(line)),
            TranslationLanguage.TraditionalChinese or TranslationLanguage.SimplifiedChinese when ContainsHangul(original) =>
                KeepWhere(lines, line => ContainsCjk(line) && !ContainsHangul(line)),
            TranslationLanguage.TraditionalChinese or TranslationLanguage.SimplifiedChinese when ContainsLatinLetter(original) =>
                KeepWhere(lines, ContainsCjk),
            _ => new List<string>()
        };
    }

    private static List<string> StripSourceLanguageArtifacts(List<string> lines, string original, TranslationLanguage target)
    {
        bool stripJapanese = target is TranslationLanguage.TraditionalChinese or TranslationLanguage.SimplifiedChinese
            && ContainsJapaneseKana(original);
        bool stripKorean = target is TranslationLanguage.TraditionalChinese or TranslationLanguage.SimplifiedChinese
            && ContainsHangul(original);

        if (!stripJapanese && !stripKorean)
        {
            return new List<string>();
        }

        var stripped = new List<string>(lines.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines)
        {
            string cleaned = StripSourceLanguageArtifactsFromLine(line, stripJapanese, stripKorean);
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                continue;
            }

            string normalized = NormalizeTranslationComparisonText(cleaned);
            if (normalized.Length == 0 || !seen.Add(normalized))
            {
                continue;
            }

            stripped.Add(cleaned);
        }

        return stripped;
    }

    private static string StripSourceLanguageArtifactsFromLine(string line, bool stripJapanese, bool stripKorean)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return string.Empty;
        }

        string cleaned = line.Trim();
        cleaned = RemoveBracketedSourceSegmentsSafe(cleaned, stripJapanese, stripKorean);
        cleaned = StripTrailingSourceSegment(cleaned, stripJapanese, stripKorean);
        return TrimSourceArtifacts(cleaned);
    }

    private static string StripRomanizedGlosses(string line, TranslationLanguage target)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return string.Empty;
        }

        if (!ContainsCjk(line) && !ContainsHangul(line))
        {
            return line.Trim();
        }

        string cleaned = line.Trim();
        cleaned = RemoveBracketedLatinGlossSegments(cleaned);
        cleaned = StripTrailingLatinGlossSegment(cleaned);
        return TrimTranslationArtifacts(cleaned);
    }

    private static string RemoveBracketedLatinGlossSegments(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        ReadOnlySpan<(char Open, char Close)> brackets =
        [
            ('(', ')'),
            ('\uFF08', '\uFF09'),
            ('[', ']'),
            ('\uFF3B', '\uFF3D'),
            ('{', '}'),
            ('\uFF5B', '\uFF5D')
        ];

        string result = value;
        foreach (var (open, close) in brackets)
        {
            int searchStart = 0;
            while (searchStart < result.Length)
            {
                int start = result.IndexOf(open, searchStart);
                if (start < 0)
                {
                    break;
                }

                int end = result.IndexOf(close, start + 1);
                if (end < 0)
                {
                    break;
                }

                string inner = result.Substring(start + 1, end - start - 1);
                if (!ContainsLatinLetter(inner) || ContainsCjk(inner) || ContainsHangul(inner))
                {
                    searchStart = end + 1;
                    continue;
                }

                string left = result[..start].TrimEnd();
                string right = result[(end + 1)..].TrimStart();
                result = left.Length > 0 && right.Length > 0
                    ? $"{left} {right}"
                    : left + right;
                searchStart = 0;
            }
        }

        return result;
    }

    private static string StripTrailingLatinGlossSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string[] separators =
        [
            " - ",
            " / ",
            " | ",
            ":",
            "/",
            "|"
        ];

        foreach (var separator in separators)
        {
            int index = value.LastIndexOf(separator, StringComparison.Ordinal);
            if (index <= 0 || index + separator.Length >= value.Length)
            {
                continue;
            }

            string left = value[..index].TrimEnd();
            string right = value[(index + separator.Length)..].TrimStart();
            if (left.Length == 0 || right.Length == 0)
            {
                continue;
            }

            if ((ContainsCjk(left) || ContainsHangul(left))
                && ContainsLatinLetter(right)
                && !ContainsCjk(right)
                && !ContainsHangul(right))
            {
                return left;
            }
        }

        return value;
    }

    private static string TrimTranslationArtifacts(string value)
    {
        return value.Trim().Trim(
            ' ',
            '\t',
            '-',
            '/',
            '|',
            ':',
            ',',
            ';',
            '\u3001',
            '\u3002',
            '\uFF0C',
            '\uFF1B',
            '\uFF1A');
    }

    private static bool LooksLikeMostlyLatinInstruction(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string normalized = NormalizeTranslationComparisonText(value);
        if (normalized.Length == 0)
        {
            return false;
        }

        int latinCount = 0;
        foreach (char c in normalized)
        {
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))
            {
                latinCount++;
            }
        }

        return latinCount >= Math.Max(8, normalized.Length / 2);
    }

    private static bool HasUnexpectedTranslationLineCount(string original, string translated)
    {
        string[] originalLines = original.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string[] translatedLines = translated.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (originalLines.Length == 0 || translatedLines.Length == 0)
        {
            return false;
        }

        if (originalLines.Length == 1)
        {
            return translatedLines.Length > 1;
        }

        return translatedLines.Length > Math.Max(originalLines.Length + 1, originalLines.Length * 2);
    }

    private static bool LooksLikeTranslationMetaOrAssistantReply(string line, TranslationLanguage target)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        string trimmed = line.Trim();
        string normalized = NormalizeTranslationComparisonText(trimmed);
        string[] extraGenericMarkers =
        [
            "thetranslationis",
            "translationonly",
            "\u4ee5\u4e0b\u662f\u7ffb\u8b6f",
            "\u7ffb\u8b6f\u5982\u4e0b",
            "\u4ee5\u4e0b\u304c\u7ffb\u8a33",
            "\u7ffb\u8a33\u7d50\u679c"
        ];

        foreach (string marker in extraGenericMarkers)
        {
            if (normalized.Contains(NormalizeTranslationComparisonText(marker), StringComparison.Ordinal))
            {
                return true;
            }
        }

        string[] genericMarkers =
        [
            "translationresult",
            "translatedtext",
            "hereisthetranslation",
            "followingtranslation",
            "以下是翻譯",
            "以下是翻译",
            "翻譯如下",
            "翻译如下",
            "日本語訳",
            "翻訳結果",
            "以下の通り",
            "以下は",
            "以下を",
            "説明",
            "解説",
            "summary",
            "note"
        ];

        foreach (string marker in genericMarkers)
        {
            if (normalized.Contains(NormalizeTranslationComparisonText(marker), StringComparison.Ordinal))
            {
                return true;
            }
        }

        if (target != TranslationLanguage.Japanese)
        {
            return false;
        }

        string[] explicitJapaneseAssistantMarkers =
        [
            "\u627f\u77e5\u3044\u305f\u3057\u307e\u3057\u305f",
            "\u304b\u3057\u3053\u307e\u308a\u307e\u3057\u305f",
            "\u7533\u3057\u8a33\u3054\u3056\u3044\u307e\u305b\u3093",
            "\u78ba\u8a8d\u3055\u305b\u3066\u3044\u305f\u3060\u304d\u307e\u3059",
            "\u304a\u8abf\u3079\u3044\u305f\u3057\u307e\u3059",
            "\u304a\u77e5\u3089\u305b\u304f\u3060\u3055\u3044",
            "\u3082\u3057\u53ef\u80fd\u3067\u3042\u308c\u3070",
            "\u304a\u624b\u6570\u3067\u3059\u304c",
            "\u304a\u554f\u3044\u5408\u308f\u305b",
            "\u3054\u9023\u7d61\u304f\u3060\u3055\u3044",
            "\u3054\u78ba\u8a8d\u304f\u3060\u3055\u3044",
            "\u3044\u305f\u3060\u3051\u307e\u3059\u304b"
        ];

        foreach (string marker in explicitJapaneseAssistantMarkers)
        {
            if (normalized.Contains(NormalizeTranslationComparisonText(marker), StringComparison.Ordinal))
            {
                return true;
            }
        }

        string[] japaneseAssistantMarkers =
        [
            "承知いたしました",
            "申し訳ございません",
            "お調べいたします",
            "確認させていただきます",
            "もし可能であれば",
            "お客様",
            "ご提示",
            "させていただきます",
            "大変申し訳",
            "ご連絡ください"
        ];

        foreach (string marker in japaneseAssistantMarkers)
        {
            if (trimmed.Contains(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string RemoveBracketedSourceSegmentsSafe(string value, bool stripJapanese, bool stripKorean)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        ReadOnlySpan<(char Open, char Close)> brackets =
        [
            ('(', ')'),
            ('\uFF08', '\uFF09'),
            ('[', ']'),
            ('\uFF3B', '\uFF3D'),
            ('{', '}'),
            ('\uFF5B', '\uFF5D'),
            ('\u300C', '\u300D'),
            ('\u300E', '\u300F'),
            ('\u3010', '\u3011'),
            ('\u3008', '\u3009'),
            ('\u300A', '\u300B')
        ];

        string result = value;
        foreach (var (open, close) in brackets)
        {
            int searchStart = 0;
            while (searchStart < result.Length)
            {
                int start = result.IndexOf(open, searchStart);
                if (start < 0)
                {
                    break;
                }

                int end = result.IndexOf(close, start + 1);
                if (end < 0)
                {
                    break;
                }

                string inner = result.Substring(start + 1, end - start - 1);
                if (!ContainsSourceScript(inner, stripJapanese, stripKorean))
                {
                    searchStart = end + 1;
                    continue;
                }

                string left = result[..start].TrimEnd();
                string right = result[(end + 1)..].TrimStart();
                result = left.Length > 0 && right.Length > 0
                    ? $"{left} {right}"
                    : left + right;
                searchStart = 0;
            }
        }

        return result;
    }

    private static string RemoveBracketedSourceSegments(string value, bool stripJapanese, bool stripKorean)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        ReadOnlySpan<(char Open, char Close)> brackets =
        [
            ('(', ')'),
            ('（', '）'),
            ('[', ']'),
            ('［', '］'),
            ('{', '}'),
            ('｛', '｝'),
            ('「', '」'),
            ('『', '』'),
            ('【', '】'),
            ('〈', '〉'),
            ('《', '》')
        ];

        string result = value;
        foreach (var (open, close) in brackets)
        {
            int searchStart = 0;
            while (searchStart < result.Length)
            {
                int start = result.IndexOf(open, searchStart);
                if (start < 0)
                {
                    break;
                }

                int end = result.IndexOf(close, start + 1);
                if (end < 0)
                {
                    break;
                }

                string inner = result.Substring(start + 1, end - start - 1);
                if (!ContainsSourceScript(inner, stripJapanese, stripKorean))
                {
                    searchStart = end + 1;
                    continue;
                }

                string left = result[..start].TrimEnd();
                string right = result[(end + 1)..].TrimStart();
                result = left.Length > 0 && right.Length > 0
                    ? $"{left} {right}"
                    : left + right;
                searchStart = 0;
            }
        }

        return result;
    }

    private static string StripTrailingSourceSegment(string value, bool stripJapanese, bool stripKorean)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string[] separators =
        [
            " - ",
            " – ",
            " — ",
            " / ",
            " | ",
            "：",
            ":",
            "/",
            "|"
        ];

        foreach (var separator in separators)
        {
            int index = value.LastIndexOf(separator, StringComparison.Ordinal);
            if (index <= 0 || index + separator.Length >= value.Length)
            {
                continue;
            }

            string left = value[..index].TrimEnd();
            string right = value[(index + separator.Length)..].TrimStart();
            if (left.Length == 0 || right.Length == 0)
            {
                continue;
            }

            if (ContainsSourceScript(right, stripJapanese, stripKorean)
                && ContainsCjk(left)
                && !ContainsSourceScript(left, stripJapanese, stripKorean))
            {
                return left;
            }
        }

        return value;
    }

    private static string TrimSourceArtifacts(string value)
    {
        return value.Trim().Trim(
            ' ',
            '\t',
            '-',
            '–',
            '—',
            '/',
            '|',
            ':',
            '：',
            '・',
            '･',
            '、',
            '，',
            ',',
            ';',
            '；');
    }

    private static string CollapseRepeatedTranslationArtifacts(string original, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string trimmed = value.Trim();
        string normalizedOriginal = NormalizeTranslationComparisonText(original);
        string collapsed = TryCollapseRepeatedTokenCycle(TokenizeTranslationValue(trimmed), normalizedOriginal);
        return string.IsNullOrWhiteSpace(collapsed) ? trimmed : collapsed;
    }

    private static string BuildFailureFallbackText(string original, TranslationLanguage target)
    {
        if (string.IsNullOrWhiteSpace(original))
        {
            return string.Empty;
        }

        if (!ShouldUseOriginalTextFallback(original, target))
        {
            return string.Empty;
        }

        string[] lines = original.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<string>(lines.Length);

        foreach (string rawLine in lines)
        {
            string cleaned = SanitizeRecognizedOcrText(rawLine);
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                continue;
            }

            string normalized = NormalizeTranslationComparisonText(cleaned);
            if (normalized.Length == 0 || !seen.Add(normalized))
            {
                continue;
            }

            kept.Add(cleaned);
        }

        return string.Join(Environment.NewLine, kept).Trim();
    }

    private static string SanitizeRecognizedOcrText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string[] rawLines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<string>(rawLines.Length);

        foreach (string rawLine in rawLines)
        {
            var builder = new StringBuilder(rawLine.Length);
            bool lastWasSpace = false;

            foreach (char ch in rawLine)
            {
                if (IsInvalidOcrCharacter(ch))
                {
                    continue;
                }

                if (char.IsWhiteSpace(ch))
                {
                    if (!lastWasSpace)
                    {
                        builder.Append(' ');
                        lastWasSpace = true;
                    }

                    continue;
                }

                lastWasSpace = false;
                builder.Append(ch);
            }

            string cleaned = NormalizeTerminalOcrPunctuation(builder.ToString().Trim());
            if (string.IsNullOrWhiteSpace(cleaned) || IsLowSignalOcrLine(cleaned))
            {
                continue;
            }

            string normalized = NormalizeTranslationComparisonText(cleaned);
            if (normalized.Length == 0 || !seen.Add(normalized))
            {
                continue;
            }

            kept.Add(cleaned);
        }

        return string.Join(Environment.NewLine, kept).Trim();
    }

    private static string NormalizeTerminalOcrPunctuation(string text)
    {
        if (text.Length < 2)
        {
            return text;
        }

        char last = text[^1];
        char previous = text[^2];
        bool followsCjkText = (previous >= '\u4E00' && previous <= '\u9FFF')
            || (previous >= '\u3040' && previous <= '\u30FF');

        return followsCjkText && last is '\u00B7' or '\u2022' or '\u2027'
            ? text[..^1] + '\u3002'
            : text;
    }

    private static bool IsLowSignalOcrLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return true;
        }

        int usefulCount = 0;
        int suspiciousCount = 0;
        int punctuationCount = 0;

        foreach (char ch in line)
        {
            if (char.IsLetterOrDigit(ch)
                || (ch >= 0x4E00 && ch <= 0x9FFF)
                || (ch >= 0x3040 && ch <= 0x30FF)
                || (ch >= 0xAC00 && ch <= 0xD7AF))
            {
                usefulCount++;
                continue;
            }

            if (char.IsPunctuation(ch))
            {
                punctuationCount++;
                continue;
            }

            if (!char.IsWhiteSpace(ch))
            {
                suspiciousCount++;
            }
        }

        if (usefulCount == 0)
        {
            return true;
        }

        if (suspiciousCount > usefulCount)
        {
            return true;
        }

        return usefulCount <= 1 && punctuationCount + suspiciousCount >= 2;
    }

    private static bool IsInvalidOcrCharacter(char ch)
    {
        return ch == '\uFFFD'
            || char.IsControl(ch)
            || ch is '\u200B' or '\u200C' or '\u200D' or '\u2060' or '\uFEFF'
            || (ch >= '\uD800' && ch <= '\uDFFF')
            || (ch >= '\uE000' && ch <= '\uF8FF');
    }

    private static bool ShouldUseOriginalTextFallback(string original, TranslationLanguage target)
    {
        _ = target;
        return CountMeaningfulChars(original) > 0;
    }

    private static bool LooksLikeRunawayRepetition(string original, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string trimmed = value.Trim();
        if (trimmed.Length <= Math.Max(24, original.Length * 4))
        {
            return false;
        }

        string[] tokens = TokenizeTranslationValue(trimmed);
        if (tokens.Length < 6)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(TryCollapseRepeatedTokenCycle(tokens, NormalizeTranslationComparisonText(original))))
        {
            return true;
        }

        int uniqueCount = new HashSet<string>(tokens, StringComparer.OrdinalIgnoreCase).Count;
        return uniqueCount <= 3 && tokens.Length >= uniqueCount * 4;
    }

    private static string[] TokenizeTranslationValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        return Array.FindAll(
            System.Text.RegularExpressions.Regex.Split(value.Trim(), @"[\s,\u3001\uFF0C;\uFF1B/|]+"),
            static token => !string.IsNullOrWhiteSpace(token));
    }

    private static string TryCollapseRepeatedTokenCycle(string[] tokens, string normalizedOriginal)
    {
        if (tokens.Length < 4)
        {
            return string.Empty;
        }

        for (int cycleLength = 1; cycleLength <= Math.Min(4, tokens.Length / 2); cycleLength++)
        {
            if (!IsMostlyRepeatedTokenCycle(tokens, cycleLength))
            {
                continue;
            }

            var cycleTokens = new List<string>(cycleLength);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < cycleLength && i < tokens.Length; i++)
            {
                if (seen.Add(tokens[i]))
                {
                    cycleTokens.Add(tokens[i]);
                }
            }

            foreach (string token in cycleTokens)
            {
                if (!string.IsNullOrWhiteSpace(normalizedOriginal)
                    && string.Equals(NormalizeTranslationComparisonText(token), normalizedOriginal, StringComparison.Ordinal))
                {
                    return token.Trim();
                }
            }

            return string.Join("\u3001", cycleTokens).Trim();
        }

        return string.Empty;
    }

    private static bool IsMostlyRepeatedTokenCycle(string[] tokens, int cycleLength)
    {
        if (tokens.Length < (cycleLength * 2) + 1)
        {
            return false;
        }

        int comparisons = 0;
        int matches = 0;
        for (int i = cycleLength; i < tokens.Length; i++)
        {
            comparisons++;
            if (string.Equals(tokens[i], tokens[i % cycleLength], StringComparison.Ordinal))
            {
                matches++;
            }
        }

        return comparisons > 0 && matches * 5 >= comparisons * 4;
    }

    private static bool IsJapaneseTranslationAcceptable(
        string original,
        string translated,
        string normalizedOriginal,
        string normalizedTranslated)
    {
        if (LooksLikeMostlyLatinInstruction(translated))
        {
            return false;
        }

        if (ContainsJapaneseKana(translated))
        {
            return true;
        }

        if (string.Equals(normalizedOriginal, normalizedTranslated, StringComparison.Ordinal))
        {
            return false;
        }

        if (ContainsJapaneseKana(original))
        {
            return ContainsCjk(translated);
        }

        if (ContainsLatinLetter(original) || ContainsHangul(original))
        {
            return ContainsCjk(translated);
        }

        if (ContainsCjk(original))
        {
            if (!ContainsCjk(translated))
            {
                return false;
            }

            if (CountMeaningfulChars(original) > 4 && !ContainsJapaneseKana(translated))
            {
                return false;
            }

            return true;
        }

        return ContainsCjk(translated);
    }

    private static string TryPromoteBestEffortTranslation(string original, string translated, TranslationLanguage target)
    {
        if (string.IsNullOrWhiteSpace(translated))
        {
            return string.Empty;
        }

        string trimmed = translated.Trim();
        if (IsObviousPromptOrControlText(trimmed) || LooksLikeTranslationMetaOrAssistantReply(trimmed, target))
        {
            return string.Empty;
        }

        string normalizedOriginal = NormalizeTranslationComparisonText(original);
        string normalizedTranslated = NormalizeTranslationComparisonText(trimmed);
        if (normalizedTranslated.Length == 0
            || string.Equals(normalizedOriginal, normalizedTranslated, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        if (trimmed.Length > Math.Max(80, original.Length * 6))
        {
            return string.Empty;
        }

        if (LooksLikeRunawayRepetition(original, trimmed))
        {
            return string.Empty;
        }

        if (HasUnexpectedTranslationLineCount(original, trimmed))
        {
            return string.Empty;
        }

        return target switch
        {
            TranslationLanguage.TraditionalChinese or TranslationLanguage.SimplifiedChinese =>
                ContainsCjk(trimmed) ? trimmed : string.Empty,
            TranslationLanguage.Japanese =>
                IsJapaneseTranslationAcceptable(original, trimmed, normalizedOriginal, normalizedTranslated)
                    ? trimmed
                    : string.Empty,
            TranslationLanguage.Korean =>
                ContainsHangul(trimmed) ? trimmed : string.Empty,
            TranslationLanguage.English =>
                ContainsLatinLetter(trimmed) ? trimmed : string.Empty,
            _ => trimmed
        };
    }

    private static bool ContainsSourceScript(string text, bool stripJapanese, bool stripKorean)
    {
        return (stripJapanese && ContainsJapaneseKana(text))
            || (stripKorean && ContainsHangul(text));
    }

    private static bool IsObviousPromptOrControlText(string text)
    {
        string trimmed = text.Trim();
        string normalized = NormalizeTranslationComparisonText(trimmed);
        return trimmed.StartsWith("User:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Assistant:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Input:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Output:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("System:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("翻譯:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("翻訳:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("譯文:", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("Translate the following", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("Translate into", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("Translate to", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("target language is", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("translatethefollowing", StringComparison.Ordinal)
            || normalized.Contains("translateinto", StringComparison.Ordinal)
            || normalized.Contains("translateto", StringComparison.Ordinal)
            || normalized.Contains("thetargetlanguageis", StringComparison.Ordinal)
            || normalized.Contains("targetlanguageis", StringComparison.Ordinal)
            || trimmed.Contains("Output ONLY", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("No explanations", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("No markdown", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("No json", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("翻訳してください", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeTranslationComparisonText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (!char.IsWhiteSpace(c) && !char.IsPunctuation(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString().Trim();
    }

    private static bool IsChineseTranslationAcceptable(string original, string translated, string normalizedOriginal, string normalizedTranslated)
    {
        if (ContainsJapaneseKana(original))
        {
            return ContainsCjk(translated)
                && !ContainsJapaneseKana(translated)
                && !string.Equals(normalizedOriginal, normalizedTranslated, StringComparison.Ordinal);
        }

        if (ContainsHangul(original))
        {
            return ContainsCjk(translated)
                && !ContainsHangul(translated)
                && !string.Equals(normalizedOriginal, normalizedTranslated, StringComparison.Ordinal);
        }

        if (ContainsLatinLetter(original))
        {
            return ContainsCjk(translated);
        }

        return ContainsCjk(translated)
            || !string.Equals(normalizedOriginal, normalizedTranslated, StringComparison.Ordinal);
    }

    private static bool ContainsLatinLetter(string text) =>
        text.AsValueEnumerable().Any(static c => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'));

    private static bool ContainsHangul(string text) =>
        text.AsValueEnumerable().Any(static c => c >= '\uAC00' && c <= '\uD7AF');

    private static bool ContainsCjk(string text) =>
        text.AsValueEnumerable().Any(static c => c >= '\u4E00' && c <= '\u9FFF');

    private static int CountMeaningfulChars(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        int count = 0;
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c)
                || (c >= '\u4E00' && c <= '\u9FFF')
                || (c >= '\u3040' && c <= '\u30FF')
                || (c >= '\uAC00' && c <= '\uD7AF'))
            {
                count++;
            }
        }

        return count;
    }

    private string BuildTargetLanguageFallbackText(string text, TranslationLanguage target)
    {
        if (target == TranslationLanguage.TraditionalChinese) return text; // Already source-like
        return text; // Simple fallback
    }

    private static OCRLanguage GuessSourceLanguageFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return OCRLanguage.Auto;
        if (ContainsJapaneseKana(text)) return OCRLanguage.Japanese;
        if (text.AsValueEnumerable().Any(c => c >= '\uAC00' && c <= '\uD7AF')) return OCRLanguage.Korean;
        if (text.AsValueEnumerable().Any(c => c >= '\u4E00' && c <= '\u9FFF')) return OCRLanguage.TraditionalChinese;
        if (text.AsValueEnumerable().Any(c => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))) return OCRLanguage.English;
        return OCRLanguage.Auto;
    }

    private static bool ContainsTargetScript(string text, TranslationLanguage target)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        return target switch
        {
            TranslationLanguage.TraditionalChinese or TranslationLanguage.SimplifiedChinese =>
                text.AsValueEnumerable().Any(c => c >= '\u4E00' && c <= '\u9FFF'),
            TranslationLanguage.Japanese =>
                text.AsValueEnumerable().Any(c => (c >= '\u3040' && c <= '\u30FF') || (c >= '\u4E00' && c <= '\u9FFF')),
            TranslationLanguage.Korean =>
                text.AsValueEnumerable().Any(c => c >= '\uAC00' && c <= '\uD7AF'),
            _ => true
        };
    }

    public async Task<List<string>> GetAvailableModelsAsync()
    {
        var presets = _aiResourceService.GetLlamaModelPresets();
        var models = new List<string>(presets.Count);
        foreach (var preset in presets.AsValueEnumerable())
        {
            models.Add(preset.DisplayName);
        }
        return models;
    }

    private static string BuildMergedText(List<(SKRectI Box, string Text, float Confidence)> blocks)
    {
        if (blocks.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        for (int i = 0; i < blocks.Count; i++)
        {
            if (i > 0)
            {
                builder.Append('\n');
            }

            builder.Append(blocks[i].Text);
        }

        return builder.ToString();
    }

    private static SKRectI GetUnionBox(List<(SKRectI Box, string Text, float Confidence)> blocks)
    {
        int left = blocks[0].Box.Left;
        int top = blocks[0].Box.Top;
        int right = blocks[0].Box.Right;
        int bottom = blocks[0].Box.Bottom;

        for (int i = 1; i < blocks.Count; i++)
        {
            var box = blocks[i].Box;
            left = Math.Min(left, box.Left);
            top = Math.Min(top, box.Top);
            right = Math.Max(right, box.Right);
            bottom = Math.Max(bottom, box.Bottom);
        }

        return new SKRectI(left, top, right, bottom);
    }

    public void Dispose()
    {
        ReleaseHeavyResources(trimProcessWorkingSet: false);

        foreach (var engine in _translationEngines.AsValueEnumerable().OfType<IDisposable>())
        {
            engine.Dispose();
        }
    }

    private IOCREngine GetOrCreateOcrEngine()
    {
        _ocrEngine ??= new PaddleOCREngine(_aiResourceService, _settingsService, _ocrRuntimeService);
        return _ocrEngine;
    }

    private LlamaSharpTranslationEngine? GetLlamaEngine()
    {
        return _translationEngines.AsValueEnumerable().OfType<LlamaSharpTranslationEngine>().FirstOrDefault();
    }

    private static bool ContainsJapaneseKana(string text)
    {
        foreach (char c in text)
        {
            if ((c >= '\u3040' && c <= '\u309F') || (c >= '\u30A0' && c <= '\u30FF'))
            {
                return true;
            }
        }

        return false;
    }
}
