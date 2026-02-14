using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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

        var sourceLangName = ResolveSourceLanguageName(sourceLang, text);
        var prompt = BuildStrictTranslationPrompt(text, sourceLangName, targetLang.ToString());

        var request = new
        {
            model = model,
            prompt = prompt,
            stream = false,
            options = new { temperature = 0.1, top_p = 0.9, num_predict = 512 }
        };

        try
        {
            var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
            // Base URL from OllamaApiUrl, removing the endpoint part if present
            var baseUrl = settings.OllamaApiUrl.Replace("/api/generate", "");
            var response = await _httpClient.PostAsync($"{baseUrl}/api/generate", content, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var result = doc.RootElement.GetProperty("response").GetString() ?? "";
            
            return CleanupTranslationResult(result);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LLM] Translation Error: {ex.Message}");
            return text;
        }
    }

    private string BuildStrictTranslationPrompt(string text, string from, string to)
    {
         // Gemma 3 Prompt Format for strict translation
         return $"<|begin_of_text|><|start_header_id|>system<|end_header_id|>\n\n" +
                $"You are a professional translator. Translate the text from {from} to {to} exactly. " +
                $"Maintain the output format and line breaks. " +
                $"Do NOT add any explanations, notes, or conversational filler. " +
                $"Only output the translated text.<|eot_id|>" +
                $"<|start_header_id|>user<|end_header_id|>\n\n" +
                $"{text}<|eot_id|>" +
                $"<|start_header_id|>assistant<|end_header_id|>\n\n";
    }

    private string CleanupTranslationResult(string result)
    {
        if (string.IsNullOrWhiteSpace(result)) return "";
        var cleaned = result.Trim();
        // Remove common LLM conversational patterns
        if (cleaned.StartsWith("\"") && cleaned.EndsWith("\"")) cleaned = cleaned.Substring(1, cleaned.Length - 2);
        return cleaned;
    }

    private string ResolveSourceLanguageName(OCRLanguage lang, string text)
    {
        if (lang != OCRLanguage.Auto) return lang.ToString();
        
        // Simple heuristic for script detection
        if (text.Any(c => (c >= 0x3040 && c <= 0x309F) || (c >= 0x30A0 && c <= 0x30FF))) return "Japanese";
        if (text.Any(c => (c >= 0x1100 && c <= 0x11FF) || (c >= 0xAC00 && c <= 0xD7AF))) return "Korean";
        if (text.Any(c => (c >= 0x4E00 && c <= 0x9FFF))) return "Chinese";
        return "English";
    }
}
