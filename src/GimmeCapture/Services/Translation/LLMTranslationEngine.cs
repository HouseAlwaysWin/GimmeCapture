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
        if (string.IsNullOrEmpty(model)) 
        {
            System.Diagnostics.Debug.WriteLine("[LLM] SKIPPED: No model selected in settings.");
            return string.Empty;
        }

        string sourceLangName = ResolveSourceLanguageForPrompt(text, sourceLang);
        string targetLangName = GetTargetLanguageName(targetLang);
        
        var request = new
        {
            model = model,
            prompt = $"Translate \"{text}\" from {sourceLangName} to {targetLangName}. Output ONLY the translated text. No quotes. No explanations.",
            stream = false
            // Removed options to use server defaults as per user feedback
        };

        try
        {
            string baseUrl = !string.IsNullOrEmpty(settings.OllamaApiUrl) ? settings.OllamaApiUrl.TrimEnd('/') : "http://localhost:11434";
            string url;
            if (baseUrl.EndsWith("/api/generate")) url = baseUrl;
            else if (baseUrl.EndsWith("/api/chat")) url = baseUrl.Replace("/api/chat", "/api/generate");
            else url = baseUrl + "/api/generate";

            string requestJson = JsonSerializer.Serialize(request);
            System.Diagnostics.Debug.WriteLine($"[LLM] Requesting translation for: {text}");
            System.Diagnostics.Debug.WriteLine($"[LLM] Request JSON: {requestJson}");

            var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(url, content, ct);
            
            if (!response.IsSuccessStatusCode)
            {
                System.Diagnostics.Debug.WriteLine($"[LLM] API Error: {response.StatusCode}");
                return string.Empty;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
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
                var retried = await TranslateStrictRetryForCjkAsync(model, url, text, targetLang, ct);
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

            return resultText;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LLM] Translation Error: {ex.Message}");
            return string.Empty;
        }
    }

    private async Task<string> TranslateStrictRetryForCjkAsync(string model, string url, string text, TranslationLanguage target, CancellationToken ct)
    {
        string targetName = GetTargetLanguageName(target);
        var req = new
        {
            model,
            prompt = $"The previous translation for \"{text}\" was incorrect. Please output ONLY the raw {targetName} translation of \"{text}\". No explanations.",
            stream = false
        };

        try
        {
            var content = new StringContent(JsonSerializer.Serialize(req), Encoding.UTF8, "application/json");
            var resp = await _httpClient.PostAsync(url, content, ct);
            if (resp == null || !resp.IsSuccessStatusCode) return string.Empty;
            var payload = await resp.Content.ReadAsStringAsync();
            System.Diagnostics.Debug.WriteLine($"[LLM-Retry] Raw Response: {payload}");
            using var doc = JsonDocument.Parse(payload);
            
            if (doc.RootElement.TryGetProperty("response", out var responseProp))
            {
                return CleanupTranslationResult(responseProp.GetString()?.Trim() ?? string.Empty);
            }
            return string.Empty;
        }
        catch { return string.Empty; }
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

        // Hallucination Check: If source is tiny, but translation is a long sentence, it's likely a hallucination
        if (source.Trim().Length <= 6 && translated.Length > 25)
        {
            System.Diagnostics.Debug.WriteLine($"[LLM] Validation: rejected due to length jump (Hallucination suspected).");
            return false;
        }

        // If target is Japanese, it MUST contain kana if it's more than a few words...
        if (target == TranslationLanguage.Japanese)
        {
            // For Japanese, we expect some kana.
            bool hasKana = translated.Any(c => (c >= 0x3040 && c <= 0x309F) || (c >= 0x30A0 && c <= 0x30FF));
            if (!hasKana && translated.Length > 2) // Allow 1-2 char Kanji-only words
            {
                System.Diagnostics.Debug.WriteLine($"[LLM] Validation: rejected Japanese output (No Kana found).");
                return false; 
            }
        }

        // If target is Traditional/Simplified Chinese, it should NOT contain excessive Japanese kana
        if ((target == TranslationLanguage.TraditionalChinese || target == TranslationLanguage.SimplifiedChinese))
        {
             // For Chinese variants, some technical terms or proper names might have kana in source, 
             // but usually none in target. However, we should be lenient.
             int tKana = translated.Count(c => (c >= 0x3040 && c <= 0x309F) || (c >= 0x30A0 && c <= 0x30FF));
             if (tKana > 0)
             {
                 int sKana = source.Count(c => (c >= 0x3040 && c <= 0x309F) || (c >= 0x30A0 && c <= 0x30FF));
                 // If source had NO kana, but target HAS kana -> Hallucination or failed translation
                 if (sKana == 0 && tKana > 0)
                 {
                     System.Diagnostics.Debug.WriteLine($"[LLM] Validation: rejected Chinese output (Hallucinated Kana).");
                     return false;
                 }
                 
                 // If source HAD kana, allow some in target (maybe untranslated names), but not too much
                 if (tKana > translated.Length / 3) 
                 {
                     System.Diagnostics.Debug.WriteLine($"[LLM] Validation: rejected Chinese output (Too much Kana remains).");
                     return false;
                 }
             }
        }
        
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
        _ => text.Any(c => (c >= 0x3040 && c <= 0x309F) || (c >= 0x30A0 && c <= 0x30FF)) ? "Japanese" :
             text.Any(c => (c >= 0x1100 && c <= 0x11FF) || (c >= 0xAC00 && c <= 0xD7AF)) ? "Korean" :
             text.Any(c => (c >= 0x4E00 && c <= 0x9FFF)) ? "Chinese" : "English"
    };
}
