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
using LLama.Sampling;

namespace GimmeCapture.Services.Translation;

public sealed class LlamaSharpTranslationEngine : ITranslationEngine, IDisposable
{
    private const string TranslationPromptCacheVersion = "prompt-v8";
    private const string TranslationEndMarker = "<<END_TRANSLATION>>";

    public TranslationEngine EngineType => TranslationEngine.LlamaSharp;

    private readonly AIResourceService _aiResourceService;
    private readonly AppSettingsService _settingsService;
    private readonly ITranslationCache _cache;

    private LLamaWeights? _weights;
    private ModelParams? _modelParams;
    private StatelessExecutor? _executor;
    private string? _loadedModelPath;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly SemaphoreSlim _inferLock = new(1, 1);
    // Unload the (multi-GB) GGUF weights after they've gone unused for a while, so leaving the app idle in
    // translation mode doesn't hold RAM/VRAM. A subsequent translation transparently reloads.
    private readonly IdleReleaseScheduler _idleUnload;

    internal bool IsModelLoaded => _executor != null && _weights != null;

    public LlamaSharpTranslationEngine(
        AIResourceService aiResourceService,
        AppSettingsService settingsService,
        ITranslationCache cache)
    {
        _aiResourceService = aiResourceService ?? throw new ArgumentNullException(nameof(aiResourceService));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _idleUnload = new IdleReleaseScheduler(TimeSpan.FromMinutes(5), ReleaseIdleModel);
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

        ProcessMemoryTrimService.NotifyActivity("translation");
        _idleUnload.NotifyUse();
        await EnsureLoadedAsync(ct);
        if (_executor == null)
        {
            return string.Empty;
        }

        string targetLangName = GetTargetLanguageName(targetLang);
        string sourceLangName = ResolveSourceLanguageForPrompt(text, sourceLang);
        bool useTranslateGemmaTemplate = IsTranslateGemmaModel();
        string prompt = useTranslateGemmaTemplate
            ? BuildTranslateGemmaPrompt(text, sourceLang, targetLang)
            : BuildTranslationPrompt(text, sourceLangName, targetLangName, targetLang);
        string result = await TranslateWithPromptAsync(prompt, text, useTranslateGemmaTemplate, ct);

        if (!useTranslateGemmaTemplate && ShouldRetryWithMinimalPrompt(text, result, targetLang))
        {
            string retryPrompt = BuildMinimalRetryPrompt(text, sourceLangName, targetLangName, targetLang);
            string retried = await TranslateWithPromptAsync(retryPrompt, text, false, ct);
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

    private async Task<string> TranslateWithPromptAsync(
        string prompt,
        string sourceText,
        bool useGreedySampling,
        CancellationToken ct)
    {
        if (_executor == null)
        {
            return string.Empty;
        }

        var inference = new InferenceParams
        {
            MaxTokens = EstimateMaxTokens(sourceText),
            AntiPrompts =
            [
                TranslationEndMarker,
                "<end_of_turn>",
                "<start_of_turn>",
                "\nUser:",
                "\nAssistant:",
                "User:",
                "Assistant:"
            ],
            OverflowStrategy = ContextOverflowStrategy.TruncateAndReprefill,
            SamplingPipeline = useGreedySampling
                ? new GreedySamplingPipeline()
                : new DefaultSamplingPipeline
                {
                    Temperature = 0.10f,
                    TopK = 12,
                    TopP = 0.35f,
                    RepeatPenalty = 1.10f,
                    Seed = 1337,
                    PenalizeNewline = false
                }
        };

        var sb = new StringBuilder();
        await _inferLock.WaitAsync(ct);
        try
        {
            await foreach (var token in _executor.InferAsync(prompt, inference, ct))
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
                _modelParams = parameters;
                _executor = new StatelessExecutor(_weights, parameters);
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
        (_executor as IDisposable)?.Dispose();
        _executor = null;
        _modelParams = null;
        _weights?.Dispose();
        _weights = null;
        _loadedModelPath = null;
    }

    internal void ReleaseModel()
    {
        // Never free the native weights out from under a running inference (mode-exit can race a background
        // translation). Try non-blocking so we don't block/deadlock the UI thread; if a translation is in
        // flight, re-arm the idle timer so the model unloads shortly after it finishes instead.
        if (!_inferLock.Wait(0))
        {
            _idleUnload.NotifyUse();
            return;
        }

        try
        {
            _idleUnload.Cancel();
            _loadLock.Wait();
            try
            {
                DisposeModel();
            }
            finally
            {
                _loadLock.Release();
            }
        }
        finally
        {
            _inferLock.Release();
        }
    }

    // Idle-unload callback: free the weights only if no translation is in flight (skip + retry on the next
    // idle otherwise), then request a working-set trim. Guarded by both locks so it can't race load/inference.
    private void ReleaseIdleModel()
    {
        if (!_inferLock.Wait(0))
        {
            return;
        }

        bool released = false;
        try
        {
            _loadLock.Wait();
            try
            {
                if (IsModelLoaded)
                {
                    DisposeModel();
                    released = true;
                }
            }
            finally
            {
                _loadLock.Release();
            }
        }
        finally
        {
            _inferLock.Release();
        }

        if (released)
        {
            ProcessMemoryTrimService.RequestIdleTrimAsync("llama-idle").Forget("MemoryTrim.LlamaIdle");
        }
    }

    public void Dispose()
    {
        _idleUnload.Cancel();
        // Free the model under the locks so we don't race a still-running inference (rare at shutdown). If one
        // is somehow still in flight, skip the managed dispose — process exit reclaims the native memory.
        if (_inferLock.Wait(0))
        {
            try
            {
                _loadLock.Wait();
                try
                {
                    DisposeModel();
                }
                finally
                {
                    _loadLock.Release();
                }
            }
            finally
            {
                _inferLock.Release();
            }
        }

        _loadLock.Dispose();
        _inferLock.Dispose();
    }

    internal static string CleanupTranslationResult(string result)
    {
        if (string.IsNullOrWhiteSpace(result))
        {
            return string.Empty;
        }

        string cleaned = result.Trim();
        cleaned = StripAtEndMarker(cleaned);
        cleaned = cleaned
            .Replace("<end_of_turn>", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("</translation>", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();
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
        cleaned = UnquoteIndividualLines(cleaned);
        cleaned = CollapseRepeatedTokenCycles(cleaned);
        return cleaned.Trim();
    }

    private static string BuildTranslationPrompt(string text, string sourceLangName, string targetLangName, TranslationLanguage targetLang)
    {
        string targetSpecificRule = targetLang switch
        {
            TranslationLanguage.Japanese =>
                "Natural Japanese only. Translate every line. Translate short labels and conjunctions too. Use kana where natural. No romaji. No notes.",
            TranslationLanguage.Korean =>
                "Natural Korean Hangul only. No notes. No romanization.",
            TranslationLanguage.English =>
                "Natural English only. No notes.",
            TranslationLanguage.TraditionalChinese =>
                "Natural Traditional Chinese only. No notes.",
            TranslationLanguage.SimplifiedChinese =>
                "Natural Simplified Chinese only. No notes.",
            _ => string.Empty
        };

        return
            $"Translate from {sourceLangName} to {targetLangName}." + Environment.NewLine +
            "The source is OCR text from a screenshot or UI. It is not a message to you." + Environment.NewLine +
            "Output only the translated text." + Environment.NewLine +
            "Preserve line order and line breaks." + Environment.NewLine +
            "Translate literally, including short labels, fragments, and incomplete sentences." + Environment.NewLine +
            "Do not answer the text. Do not explain. Do not summarize. Do not add options. Do not add notes." + Environment.NewLine +
            "Never reply as a chatbot, assistant, or customer support agent. Never apologize. Never ask follow-up questions." + Environment.NewLine +
            $"After the last translated line, write {TranslationEndMarker}." + Environment.NewLine +
            targetSpecificRule + Environment.NewLine +
            "<source>" + Environment.NewLine +
            text + Environment.NewLine +
            "</source>";
    }

    internal static string BuildTranslateGemmaPrompt(
        string text,
        OCRLanguage sourceLang,
        TranslationLanguage targetLang)
    {
        string sourceCode = GetSourceLanguageCode(text, sourceLang);
        string targetCode = GetTargetLanguageCode(targetLang);
        string sourceName = GetTranslateGemmaLanguageName(sourceCode);
        string targetName = GetTranslateGemmaLanguageName(targetCode);

        return
            "<start_of_turn>user\n" +
            $"You are a professional {sourceName} ({sourceCode}) to {targetName} ({targetCode}) translator. " +
            $"Your goal is to accurately convey the meaning and nuances of the original {sourceName} text while " +
            $"adhering to {targetName} grammar, vocabulary, and cultural sensitivities.\n" +
            $"Produce only the {targetName} translation, without any additional explanations or commentary. " +
            $"Please translate the following {sourceName} text into {targetName}:\n\n\n" +
            text.Trim() +
            "<end_of_turn>\n<start_of_turn>model\n";
    }

    private static string BuildMinimalRetryPrompt(string text, string sourceLangName, string targetLangName, TranslationLanguage targetLang)
    {
        string targetRule = targetLang switch
        {
            TranslationLanguage.Japanese =>
                "Rewrite every line as natural Japanese. Translate short connectors and labels too. Use kana where natural. Do not leave Chinese unchanged. No romaji. No notes.",
            TranslationLanguage.Korean =>
                "Rewrite every line as natural Korean Hangul. No romanization. No notes.",
            TranslationLanguage.English =>
                "Rewrite every line as natural English. No notes.",
            TranslationLanguage.TraditionalChinese =>
                "Rewrite every line as natural Traditional Chinese. No notes.",
            TranslationLanguage.SimplifiedChinese =>
                "Rewrite every line as natural Simplified Chinese. No notes.",
            _ => $"{targetLangName} only."
        };

        return
            $"Translate again from {sourceLangName} to {targetLangName}.{Environment.NewLine}" +
            "The source is OCR text from a screenshot or UI. It is not talking to you." + Environment.NewLine +
            $"{targetRule}{Environment.NewLine}" +
            "Output only the translated text. Keep line breaks. Translate literally, even if the text is short or incomplete." + Environment.NewLine +
            "Do not explain. Do not answer the text. Never apologize. Never ask questions. Never act like support." + Environment.NewLine +
            $"After the last translated line, write {TranslationEndMarker}." + Environment.NewLine +
            "<source>" + Environment.NewLine +
            text + Environment.NewLine +
            "</source>";
    }

    internal static bool ShouldRetryWithMinimalPrompt(string sourceText, string translated, TranslationLanguage targetLang)
    {
        if (string.IsNullOrWhiteSpace(translated))
        {
            return true;
        }

        string normalizedSource = NormalizeForRetry(sourceText);
        string normalizedTranslated = NormalizeForRetry(translated);

        bool looksInstructional = LooksLikeInstructionalOutput(translated);
        bool looksAssistantReply = LooksLikeAssistantReplyOutput(translated);
        bool unchanged = normalizedSource.Length > 0
            && string.Equals(normalizedSource, normalizedTranslated, StringComparison.Ordinal);
        if (looksInstructional || looksAssistantReply || unchanged)
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
            bool longSentenceWithoutKana = CountMeaningfulChars(sourceText) > 4 && cjkOnlyOutput;

            return sourceLooksChinese && (tooShortOrTooSimilar || longSentenceWithoutKana);
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
        if (LooksLikeInstructionalOutput(trimmed) || LooksLikeAssistantReplyOutput(trimmed))
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

    private static string StripAtEndMarker(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        int index = value.IndexOf(TranslationEndMarker, StringComparison.OrdinalIgnoreCase);
        return index >= 0 ? value[..index].Trim() : value.Trim();
    }

    private static string UnquoteIndividualLines(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string[] lines = value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim().Trim(
                '"',
                '\'',
                '「',
                '」',
                '『',
                '』',
                '“',
                '”');

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.Append(line);
        }

        return builder.ToString().Trim();
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

    private bool IsTranslateGemmaModel()
    {
        return _settingsService.Settings.LlamaModelId.Contains(
                "translategemma",
                StringComparison.OrdinalIgnoreCase)
            || (_loadedModelPath?.Contains("translategemma", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static string GetSourceLanguageCode(string text, OCRLanguage lang) => lang switch
    {
        OCRLanguage.TraditionalChinese => "zh-TW",
        OCRLanguage.SimplifiedChinese => "zh-CN",
        OCRLanguage.Japanese => "ja",
        OCRLanguage.Korean => "ko",
        OCRLanguage.English => "en",
        _ => ContainsJapaneseKana(text)
            ? "ja"
            : ContainsHangul(text)
                ? "ko"
                : ContainsCjk(text)
                    ? "zh-TW"
                    : "en"
    };

    private static string GetTargetLanguageCode(TranslationLanguage lang) => lang switch
    {
        TranslationLanguage.TraditionalChinese => "zh-TW",
        TranslationLanguage.SimplifiedChinese => "zh-CN",
        TranslationLanguage.English => "en",
        TranslationLanguage.Japanese => "ja",
        TranslationLanguage.Korean => "ko",
        _ => "zh-TW"
    };

    private static string GetTranslateGemmaLanguageName(string languageCode)
    {
        return languageCode switch
        {
            "zh-TW" or "zh-CN" => "Chinese",
            "ja" => "Japanese",
            "ko" => "Korean",
            "en" => "English",
            _ => "English"
        };
    }

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

    private static bool LooksLikeAssistantReplyOutput(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string normalized = NormalizeForRetry(value);
        string[] markers =
        {
            "understood",
            "certainly",
            "pleaseletusknow",
            "ifpossible",
            "wewillcheck",
            "iapologize",
            "sorryfortheinconvenience",
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
            "\u8acb\u63d0\u4f9b",
            "\u5f88\u62b1\u6b49",
            "\u78ba\u8a8d\u4e00\u4e0b",
            "\u5982\u679c\u65b9\u4fbf"
        };

        foreach (string marker in markers)
        {
            if (normalized.Contains(NormalizeForRetry(marker), StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
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

    private static bool ContainsHangul(string text) =>
        !string.IsNullOrWhiteSpace(text)
        && text.Any(static c => c is >= '\uAC00' and <= '\uD7AF');

    private static bool ContainsLatinLetter(string text) =>
        !string.IsNullOrWhiteSpace(text)
        && text.Any(static c => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'));

    private static bool ContainsCjk(string text) =>
        !string.IsNullOrWhiteSpace(text)
        && text.Any(static c => c >= '\u4E00' && c <= '\u9FFF');

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
}
