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
                if (IsUsefulOcrText(text, confidence))
                {
                    recognizedBlocks.Add((box, text, confidence));
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
            var translated = await TranslateAsync(mergedText, ocrLang, ct);
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
                return (new List<TranslatedBlock>(), "StatusTranslateFailedEngine");
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
                translated = await ForceTranslateAsync(mergedText, ocrLang, _settings.TargetLanguage, ct);
                acceptable = IsTranslationAcceptable(mergedText, translated, _settings.TargetLanguage);
            }

            var result = new List<TranslatedBlock>();

            var logicalBounds = new Rect(
                unionBox.Left / scale,
                unionBox.Top / scale,
                unionBox.Width / scale,
                unionBox.Height / scale);

            if (acceptable)
            {
                result.Add(new TranslatedBlock
                {
                    OriginalText = mergedText,
                    TranslatedText = translated,
                    Bounds = logicalBounds,
                    InferredFontSize = inferredFontSize,
                    DisplayFontSize = inferredFontSize
                });
                return (result, string.Empty);
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[TranslationService] Final translation effort failed or was unacceptable. Returning empty result.");
                return (result, "StatusTranslateFailedAcceptable");
            }
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
        string translated = (await TranslateAsync(text, sourceLang, ct)).Trim();
        if (IsTranslationAcceptable(text, translated, _settings.TargetLanguage))
        {
            return translated;
        }

        // Retry once with a likely source guess when Auto/ambiguous source may hurt engine routing.
        var guessedSource = GuessSourceLanguageFromText(text);
        if (guessedSource != sourceLang)
        {
            string retried = (await TranslateAsync(text, guessedSource, ct)).Trim();
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

            string fallback = (await engine.TranslateAsync(text, guessedSource, _settings.TargetLanguage, ct)).Trim();
            if (IsTranslationAcceptable(text, fallback, _settings.TargetLanguage))
            {
                return fallback;
            }
        }

        return string.Empty;
    }

    private async Task<OCRLanguage> DetectScriptLanguageAsync(SKBitmap bitmap, CancellationToken ct)
    {
        var ocrEngine = GetOrCreateOcrEngine();
        await ocrEngine.EnsureLoadedAsync(OCRLanguage.TraditionalChinese, ct);
        var boxes = ocrEngine.DetectText(bitmap);
        int sampleCount = Math.Min(5, boxes.Count);
        for (int i = 0; i < sampleCount; i++)
        {
            var box = boxes[i];
            var (text, _) = ocrEngine.RecognizeText(bitmap, box, ct);
            if (ContainsJapaneseKana(text))
                return OCRLanguage.Japanese;
        }
        return OCRLanguage.TraditionalChinese;
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
            ProcessMemoryTrimService.TrimCurrentProcessWorkingSet();
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
        foreach (char ch in trimmed)
        {
            if (ch == '\uFFFD')
            {
                return false;
            }

            bool isPlaceholder = ch is '?' or '.' or '-' or '_' or '*';
            allPlaceholder &= isPlaceholder;

            if (char.IsLetterOrDigit(ch)
                || (ch >= 0x4E00 && ch <= 0x9FFF)
                || (ch >= 0x3040 && ch <= 0x309F)
                || (ch >= 0x30A0 && ch <= 0x30FF))
            {
                sawUseful = true;
            }
        }

        return sawUseful && !allPlaceholder;
    }

    private bool IsTranslationAcceptable(string original, string translated, TranslationLanguage target)
    {
        if (string.IsNullOrWhiteSpace(translated)) return false;
        if (target == TranslationLanguage.English) return true;

        // For CJK targets, reject pure Latin outputs that look like untranslated text.
        if (!ContainsTargetScript(translated, target))
        {
            return false;
        }
        return true;
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
