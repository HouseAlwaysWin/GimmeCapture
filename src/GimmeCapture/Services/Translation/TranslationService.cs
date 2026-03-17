using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
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

public class TranslationService
{
    private readonly AppSettingsService _settingsService;
    private readonly IOCREngine _ocrEngine;
    private readonly IEnumerable<ITranslationEngine> _translationEngines;
    private readonly IOllamaApiClient _ollamaApiClient;
    private readonly AIResourceService _aiResourceService;
    private readonly MarianMTService _marianMTService;

    private AppSettings _settings => _settingsService.Settings;

    public TranslationService(
        AIResourceService aiResourceService, 
        AppSettingsService settingsService, 
        MarianMTService marianMTService)
    {
        _aiResourceService = aiResourceService ?? throw new ArgumentNullException(nameof(aiResourceService));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _marianMTService = marianMTService ?? throw new ArgumentNullException(nameof(marianMTService));
        
        // Manual DI for now as the app doesn't use a container in constructor injection here
        var translationCache = new InMemoryTranslationCache();
        var executionPolicy = new TranslationExecutionPolicy();
        _ollamaApiClient = new OllamaApiClient(new HttpClient
        {
            // Timeout is controlled by TranslationExecutionHelper + policy.
            Timeout = Timeout.InfiniteTimeSpan
        }, settingsService, executionPolicy);

        _ocrEngine = new PaddleOCREngine(aiResourceService, settingsService);
        _translationEngines = new List<ITranslationEngine>
        {
            new LLMTranslationEngine(_ollamaApiClient, settingsService, translationCache),
            new MarianMTTranslationEngine(_marianMTService)
        };
    }

    public async Task WarmUpAsync(CancellationToken ct = default)
    {
        var warmupSourceLanguage = _settings.SourceLanguage == OCRLanguage.Auto
            ? OCRLanguage.TraditionalChinese
            : _settings.SourceLanguage;

        try
        {
            bool ocrReady = await _aiResourceService.EnsureOCRAsync(warmupSourceLanguage, ct);
            if (ocrReady)
            {
                await _ocrEngine.EnsureLoadedAsync(warmupSourceLanguage, ct);
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

        if (_settings.SelectedTranslationEngine == TranslationEngine.MarianMT)
        {
            try
            {
                bool nmtReady = await _aiResourceService.EnsureNmtAsync(ct);
                if (nmtReady)
                {
                    await _marianMTService.EnsureLoadedAsync(ct);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TranslationService] Warm-up MarianMT failed: {ex.Message}");
            }

            return;
        }

        if (_settings.SelectedTranslationEngine == TranslationEngine.Ollama)
        {
            var model = _settings.OllamaModel;
            if (string.IsNullOrWhiteSpace(model))
            {
                return;
            }

            try
            {
                bool ready = await _ollamaApiClient.IsReadyAsync(model, ct);
                if (!ready)
                {
                    return;
                }

                // Trigger model cold-start in the background so first real translation is faster.
                await _ollamaApiClient.GenerateAsync(model, "Reply with OK only.", ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TranslationService] Warm-up Ollama failed: {ex.Message}");
            }
        }
    }

    public async Task<ResourceReadyResult> CheckEngineReadyAsync()
    {
        var engineType = _settings.SelectedTranslationEngine;
        if (engineType == TranslationEngine.MarianMT)
        {
            if (!_aiResourceService.IsNmtReady())
            {
                return ResourceReadyResult.NotReady("StatusMarianMTNotReady", true);
            }
        }
        else if (engineType == TranslationEngine.Ollama)
        {
            var model = _settings.OllamaModel;
            bool ready = await _ollamaApiClient.IsReadyAsync(model);
            if (!ready)
            {
                return ResourceReadyResult.NotReady("StatusOllamaRequired", false);
            }
        }

        return ResourceReadyResult.Ready();
    }

    public async Task<(List<TranslatedBlock> Blocks, string ErrorKey)> AnalyzeAndTranslateAsync(SKBitmap bitmap, double scale = 1.0, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var ocrLang = _settings.SourceLanguage;
        Console.WriteLine($"[TranslationService] SourceLanguage={ocrLang}, TargetLanguage={_settings.TargetLanguage}");
        if (ocrLang == OCRLanguage.Auto)
        {
            ocrLang = await DetectScriptLanguageAsync(bitmap, ct);
            Console.WriteLine($"[TranslationService] Auto-detected source: {ocrLang}");
        }

        await _ocrEngine.EnsureLoadedAsync(ocrLang, ct);
        var boxes = _ocrEngine.DetectText(bitmap);
        
        var recognizedBlocks = new List<(SKRectI Box, string Text, float Confidence)>();
        foreach (var box in boxes)
        {
            ct.ThrowIfCancellationRequested();
            var (text, confidence) = _ocrEngine.RecognizeText(bitmap, box, ct);
            if (IsUsefulOcrText(text, confidence))
            {
                recognizedBlocks.Add((box, text, confidence));
            }
        }

        if (recognizedBlocks.Count == 0) return (new List<TranslatedBlock>(), "StatusTranslateNoText");

        var sortedBlocks = recognizedBlocks
            .OrderBy(b => b.Box.Top / 16)
            .ThenBy(b => b.Box.Left)
            .ToList();

        var mergedText = string.Join("\n", sortedBlocks.Select(b => b.Text));
        var unionBox = new SKRectI(
            sortedBlocks.Min(b => b.Box.Left),
            sortedBlocks.Min(b => b.Box.Top),
            sortedBlocks.Max(b => b.Box.Right),
            sortedBlocks.Max(b => b.Box.Bottom));

        var translated = await TranslateAsync(mergedText, ocrLang, ct);
        
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
        
        // Infer font size from average height of OCR blocks (Pixels -> DIPs)
        double inferredFontSize = 12.0;
        if (sortedBlocks.Any())
        {
            double averagePixelHeight = sortedBlocks.Average(b => (double)b.Box.Height);
            // Convert to DIPs by dividing by scale, and apply a 0.85 correction factor
            // as OCR bounding boxes are usually taller than the actual text font size.
            inferredFontSize = (averagePixelHeight / scale) * 0.85;
            // Limit range to reasonable font sizes
            inferredFontSize = Math.Clamp(inferredFontSize, 8.0, 72.0);
        }

        bool acceptable = IsTranslationAcceptable(mergedText, translated, _settings.TargetLanguage);
        if (!acceptable)
        {
            // Retry for ANY language if the first attempt was unacceptable
            translated = await ForceTranslateAsync(mergedText, ocrLang, _settings.TargetLanguage, ct);
            acceptable = IsTranslationAcceptable(mergedText, translated, _settings.TargetLanguage);
        }

        var result = new List<TranslatedBlock>();
        
        // Convert unionBox (Pixels) to logical bounds (DIPs)
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
                InferredFontSize = inferredFontSize
            });
            return (result, string.Empty);
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"[TranslationService] Final translation effort failed or was unacceptable. Returning empty result.");
            return (result, "StatusTranslateFailedAcceptable");
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
        foreach (var engine in _translationEngines.Where(e => e.EngineType != _settings.SelectedTranslationEngine))
        {
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
        await _ocrEngine.EnsureLoadedAsync(OCRLanguage.TraditionalChinese, ct);
        var boxes = _ocrEngine.DetectText(bitmap);
        foreach (var box in boxes.Take(5))
        {
            var (text, _) = _ocrEngine.RecognizeText(bitmap, box, ct);
            if (text.Any(c => (c >= 0x3040 && c <= 0x309F) || (c >= 0x30A0 && c <= 0x30FF)))
                return OCRLanguage.Japanese;
        }
        return OCRLanguage.TraditionalChinese;
    }

