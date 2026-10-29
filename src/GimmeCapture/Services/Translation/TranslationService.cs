using System;
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

public partial class TranslationService : IDisposable
{
    private readonly IAppSettingsService _settingsService;
    private readonly OcrRuntimeService _ocrRuntimeService;
    private readonly ITranslationModelEngine _translationEngine;
    private readonly TranslationEngineRunner _translationEngineRunner;
    private readonly AIResourceService _aiResourceService;
    private readonly IOcrScriptDetector _scriptDetector;
    private readonly IOcrEngineFactory _ocrEngineFactory;
    private readonly TranslationPipeline _translationPipeline = new();
    private IOCREngine? _ocrEngine;
    private bool _keepOcrWarm;

    private AppSettings _settings => _settingsService.Settings;

    /// <param name="translationEngine">
    /// The app's engine, shared by every overlay (<see cref="TranslationRuntimeService"/>): this service translates
    /// through it, but its model and its lifetime are not this overlay's to end.
    /// </param>
    /// <param name="ocrEngineFactory">Builds the OCR engine; PaddleOCR unless a test hands in another.</param>
    public TranslationService(
        AIResourceService aiResourceService,
        IAppSettingsService settingsService,
        OcrRuntimeService ocrRuntimeService,
        IOcrScriptDetector scriptDetector,
        ITranslationModelEngine translationEngine,
        IOcrEngineFactory? ocrEngineFactory = null)
    {
        _aiResourceService = aiResourceService ?? throw new ArgumentNullException(nameof(aiResourceService));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _ocrRuntimeService = ocrRuntimeService ?? throw new ArgumentNullException(nameof(ocrRuntimeService));
        _scriptDetector = scriptDetector ?? throw new ArgumentNullException(nameof(scriptDetector));
        _translationEngine = translationEngine ?? throw new ArgumentNullException(nameof(translationEngine));
        _ocrEngineFactory = ocrEngineFactory ?? new PaddleOcrEngineFactory();
        _translationEngineRunner = new TranslationEngineRunner([_translationEngine]);
    }

    internal bool IsOcrLoaded => _ocrRuntimeService.IsLoaded;
    internal OCRLanguage? LoadedOcrLanguage => _ocrRuntimeService.LoadedLanguage;
    internal bool IsLlamaLoaded => _translationEngine.IsModelLoaded;

    public async Task WarmUpAsync(CancellationToken ct = default)
    {
        // Auto's real language is only known once a capture exists to probe, so the warm-up loads the fallback the
        // probe would settle on with no evidence.
        var warmupSourceLanguage = OcrLanguageResolver.Resolve(_settings.SourceLanguage);

        TranslationMemoryDiagnostics.Log(
            "translation-warmup-before",
            ocrLoaded: IsOcrLoaded,
            ocrLanguage: LoadedOcrLanguage?.ToString(),
            llamaLoaded: IsLlamaLoaded);

        // The translation model starts loading alongside OCR, instead of only once OCR has read the text. Not awaited:
        // a press with nothing new to translate should not wait for a model it will not use, and a translation that
        // does need it waits for this load inside the engine rather than starting another.
        PreloadTranslationModelAsync(ct).Forget("Translation.WarmupModel");

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
            AppLog.Warning("Translation.WarmupOcr", ex);
        }

