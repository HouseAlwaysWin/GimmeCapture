using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading;
using System.Text.RegularExpressions;
using Avalonia;
using GimmeCapture.Models; // This using statement is already present, indicating the target namespace
using GimmeCapture.Services.Core.Interfaces;
using GimmeCapture.Services.OCR;
using GimmeCapture.Services.Translation;
using SkiaSharp;
using SKRectI = SkiaSharp.SKRectI;

// Assuming TranslatedBlock was previously defined here within GimmeCapture.Services.Core
// and needs to be moved to GimmeCapture.Models.
// Since the definition of TranslatedBlock is not in the provided document,
// I cannot physically move it. However, the instruction implies it exists
// and should conceptually be in GimmeCapture.Models.
// The existing `using GimmeCapture.Models;` statement already makes it accessible.
// If the class definition was present, I would move it to a new namespace block.
// As it's not present, I will assume the instruction is about its logical location
// and that the `using` statement correctly reflects its new location.

namespace GimmeCapture.Services.Core;

public class TranslationService
{
    private readonly AppSettingsService _settingsService;
    private readonly IOCREngine _ocrEngine;
    private readonly IEnumerable<ITranslationEngine> _translationEngines;
    private readonly HttpClient _httpClient = new();

    private AppSettings _settings => _settingsService.Settings;

    public TranslationService(
        AIResourceService aiResourceService, 
        AppSettingsService settingsService, 
        MarianMTService marianMTService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        
        // Manual DI for now as the app doesn't use a container in constructor injection here
        _ocrEngine = new PaddleOCREngine(aiResourceService, settingsService);
        _translationEngines = new List<ITranslationEngine>
        {
            new LLMTranslationEngine(_httpClient, settingsService),
            new MarianMTTranslationEngine(marianMTService)
        };
        
        _httpClient.Timeout = TimeSpan.FromSeconds(20);
    }

    public async Task<List<TranslatedBlock>> AnalyzeAndTranslateAsync(SKBitmap bitmap, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var ocrLang = _settings.SourceLanguage;
        if (ocrLang == OCRLanguage.Auto)
        {
            ocrLang = await DetectScriptLanguageAsync(bitmap, ct);
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

        if (recognizedBlocks.Count == 0) return new List<TranslatedBlock>();

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
        
        bool acceptable = IsTranslationAcceptable(mergedText, translated, _settings.TargetLanguage);
        if (!acceptable && _settings.TargetLanguage == TranslationLanguage.English)
        {
            translated = await ForceTranslateAsync(mergedText, ocrLang, ct);
            acceptable = IsTranslationAcceptable(mergedText, translated, TranslationLanguage.English);
        }

        var result = new List<TranslatedBlock>();
        if (acceptable)
        {
            result.Add(new TranslatedBlock
            {
                OriginalText = mergedText,
                TranslatedText = translated,
                Bounds = new Rect(unionBox.Left, unionBox.Top, unionBox.Width, unionBox.Height)
            });
        }
        else
        {
            var fallback = BuildTargetLanguageFallbackText(mergedText, _settings.TargetLanguage);
            if (!string.IsNullOrWhiteSpace(fallback))
            {
                result.Add(new TranslatedBlock
                {
                    OriginalText = mergedText,
                    TranslatedText = fallback,
                    Bounds = new Rect(unionBox.Left, unionBox.Top, unionBox.Width, unionBox.Height)
                });
            }
        }

        return result;
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
        if (engine == null) return text;
        return await engine.TranslateAsync(text, sourceLang, _settings.TargetLanguage, ct);
    }

    private async Task<string> ForceTranslateAsync(string text, OCRLanguage sourceLang, CancellationToken ct)
    {
        // Try fallback to LLM if current is not LLM, or just retry with strict English prompt
        var llm = _translationEngines.OfType<LLMTranslationEngine>().FirstOrDefault();
        if (llm != null) return await llm.TranslateAsync(text, sourceLang, TranslationLanguage.English, ct);
        return text;
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
        if (original.Length > 3 && translated == original) return false;
        return true;
    }

    private string BuildTargetLanguageFallbackText(string text, TranslationLanguage target)
    {
        if (target == TranslationLanguage.TraditionalChinese) return text; // Already source-like
        return text; // Simple fallback
    }

    public async Task<List<string>> GetAvailableModelsAsync()
    {
        try
        {
            var baseUrl = _settings.OllamaApiUrl.Replace("/api/generate", "");
            var response = await _httpClient.GetAsync($"{baseUrl}/api/tags");
            if (!response.IsSuccessStatusCode) return new List<string>();
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("models").EnumerateArray()
                .Select(m => m.GetProperty("name").GetString() ?? "").ToList();
        }
        catch { return new List<string>(); }
    }
}