    private async Task<string> TranslateAsync(string text, OCRLanguage sourceLang, CancellationToken ct)
    {
        var engine = _translationEngines.FirstOrDefault(e => e.EngineType == _settings.SelectedTranslationEngine);
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
        // Force the use of LLM for retry with a stricter target language prompt
        var llm = _translationEngines.OfType<LLMTranslationEngine>().FirstOrDefault();
        if (llm != null) return await llm.TranslateAsync(text, sourceLang, targetLang, ct);
        return string.Empty;
    }

    private bool IsUsefulOcrText(string text, float confidence)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (confidence < 0.10f) return false;

        string trimmed = text.Trim();
        if (trimmed.Length == 0) return false;

        int replacementCount = trimmed.Count(ch => ch == '\uFFFD');
        if (replacementCount > 0) return false;
        if (trimmed.All(ch => ch == '?' || ch == '.' || ch == '-' || ch == '_' || ch == '*')) return false;

        int useful = trimmed.Count(ch => char.IsLetterOrDigit(ch) || (ch >= 0x4E00 && ch <= 0x9FFF) || (ch >= 0x3040 && ch <= 0x309F) || (ch >= 0x30A0 && ch <= 0x30FF));
        if (useful == 0) return false;
        
        return true;
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
        if (text.Any(c => c >= '\u3040' && c <= '\u30FF')) return OCRLanguage.Japanese;
        if (text.Any(c => c >= '\uAC00' && c <= '\uD7AF')) return OCRLanguage.Korean;
        if (text.Any(c => c >= '\u4E00' && c <= '\u9FFF')) return OCRLanguage.TraditionalChinese;
        if (text.Any(c => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))) return OCRLanguage.English;
        return OCRLanguage.Auto;
    }

    private static bool ContainsTargetScript(string text, TranslationLanguage target)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        return target switch
        {
            TranslationLanguage.TraditionalChinese or TranslationLanguage.SimplifiedChinese =>
                text.Any(c => c >= '\u4E00' && c <= '\u9FFF'),
            TranslationLanguage.Japanese =>
                text.Any(c => (c >= '\u3040' && c <= '\u30FF') || (c >= '\u4E00' && c <= '\u9FFF')),
            TranslationLanguage.Korean =>
                text.Any(c => c >= '\uAC00' && c <= '\uD7AF'),
            _ => true
        };
    }

    public async Task<List<string>> GetAvailableModelsAsync()
    {
        var models = await _ollamaApiClient.GetModelsAsync();
        return models.ToList();
    }
}
