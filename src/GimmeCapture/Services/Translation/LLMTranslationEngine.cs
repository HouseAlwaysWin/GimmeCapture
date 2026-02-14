using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Core;
using GimmeCapture.Services.Core.Interfaces;

namespace GimmeCapture.Services.Translation;

public class LLMTranslationEngine : ITranslationEngine
{
    public TranslationEngine EngineType => TranslationEngine.Ollama;
    
    private readonly HttpClient _httpClient;
    private readonly AppSettingsService _settingsService;

    public LLMTranslationEngine(HttpClient httpClient, AppSettingsService settingsService)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
    }

    public async Task<string> TranslateAsync(string text, OCRLanguage sourceLang, TranslationLanguage targetLang, CancellationToken ct = default)
    {
        var settings = _settingsService.Settings;
        var model = settings.OllamaModel;
        if (string.IsNullOrEmpty(model)) return text;

        string sourceLangName = ResolveSourceLanguageForPrompt(text, sourceLang);
        string targetLangName = GetTargetLanguageName(targetLang);
        
        var request = new
        {
            model = model,
            prompt = BuildStrictTranslationPrompt(sourceLangName, targetLangName, text),
            stream = false,
            options = new
            {
                temperature = 0.0,
                top_p = 0.1,
                repeat_penalty = 1.0,
                num_predict = 512,
                seed = 42
            }
        };

        try
        {
            string url = !string.IsNullOrEmpty(settings.OllamaApiUrl) ? 
                (settings.OllamaApiUrl.EndsWith("/api/generate") ? settings.OllamaApiUrl : settings.OllamaApiUrl.TrimEnd('/') + "/api/generate") : 
                "http://localhost:11434/api/generate";

            var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(url, content, ct);
            if (!response.IsSuccessStatusCode) return text;

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var resultText = doc.RootElement.GetProperty("response").GetString()?.Trim() ?? text;
            
            resultText = CleanupTranslationResult(resultText);
            if (string.IsNullOrWhiteSpace(resultText)) return text;

            // Optional: Restore retry logic if it still drifts
            if (!IsTranslationAcceptableInternal(text, resultText, targetLang))
            {
                var retried = await TranslateStrictRetryForCjkAsync(model, url, text, targetLang, ct);
                if (!string.IsNullOrWhiteSpace(retried) && IsTranslationAcceptableInternal(text, retried, targetLang))
                {
                    resultText = retried;
                }
            }

            return resultText;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LLM] Translation Error: {ex.Message}");
            return text;
        }
    }

    private static string BuildStrictTranslationPrompt(string sourceLang, string targetLang, string text)
    {
        return $@"You are an expert translator. 
Translate the following {sourceLang} text into {targetLang} accurately.

Rules:
1) Output ONLY the translated text.
2) NO explanations, NO quotes, NO original text.
3) Do NOT say ""Translation:"", ""Sure"", or ""Here is the translation"".
4) Preserve formatting, punctuation, and ORIGINAL LINE BREAKS.
5) If the text is already in {targetLang}, return it as is.

Input:
{text}

Output:";
    }

    private async Task<string> TranslateStrictRetryForCjkAsync(string model, string url, string text, TranslationLanguage target, CancellationToken ct)
    {
        string targetName = GetTargetLanguageName(target);
        var req = new
        {
            model,
            prompt = $@"Translate to {targetName}. 
Rules: 
1) Output ONLY {targetName}. 
2) ABSOLUTELY NO Japanese kana or Korean hangul if translating to Chinese.
3) NO explanations.
Input: {text}
Output:",
            stream = false,
            options = new { temperature = 0.0, top_p = 0.1, num_predict = 512, seed = 43 }
        };

        try
        {
            var content = new StringContent(JsonSerializer.Serialize(req), Encoding.UTF8, "application/json");
            var resp = await _httpClient.PostAsync(url, content, ct);
            if (resp == null || !resp.IsSuccessStatusCode) return string.Empty;
            var payload = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(payload);
            return CleanupTranslationResult(doc.RootElement.GetProperty("response").GetString()?.Trim() ?? string.Empty);
        }
        catch { return string.Empty; }
    }

    private string CleanupTranslationResult(string result)
    {
        if (string.IsNullOrWhiteSpace(result)) return "";
        var cleaned = result.Trim();
        // Remove common prefixes
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
        if (target != TranslationLanguage.Japanese && translated.Any(c => (c >= 0x3040 && c <= 0x309F) || (c >= 0x30A0 && c <= 0x30FF)))
        {
            if (!source.Any(c => (c >= 0x3040 && c <= 0x309F) || (c >= 0x30A0 && c <= 0x30FF))) return false;
            // If source had kana, but result has too much kana left
            int sKana = source.Count(c => (c >= 0x3040 && c <= 0x309F) || (c >= 0x30A0 && c <= 0x30FF));
            int tKana = translated.Count(c => (c >= 0x3040 && c <= 0x309F) || (c >= 0x30A0 && c <= 0x30FF));
            if (tKana > translated.Length / 3) return false;
        }
        return true;
    }

    private string GetTargetLanguageName(TranslationLanguage lang) => lang switch
    {
        TranslationLanguage.TraditionalChinese => "Traditional Chinese (Taiwan)",
        TranslationLanguage.SimplifiedChinese => "Simplified Chinese",
        TranslationLanguage.English => "English",
        TranslationLanguage.Japanese => "Japanese",
        TranslationLanguage.Korean => "Korean",
        _ => "Traditional Chinese"
    };

    private string ResolveSourceLanguageForPrompt(string text, OCRLanguage lang)
    {
        if (lang != OCRLanguage.Auto) return lang.ToString();
        if (text.Any(c => (c >= 0x3040 && c <= 0x309F) || (c >= 0x30A0 && c <= 0x30FF))) return "Japanese";
        if (text.Any(c => (c >= 0x1100 && c <= 0x11FF) || (c >= 0xAC00 && c <= 0xD7AF))) return "Korean";
        if (text.Any(c => (c >= 0x4E00 && c <= 0x9FFF))) return "Chinese";
        return "English";
    }
}