        TranslationMemoryDiagnostics.Log(
            "translation-warmup-after",
            ocrLoaded: IsOcrLoaded,
            ocrLanguage: LoadedOcrLanguage?.ToString(),
            llamaLoaded: IsLlamaLoaded);
    }

    private async Task PreloadTranslationModelAsync(CancellationToken ct)
    {
        if (_settings.SelectedTranslationEngine != TranslationEngine.LlamaSharp || !_aiResourceService.IsLlamaModelReady())
        {
            return;
        }

        try
        {
            await _translationEngine.PreloadAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // The press ended, or translate mode closed, before the model was needed.
        }
        catch (Exception ex)
        {
            // The translation that needs the model loads it again and reports the failure where the user sees it.
            AppLog.Warning("Translation.WarmupModel", ex);
        }
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

            var ocrEngine = GetOrCreateOcrEngine();
            var ocrLang = _settings.SourceLanguage;
            OcrScriptDetectionResult? probe = null;
            if (ocrLang == OCRLanguage.Auto)
            {
                // The probe leaves the engine loaded with whatever it picked, and hands back the detection boxes
                // (the DB detection model is shared across every language, so they are valid for the winner too)
                // plus that winner's raw recognitions for the sampled boxes.
                probe = await _scriptDetector.DetectAsync(bitmap, ocrEngine, ct);
                ocrLang = probe.Language;
            }

            await ocrEngine.EnsureLoadedAsync(ocrLang, ct);
            TranslationMemoryDiagnostics.Log(
                "translation-ocr-ready",
                ocrLoaded: IsOcrLoaded,
                ocrLanguage: LoadedOcrLanguage?.ToString(),
                llamaLoaded: IsLlamaLoaded,
                bitmap: bitmap);
            var boxes = probe?.Boxes ?? ocrEngine.DetectText(bitmap);
            TranslationMemoryDiagnostics.Log(
                "translation-ocr-detect-complete",
                ocrLoaded: IsOcrLoaded,
                ocrLanguage: LoadedOcrLanguage?.ToString(),
                llamaLoaded: IsLlamaLoaded,
                selectionCount: boxes.Count,
                bitmap: bitmap);

            // The probe's samples came from the winning recogniser on these very boxes, so re-running it over
            // Boxes[0..N) would produce the same strings at the same cost. Empty when no probe ran.
            var sampled = probe?.SampledRecognitions ?? Array.Empty<(string Text, float Confidence)>();

            var recognizedBlocks = new List<(SKRectI Box, string Text, float Confidence)>();
            for (int i = 0; i < boxes.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var box = boxes[i];
                (string text, float confidence) = i < sampled.Count
                    ? sampled[i]
                    : ocrEngine.RecognizeText(bitmap, box, ct);
                string cleanedText = OcrTextSanitizer.Sanitize(text);
                if (OcrTextQualityPolicy.IsUseful(cleanedText, confidence))
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
            var translated = _translationPipeline.SanitizeTranslation(
                mergedText,
                await TranslateAsync(mergedText, ocrLang, ct),
                _settings.TargetLanguage);
            string bestEffortTranslation = _translationPipeline.PromoteBestEffort(mergedText, translated, _settings.TargetLanguage);
            TranslationMemoryDiagnostics.Log(
                "translation-llama-after",
                ocrLoaded: IsOcrLoaded,
                ocrLanguage: LoadedOcrLanguage?.ToString(),
                llamaLoaded: IsLlamaLoaded,
                selectionCount: recognizedBlocks.Count,
                bitmap: bitmap);

            if (string.IsNullOrEmpty(translated))
            {
                AppLog.Information("Translation.EmptyEngineResult");
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

            bool acceptable = _translationPipeline.IsAcceptable(mergedText, translated, _settings.TargetLanguage);
            if (!acceptable)
            {
                string retriedTranslation = _translationPipeline.SanitizeTranslation(
                    mergedText,
                    await ForceTranslateAsync(mergedText, ocrLang, _settings.TargetLanguage, ct),
                    _settings.TargetLanguage);
                string retriedBestEffort = _translationPipeline.PromoteBestEffort(mergedText, retriedTranslation, _settings.TargetLanguage);
                if (retriedBestEffort.Length > bestEffortTranslation.Length)
                {
                    bestEffortTranslation = retriedBestEffort;
                }

                translated = retriedTranslation;
                acceptable = _translationPipeline.IsAcceptable(mergedText, translated, _settings.TargetLanguage);
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
                AppLog.Information("Translation.Fallback.PerBlock");
                return (perBlockTranslation, string.Empty);
            }

            if (!string.IsNullOrWhiteSpace(bestEffortTranslation))
            {
                AppLog.Information("Translation.Fallback.BestEffort");
                return (result, string.Empty);
            }

            string originalFallbackText = _translationPipeline.BuildFailureFallback(mergedText, _settings.TargetLanguage);
            if (!string.IsNullOrWhiteSpace(originalFallbackText))
            {
                result[0].TranslatedText = string.Empty;
                result[0].OriginalText = originalFallbackText;
                AppLog.Information("Translation.Fallback.OcrOnly");

                return (result, "StatusTranslateFailedAcceptable");
            }

            AppLog.Information("Translation.Fallback.Unacceptable");
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
        string translated = _translationPipeline.SanitizeTranslation(text, await TranslateAsync(text, sourceLang, ct), _settings.TargetLanguage);
        string bestEffortTranslation = _translationPipeline.PromoteBestEffort(text, translated, _settings.TargetLanguage);
        if (_translationPipeline.IsAcceptable(text, translated, _settings.TargetLanguage))
        {
            return translated;
        }

        // Retry once with a likely source guess when Auto/ambiguous source may hurt engine routing.
        var guessedSource = GuessSourceLanguageFromText(text);
        if (guessedSource != sourceLang)
        {
            string retried = _translationPipeline.SanitizeTranslation(text, await TranslateAsync(text, guessedSource, ct), _settings.TargetLanguage);
            string retriedBestEffort = _translationPipeline.PromoteBestEffort(text, retried, _settings.TargetLanguage);
            if (retriedBestEffort.Length > bestEffortTranslation.Length)
            {
                bestEffortTranslation = retriedBestEffort;
            }

            if (_translationPipeline.IsAcceptable(text, retried, _settings.TargetLanguage))
            {
                return retried;
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

    private async Task<string> TranslateAsync(string text, OCRLanguage sourceLang, CancellationToken ct)
    {
        return await _translationEngineRunner.TranslateSelectedAsync(
            text,
            sourceLang,
            _settings.TargetLanguage,
            _settings.SelectedTranslationEngine,
            ct);
    }

    private async Task<string> ForceTranslateAsync(string text, OCRLanguage sourceLang, TranslationLanguage targetLang, CancellationToken ct)
    {
        return await _translationEngineRunner.TranslateWithLlamaAsync(
            text,
            sourceLang,
            targetLang,
            ct);
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
        // OCR only. The translation engine outlives this overlay: the next one translates through it, and its own idle
        // countdown frees the model.
        SetOcrWarmHold(false);
    }

    private IOCREngine GetOrCreateOcrEngine()
    {
        _ocrEngine ??= _ocrEngineFactory.Create(_aiResourceService, _settingsService, _ocrRuntimeService);
        return _ocrEngine;
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
