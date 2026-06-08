using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.AI;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Interfaces;
using LLama;
using LLama.Common;
using LLama.Exceptions;

namespace GimmeCapture.Services.Translation;

public sealed class LlamaSharpTranslationEngine : ITranslationEngine, IDisposable
{
    private const string TranslationPromptCacheVersion = "prompt-v4";

    public TranslationEngine EngineType => TranslationEngine.LlamaSharp;

    private readonly AIResourceService _aiResourceService;
    private readonly AppSettingsService _settingsService;
    private readonly ITranslationCache _cache;

    private LLamaWeights? _weights;
    private LLamaContext? _context;
    private InteractiveExecutor? _executor;
    private string? _loadedModelPath;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly SemaphoreSlim _inferLock = new(1, 1);

    internal bool IsModelLoaded => _executor != null && _context != null && _weights != null;

    public LlamaSharpTranslationEngine(
        AIResourceService aiResourceService,
        AppSettingsService settingsService,
        ITranslationCache cache)
    {
        _aiResourceService = aiResourceService ?? throw new ArgumentNullException(nameof(aiResourceService));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    public async Task<string> TranslateAsync(string text, OCRLanguage sourceLang, TranslationLanguage targetLang, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var cacheKey = $"{TranslationPromptCacheVersion}|{_cache.BuildKey(EngineType, sourceLang, targetLang, text)}";
        if (_cache.TryGet(cacheKey, out var cached))
        {
            return cached;
        }

        await EnsureLoadedAsync(ct);
        if (_executor == null)
        {
            return string.Empty;
        }

        string targetLangName = GetTargetLanguageName(targetLang);
        string sourceLangName = ResolveSourceLanguageForPrompt(text, sourceLang);
        string prompt = BuildTranslationPrompt(text, sourceLangName, targetLangName, targetLang);
        string result = await TranslateWithPromptAsync(prompt, text, ct);

        if (ShouldRetryWithMinimalPrompt(text, result, targetLang))
        {
            string retryPrompt = BuildMinimalRetryPrompt(text, sourceLangName, targetLangName, targetLang);
            string retried = await TranslateWithPromptAsync(retryPrompt, text, ct);
            if (!string.IsNullOrWhiteSpace(retried))
            {
                result = retried;
            }
        }

        if (ShouldCacheTranslation(text, result, targetLang))
        {
            _cache.Set(cacheKey, result);
        }

        return result;
    }

    private async Task<string> TranslateWithPromptAsync(string prompt, string sourceText, CancellationToken ct)
    {
        if (_executor == null)
        {
            return string.Empty;
        }

        var inference = new InferenceParams
        {
            MaxTokens = EstimateMaxTokens(sourceText),
            AntiPrompts = new[] { "User:", "Assistant:" },
            OverflowStrategy = ContextOverflowStrategy.TruncateAndReprefill
        };

        var sb = new StringBuilder();
        await _inferLock.WaitAsync(ct);
        try
        {
            var chat = new ChatSession(_executor);
            await foreach (var token in chat.ChatAsync(
                               new ChatHistory.Message(AuthorRole.User, prompt),
                               inference,
                               ct))
            {
                sb.Append(token);
            }
        }
        finally
        {
            _inferLock.Release();
        }

        return CleanupTranslationResult(sb.ToString());
    }

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        string modelPath = _aiResourceService.GetSelectedLlamaModelPath();
        if (string.IsNullOrWhiteSpace(modelPath) || !System.IO.File.Exists(modelPath))
        {
            throw new InvalidOperationException("No GGUF model available. Please download one from Translation settings.");
        }

        if (_executor != null && string.Equals(_loadedModelPath, modelPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await _loadLock.WaitAsync(ct);
        try
        {
            if (_executor != null && string.Equals(_loadedModelPath, modelPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            DisposeModel();

            var settings = _settingsService.Settings;
            var parameters = new ModelParams(modelPath)
            {
                ContextSize = (uint)Math.Clamp(settings.LlamaContextSize, 512, 8192),
                GpuLayerCount = Math.Max(0, settings.LlamaGpuLayers)
            };

            try
            {
                _weights = await LLamaWeights.LoadFromFileAsync(parameters, ct);
                _context = _weights.CreateContext(parameters);
                _executor = new InteractiveExecutor(_context);
                _loadedModelPath = modelPath;
            }
            catch (Exception ex) when (ex is TypeInitializationException or RuntimeError)
            {
                throw new InvalidOperationException(
                    "Llama backend is not available. Install LLamaSharp.Backend.Cpu (or another matching backend) and ensure native files are present in output.",
                    ex);
            }
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private void DisposeModel()
    {
        _executor = null;
        _context?.Dispose();
        _context = null;
        _weights?.Dispose();
        _weights = null;
        _loadedModelPath = null;
    }

    internal void ReleaseModel()
    {
        DisposeModel();
    }

    public void Dispose()
    {
        DisposeModel();
        _loadLock.Dispose();
        _inferLock.Dispose();
    }

    private static string CleanupTranslationResult(string result)
    {
        if (string.IsNullOrWhiteSpace(result))
        {
            return string.Empty;
        }

        string cleaned = result.Trim();
        cleaned = ExtractFromCodeFence(cleaned);
        cleaned = TryExtractFromJson(cleaned);
        cleaned = RemoveCommonPrefixes(cleaned);

        if (cleaned.Contains("<think>", StringComparison.OrdinalIgnoreCase) &&
            cleaned.Contains("</think>", StringComparison.OrdinalIgnoreCase))
        {
            int endIndex = cleaned.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
            if (endIndex >= 0 && endIndex + 8 <= cleaned.Length)
            {
                cleaned = cleaned[(endIndex + 8)..].Trim();
            }
        }

        if (cleaned.StartsWith("\"", StringComparison.Ordinal) &&
            cleaned.EndsWith("\"", StringComparison.Ordinal) &&
            cleaned.Length > 2)
        {
            cleaned = cleaned[1..^1];
        }

        cleaned = CollapseRepeatedLines(cleaned);
        cleaned = CollapseRepeatedTokenCycles(cleaned);
        return cleaned.Trim();
    }

    private static string BuildTranslationPrompt(string text, string sourceLangName, string targetLangName, TranslationLanguage targetLang)
    {
        string targetSpecificRule = targetLang switch
        {
            TranslationLanguage.Japanese =>
                "Write natural Japanese. Translate even short labels, conjunctions, and UI words. Use kana where natural. No romaji. No explanations.",
            TranslationLanguage.Korean =>
                "Write natural Korean Hangul. No explanations. No romanization.",
            TranslationLanguage.English =>
                "Write natural English only. No explanations.",
            TranslationLanguage.TraditionalChinese =>
                "Write natural Traditional Chinese only. No explanations.",
            TranslationLanguage.SimplifiedChinese =>
                "Write natural Simplified Chinese only. No explanations.",
            _ => string.Empty
        };

        return
            $"Translate the SOURCE text from {sourceLangName} to {targetLangName}." + Environment.NewLine +
            "Reply with the translation only." + Environment.NewLine +
            "Keep the same line breaks." + Environment.NewLine +
            "Do not include the source text, notes, lists, or setup words." + Environment.NewLine +
            targetSpecificRule + Environment.NewLine +
            "SOURCE:" + Environment.NewLine +
            text + Environment.NewLine +
            "TRANSLATION:";
    }

    private static string BuildMinimalRetryPrompt(string text, string sourceLangName, string targetLangName, TranslationLanguage targetLang)
    {
        string targetRule = targetLang switch
        {
            TranslationLanguage.Japanese =>
                "Rewrite every line as natural Japanese. Translate short connectors and labels too. Use kana where natural. Do not leave Chinese unchanged when a Japanese wording exists. No romaji. No explanation.",
            TranslationLanguage.Korean =>
                "Rewrite every line as natural Korean Hangul. No romanization. No explanation.",
            TranslationLanguage.English =>
                "Rewrite every line as natural English. No explanation.",
            TranslationLanguage.TraditionalChinese =>
                "Rewrite every line as natural Traditional Chinese. No explanation.",
            TranslationLanguage.SimplifiedChinese =>
                "Rewrite every line as natural Simplified Chinese. No explanation.",
            _ => $"{targetLangName} only."
        };

        return
            $"Rewrite the SOURCE text from {sourceLangName} to {targetLangName}.{Environment.NewLine}" +
            $"{targetRule}{Environment.NewLine}" +
            "Reply with the translation only. Keep line breaks." + Environment.NewLine +
            "SOURCE:" + Environment.NewLine +
            text + Environment.NewLine +
            "TRANSLATION:";
    }

    private static bool ShouldRetryWithMinimalPrompt(string sourceText, string translated, TranslationLanguage targetLang)
    {
        if (string.IsNullOrWhiteSpace(translated))
        {
            return true;
        }

        string normalizedSource = NormalizeForRetry(sourceText);
        string normalizedTranslated = NormalizeForRetry(translated);

        bool looksInstructional = LooksLikeInstructionalOutput(translated);
        bool unchanged = normalizedSource.Length > 0
            && string.Equals(normalizedSource, normalizedTranslated, StringComparison.Ordinal);
        if (looksInstructional || unchanged)
        {
            return true;
        }

        if (targetLang == TranslationLanguage.Japanese)
        {
            bool sourceLooksChinese = ContainsCjk(sourceText) && !ContainsJapaneseKana(sourceText);
            bool cjkOnlyOutput = ContainsCjk(translated) && !ContainsJapaneseKana(translated) && !ContainsLatinLetter(translated);
            bool tooShortOrTooSimilar = normalizedTranslated.Length <= Math.Max(2, normalizedSource.Length)
                || (normalizedSource.Length > 0
                    && (normalizedTranslated.Contains(normalizedSource, StringComparison.Ordinal)
                        || normalizedSource.Contains(normalizedTranslated, StringComparison.Ordinal)));

            return sourceLooksChinese && cjkOnlyOutput && tooShortOrTooSimilar;
        }

        return false;
    }

    private static bool ShouldCacheTranslation(string sourceText, string translated, TranslationLanguage targetLang)
    {
        if (string.IsNullOrWhiteSpace(translated))
        {
            return false;
        }

        string trimmed = translated.Trim();
        if (LooksLikeInstructionalOutput(trimmed))
        {
            return false;
        }

        string collapsedLines = CollapseRepeatedLines(trimmed);
        string collapsedTokens = CollapseRepeatedTokenCycles(collapsedLines);
        if (collapsedTokens.Length > 0 && collapsedTokens.Length + 8 < trimmed.Length)
        {
            return false;
        }

        return !ShouldRetryWithMinimalPrompt(sourceText, trimmed, targetLang);
    }

    private static int EstimateMaxTokens(string text)
    {
        int length = string.IsNullOrWhiteSpace(text) ? 0 : text.Trim().Length;
        if (length <= 0)
        {
            return 48;
        }

        return Math.Clamp(length * 4, 24, 192);
    }

    private static string CollapseRepeatedLines(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string[] lines = value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length <= 1)
        {
            return value.Trim();
        }

        var kept = new StringBuilder();
        var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            string trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || !seen.Add(trimmed))
            {
                continue;
            }

            if (kept.Length > 0)
            {
                kept.AppendLine();
            }

            kept.Append(trimmed);
        }

        return kept.Length == 0 ? string.Empty : kept.ToString().Trim();
    }

    private static string CollapseRepeatedTokenCycles(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string trimmed = value.Trim();
        string[] tokens = Regex.Split(trimmed, @"[\s,\u3001\uFF0C;\uFF1B/|]+")
            .Where(static token => !string.IsNullOrWhiteSpace(token))
            .ToArray();
        if (tokens.Length < 4)
        {
            return trimmed;
        }

        for (int cycleLength = 1; cycleLength <= Math.Min(4, tokens.Length / 2); cycleLength++)
        {
            if (!IsMostlyRepeatedTokenCycle(tokens, cycleLength))
            {
                continue;
            }

            return string.Join("\u3001", tokens.Take(cycleLength).Distinct(StringComparer.Ordinal)).Trim();
        }

        return trimmed;
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

    private static string ExtractFromCodeFence(string value)
    {
        if (!value.StartsWith("```", StringComparison.Ordinal))
        {
            return value;
        }

        var match = Regex.Match(value, @"^```[a-zA-Z0-9_-]*\s*(?<body>[\s\S]*?)\s*```$");
        return match.Success ? match.Groups["body"].Value.Trim() : value;
    }

    private static string TryExtractFromJson(string value)
    {
        if (!(value.StartsWith("{", StringComparison.Ordinal) && value.EndsWith("}", StringComparison.Ordinal)))
        {
            return value;
        }

        try
        {
            using var doc = JsonDocument.Parse(value);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("translation", out var tr) && tr.ValueKind == JsonValueKind.String)
                {
                    return tr.GetString()?.Trim() ?? value;
                }

                if (doc.RootElement.TryGetProperty("translated", out var translated) && translated.ValueKind == JsonValueKind.String)
                {
                    return translated.GetString()?.Trim() ?? value;
                }

                if (doc.RootElement.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String)
                {
                    return result.GetString()?.Trim() ?? value;
                }
            }
        }
        catch
        {
        }

        return value;
    }

    private static string RemoveCommonPrefixes(string value)
    {
        string cleaned = value.Trim();
        string[] prefixes =
        {
            "Translation:",
            "Translated text:",
            "Output:",
            "Result:",
            "TRANSLATION:",
            "Translation only:",
            "\"translation\":",
            "'translation':",
            "translation:"
        };

        foreach (string prefix in prefixes)
        {
            if (cleaned.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned[prefix.Length..].Trim();
            }
        }

        return cleaned;
    }

    private static string GetTargetLanguageName(TranslationLanguage lang) => lang switch
    {
        TranslationLanguage.TraditionalChinese => "Traditional Chinese",
        TranslationLanguage.SimplifiedChinese => "Simplified Chinese",
        TranslationLanguage.English => "English",
        TranslationLanguage.Japanese => "Japanese",
        TranslationLanguage.Korean => "Korean",
        _ => "Traditional Chinese"
    };

    private static string ResolveSourceLanguageForPrompt(string text, OCRLanguage lang) => lang switch
    {
        OCRLanguage.TraditionalChinese => "Traditional Chinese",
        OCRLanguage.SimplifiedChinese => "Simplified Chinese",
        OCRLanguage.Japanese => "Japanese",
        OCRLanguage.Korean => "Korean",
        OCRLanguage.English => "English",
        _ => text.Any(c => c >= 0x3040 && c <= 0x30FF) ? "Japanese" :
             text.Any(c => c >= 0xAC00 && c <= 0xD7AF) ? "Korean" :
             text.Any(c => c >= 0x4E00 && c <= 0x9FFF) ? "Chinese" : "English"
    };

    private static bool LooksLikeInstructionalOutput(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string normalized = NormalizeForRetry(value);
        return value.Contains("Translate", StringComparison.OrdinalIgnoreCase)
            || value.Contains("target language", StringComparison.OrdinalIgnoreCase)
            || value.Contains("SOURCE:", StringComparison.OrdinalIgnoreCase)
            || value.Contains("TRANSLATION:", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("replywiththetranslationonly", StringComparison.Ordinal)
            || normalized.Contains("keeplinebreaks", StringComparison.Ordinal)
            || normalized.Contains("donotincludethesourcetext", StringComparison.Ordinal);
    }

    private static string NormalizeForRetry(string value)
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

    private static bool ContainsJapaneseKana(string text) =>
        !string.IsNullOrWhiteSpace(text)
        && text.Any(static c => c is >= '\u3040' and <= '\u30FF');

    private static bool ContainsLatinLetter(string text) =>
        !string.IsNullOrWhiteSpace(text)
        && text.Any(static c => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'));

    private static bool ContainsCjk(string text) =>
        !string.IsNullOrWhiteSpace(text)
        && text.Any(static c => c >= '\u4E00' && c <= '\u9FFF');
}
