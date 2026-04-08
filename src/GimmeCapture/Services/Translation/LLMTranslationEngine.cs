using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Interfaces;

namespace GimmeCapture.Services.Translation;

public class LLMTranslationEngine : ITranslationEngine
{
    public TranslationEngine EngineType => TranslationEngine.Ollama;
    
    private readonly IOllamaApiClient _ollamaApiClient;
    private readonly AppSettingsService _settingsService;
    private readonly ITranslationCache _cache;

    public LLMTranslationEngine(IOllamaApiClient ollamaApiClient, AppSettingsService settingsService, ITranslationCache cache)
    {
        _ollamaApiClient = ollamaApiClient ?? throw new ArgumentNullException(nameof(ollamaApiClient));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    public async Task<string> TranslateAsync(string text, OCRLanguage sourceLang, TranslationLanguage targetLang, CancellationToken ct = default)
    {
        return await TranslationExecutionHelper.ExecuteAsync(
            async token =>
            {
                var settings = _settingsService.Settings;
                var model = settings.OllamaModel;
                if (string.IsNullOrEmpty(model)) 
                {
                    System.Diagnostics.Debug.WriteLine("[LLM] SKIPPED: No model selected in settings.");
                    return string.Empty;
                }

                var cacheKey = _cache.BuildKey(EngineType, sourceLang, targetLang, text);
                if (_cache.TryGet(cacheKey, out var cachedResult))
                {
                    return cachedResult;
                }

                string sourceLangName = ResolveSourceLanguageForPrompt(text, sourceLang);
                string targetLangName = GetTargetLanguageName(targetLang);
                var prompt = $"Translate \"{text}\" from {sourceLangName} to {targetLangName}. Output ONLY the translated text. No quotes. No explanations.";

                Console.WriteLine($"[LLM] Prompt: {prompt}");
                Console.WriteLine($"[LLM] Source={sourceLangName}, Target={targetLangName}, TargetLangEnum={targetLang}");
                var json = await _ollamaApiClient.GenerateAsync(model, prompt, token);
                if (string.IsNullOrWhiteSpace(json)) return string.Empty;
                System.Diagnostics.Debug.WriteLine($"[LLM] Raw Response: {json}");
                using var doc = JsonDocument.Parse(json);
                
                string resultText = string.Empty;
                if (doc.RootElement.TryGetProperty("response", out var responseProp))
                {
                    resultText = responseProp.GetString()?.Trim() ?? string.Empty;
                }
                else if (doc.RootElement.TryGetProperty("message", out var messageObj) && 
                    messageObj.TryGetProperty("content", out var contentProp))
                {
                    // Fallback for chat-style response
                    resultText = contentProp.GetString()?.Trim() ?? string.Empty;
                }
                
                resultText = CleanupTranslationResult(resultText);
                if (string.IsNullOrWhiteSpace(resultText)) return string.Empty;

                // Optional: Restore retry logic if it still drifts
                if (!IsTranslationAcceptableInternal(text, resultText, targetLang))
                {
                    var retried = await TranslateStrictRetryForCjkAsync(model, text, targetLang, token);
                    if (!string.IsNullOrWhiteSpace(retried) && IsTranslationAcceptableInternal(text, retried, targetLang))
                    {
                        resultText = retried;
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("[LLM] Validation failed even after retry. Rejecting output.");
                        return string.Empty;
                    }
                }

                _cache.Set(cacheKey, resultText);
                return resultText;
            },
            ct,
            Timeout.InfiniteTimeSpan,
            () => string.Empty,
            "LLMTranslationEngine.Translate");
    }

    private async Task<string> TranslateStrictRetryForCjkAsync(string model, string text, TranslationLanguage target, CancellationToken ct)
    {
        string targetName = GetTargetLanguageName(target);
        var prompt = $"The previous translation for \"{text}\" was incorrect. Please output ONLY the raw {targetName} translation of \"{text}\". No explanations.";

        return await TranslationExecutionHelper.ExecuteAsync(
            async token =>
            {
                var payload = await _ollamaApiClient.GenerateAsync(model, prompt, token);
                if (string.IsNullOrWhiteSpace(payload)) return string.Empty;
                System.Diagnostics.Debug.WriteLine($"[LLM-Retry] Raw Response: {payload}");
                using var doc = JsonDocument.Parse(payload);
                
                if (doc.RootElement.TryGetProperty("response", out var responseProp))
                {
                    return CleanupTranslationResult(responseProp.GetString()?.Trim() ?? string.Empty);
                }
                return string.Empty;
            },
            ct,
            Timeout.InfiniteTimeSpan,
            () => string.Empty,
            "LLMTranslationEngine.Retry");
    }

    private string CleanupTranslationResult(string result)
    {
        if (string.IsNullOrWhiteSpace(result)) return "";
        
        var cleaned = result.Trim();

        // 1. Remove thinking blocks (for reasoning models)
        if (cleaned.Contains("<think>") && cleaned.Contains("</think>"))
        {
            int endIndex = cleaned.IndexOf("</think>");
            cleaned = cleaned.Substring(endIndex + 8).Trim();
        }
        else if (cleaned.Contains("</think>"))
        {
             // Handle case where start tag might be missing or cut off
             int endIndex = cleaned.IndexOf("</think>");
             cleaned = cleaned.Substring(endIndex + 8).Trim();
        }

        // 2. Remove common prefixes
        var prefixes = new[] { "Translation:", "Output:", "Result:", "Translated text:" };
        foreach (var p in prefixes)
        {
            if (cleaned.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                cleaned = cleaned.Substring(p.Length).Trim();
        }
        // Remove surrounding quotes
        if (cleaned.StartsWith("\"") && cleaned.EndsWith("\"") && cleaned.Length > 2)
            cleaned = cleaned.Substring(1, cleaned.Length - 2);
        
        return cleaned.Trim();
    }

    private bool IsTranslationAcceptableInternal(string source, string translated, TranslationLanguage target)
    {
        if (string.IsNullOrWhiteSpace(translated)) return false;
        return true;
    }

    private string GetTargetLanguageName(TranslationLanguage lang) => lang switch
    {
        TranslationLanguage.TraditionalChinese => "Traditional Chinese",
        TranslationLanguage.SimplifiedChinese => "Simplified Chinese",
        TranslationLanguage.English => "English",
        TranslationLanguage.Japanese => "Japanese",
        TranslationLanguage.Korean => "Korean",
        _ => "Chinese"
    };

    private string ResolveSourceLanguageForPrompt(string text, OCRLanguage lang) => lang switch
    {
        OCRLanguage.TraditionalChinese => "Traditional Chinese",
        OCRLanguage.SimplifiedChinese => "Simplified Chinese",
        OCRLanguage.Japanese => "Japanese",
        OCRLanguage.Korean => "Korean",
        OCRLanguage.English => "English",
        _ => text.AsValueEnumerable().Any(c => (c >= 0x3040 && c <= 0x309F) || (c >= 0x30A0 && c <= 0x30FF)) ? "Japanese" :
             text.AsValueEnumerable().Any(c => (c >= 0x1100 && c <= 0x11FF) || (c >= 0xAC00 && c <= 0xD7AF)) ? "Korean" :
             text.AsValueEnumerable().Any(c => (c >= 0x4E00 && c <= 0x9FFF)) ? "Chinese" : "English"
    };
}
