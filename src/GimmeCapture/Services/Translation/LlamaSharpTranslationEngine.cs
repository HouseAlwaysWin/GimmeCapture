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

        var cacheKey = _cache.BuildKey(EngineType, sourceLang, targetLang, text);
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
        string prompt = $"Translate \"{text}\" from {sourceLangName} to {targetLangName}. Output ONLY the translated text. No quotes. No explanations. No markdown. No json.";

        var inference = new InferenceParams
        {
            MaxTokens = 512,
            AntiPrompts = new[] { "User:", "Assistant:", "ユーザー:", "アシスタント:", "使用者:", "助手:" },
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

        string result = CleanupTranslationResult(sb.ToString(), text, targetLang);
        if (!string.IsNullOrWhiteSpace(result))
        {
            _cache.Set(cacheKey, result);
        }

        return result;
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

    private static string CleanupTranslationResult(string result, string sourceText, TranslationLanguage targetLang)
    {
        if (string.IsNullOrWhiteSpace(result))
        {
            return string.Empty;
        }

        string cleaned = result.Trim();
        cleaned = ExtractFromCodeFence(cleaned);
        cleaned = TryExtractFromJson(cleaned);
        cleaned = RemoveCommonPrefixes(cleaned);
        cleaned = RemoveLeadingLabel(cleaned);
        cleaned = RemoveThinkBlock(cleaned);
        cleaned = RemoveMetaLines(cleaned, sourceText, targetLang);
        cleaned = CollapseRepeatedLines(cleaned);

        if (cleaned.StartsWith("\"", StringComparison.Ordinal) &&
            cleaned.EndsWith("\"", StringComparison.Ordinal) &&
            cleaned.Length > 2)
        {
            cleaned = cleaned[1..^1];
        }

        return cleaned.Trim();
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
        var seen = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        foreach (var rawLine in lines)
        {
            string normalized = NormalizeForComparison(rawLine);
            if (string.IsNullOrWhiteSpace(normalized) || !seen.Add(normalized))
            {
                continue;
            }

            if (kept.Length > 0)
            {
                kept.AppendLine();
            }

            kept.Append(rawLine.Trim());
        }

        return kept.Length == 0 ? string.Empty : kept.ToString().Trim();
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
            "Translated:",
            "Output:",
            "Result:",
            "Answer:",
            "Translation result:",
            "翻譯：",
            "翻譯:",
            "翻译：",
            "翻译:",
            "譯文：",
            "譯文:",
            "译文：",
            "译文:",
            "翻訳：",
            "翻訳:",
            "訳：",
            "訳:",
            "번역:",
            "Input:",
            "Text:",
            "User:",
            "Assistant:",
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

    private static string RemoveLeadingLabel(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string cleaned = value.Trim();
        cleaned = Regex.Replace(cleaned, @"^(?:translation|translated|result|output|answer|input|text)\s*[:：]\s*", string.Empty, RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"^(?:翻譯|翻译|譯文|译文|翻訳|訳|번역)\s*[:：]\s*", string.Empty, RegexOptions.IgnoreCase);
        return cleaned.Trim();
    }

    private static string RemoveThinkBlock(string value)
    {
        if (value.Contains("<think>", StringComparison.OrdinalIgnoreCase) &&
            value.Contains("</think>", StringComparison.OrdinalIgnoreCase))
        {
            int endIndex = value.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
            if (endIndex >= 0 && endIndex + 8 <= value.Length)
            {
                return value[(endIndex + 8)..].Trim();
            }
        }

        return value;
    }

    private static string RemoveMetaLines(string value, string sourceText, TranslationLanguage targetLang)
    {
        string[] lines = value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length <= 1)
        {
            return StripPromptEcho(value.Trim(), sourceText, targetLang);
        }

        var kept = new StringBuilder();
        foreach (string rawLine in lines)
        {
            string line = RemoveLeadingLabel(rawLine.Trim());
            line = StripPromptEcho(line, sourceText, targetLang);
            if (string.IsNullOrWhiteSpace(line) || IsMetaLine(line, sourceText))
            {
                continue;
            }

            if (kept.Length > 0)
            {
                kept.AppendLine();
            }

            kept.Append(line);
        }

        return kept.Length == 0 ? string.Empty : kept.ToString().Trim();
    }

    private static bool IsMetaLine(string line, string sourceText)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return true;
        }

        string trimmed = line.Trim();
        string normalizedLine = NormalizeForComparison(trimmed);
        string normalizedSource = NormalizeForComparison(sourceText);

        return trimmed.StartsWith("Input", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("Source language", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("Target language", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("You are", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("Translate the following", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("Translate \"", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("ユーザー", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("使用者", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("アシスタント", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("翻訳してください", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("翻譯成", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("Output is ONLY", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("ONLY テキスト", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("User:", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("Assistant:", StringComparison.OrdinalIgnoreCase) ||
               (!string.IsNullOrWhiteSpace(normalizedSource) &&
                normalizedLine.Length >= 8 &&
                (normalizedSource.Contains(normalizedLine, StringComparison.OrdinalIgnoreCase)
                 || normalizedLine.Contains(normalizedSource, StringComparison.OrdinalIgnoreCase)));
    }

    private static string StripPromptEcho(string value, string sourceText, TranslationLanguage targetLang)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string trimmed = value.Trim();
        string normalizedLine = NormalizeForComparison(trimmed);
        string normalizedSource = NormalizeForComparison(sourceText);

        if (!string.IsNullOrWhiteSpace(normalizedSource) &&
            normalizedLine.Length >= 8 &&
            (normalizedSource.Contains(normalizedLine, StringComparison.OrdinalIgnoreCase)
             || normalizedLine.Contains(normalizedSource, StringComparison.OrdinalIgnoreCase)))
        {
            return string.Empty;
        }

        if (targetLang is TranslationLanguage.Japanese or TranslationLanguage.TraditionalChinese or TranslationLanguage.SimplifiedChinese or TranslationLanguage.Korean)
        {
            int asciiLetters = trimmed.Count(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
            int nonAsciiLetters = trimmed.Count(c => c > 127);
            if (asciiLetters > 0 && asciiLetters >= nonAsciiLetters * 2 && normalizedLine.Length >= 8)
            {
                return string.Empty;
            }
        }

        return trimmed;
    }

    private static string NormalizeForComparison(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length);
        foreach (char ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }

        return sb.ToString();
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
}
