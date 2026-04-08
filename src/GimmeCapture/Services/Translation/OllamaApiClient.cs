using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Services.Translation;

public class OllamaApiClient : IOllamaApiClient
{
    private static readonly TimeSpan ModelCacheTtl = TimeSpan.FromMinutes(2);

    private readonly HttpClient _httpClient;
    private readonly AppSettingsService _settingsService;
    private readonly ITranslationExecutionPolicy _policy;
    private readonly object _modelsLock = new();
    private IReadOnlyList<string>? _cachedModels;
    private DateTime _modelsCachedAtUtc;

    public OllamaApiClient(HttpClient httpClient, AppSettingsService settingsService, ITranslationExecutionPolicy policy)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    public async Task<string> GenerateAsync(string model, string prompt, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(prompt))
        {
            return string.Empty;
        }

        return await TranslationExecutionHelper.ExecuteAsync(
            async token =>
            {
                var payload = new
                {
                    model,
                    prompt,
                    stream = false
                };

                var url = BuildGenerateUrl();
                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync(url, content, token);
                if (!response.IsSuccessStatusCode)
                {
                    System.Diagnostics.Debug.WriteLine($"[OllamaApiClient] API error: {response.StatusCode}");
                    return string.Empty;
                }

                return await response.Content.ReadAsStringAsync(token);
            },
            ct,
            _policy.OllamaGenerateTimeout,
            () => string.Empty,
            "OllamaApiClient.Generate");
    }

    public async Task<IReadOnlyList<string>> GetModelsAsync(CancellationToken ct = default)
    {
        lock (_modelsLock)
        {
            if (_cachedModels != null && DateTime.UtcNow - _modelsCachedAtUtc < ModelCacheTtl)
            {
                return _cachedModels;
            }
        }

        var names = await TranslationExecutionHelper.ExecuteAsync(
            async token =>
            {
                var response = await _httpClient.GetAsync(BuildTagsUrl(), token);
                if (!response.IsSuccessStatusCode) return Array.Empty<string>();

                var json = await response.Content.ReadAsStringAsync(token);
                using var doc = JsonDocument.Parse(json);
                var models = doc.RootElement.GetProperty("models").EnumerateArray();

                var result = new List<string>();
                foreach (var modelItem in models)
                {
                    result.Add(modelItem.GetProperty("name").GetString() ?? string.Empty);
                }

                return (IReadOnlyList<string>)result;
            },
            ct,
            _policy.OllamaTagsTimeout,
            () => Array.Empty<string>(),
            "OllamaApiClient.GetModels");

        if (names.Count == 0) return names;

        lock (_modelsLock)
        {
            _cachedModels = names;
            _modelsCachedAtUtc = DateTime.UtcNow;
        }

        return names;
    }

    public async Task<bool> IsReadyAsync(string model, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;

        var models = await GetModelsAsync(ct);
        if (models.Count == 0) return false;

        // Check for exact match or name:latest match
        return models.AsValueEnumerable().Any(m => m.Equals(model, StringComparison.OrdinalIgnoreCase) || 
                               m.Equals($"{model}:latest", StringComparison.OrdinalIgnoreCase));
    }

    private string BuildGenerateUrl()
    {
        var baseUrl = ResolveBaseUrl();
        if (baseUrl.EndsWith("/api/generate", StringComparison.OrdinalIgnoreCase)) return baseUrl;
        if (baseUrl.EndsWith("/api/chat", StringComparison.OrdinalIgnoreCase))
        {
            return baseUrl[..^"/api/chat".Length] + "/api/generate";
        }

        return baseUrl + "/api/generate";
    }

    private string BuildTagsUrl()
    {
        var generateUrl = BuildGenerateUrl();
        return generateUrl[..^"/api/generate".Length] + "/api/tags";
    }

    private string ResolveBaseUrl()
    {
        var configured = _settingsService.Settings.OllamaApiUrl;
        if (string.IsNullOrWhiteSpace(configured))
        {
            return "http://localhost:11434";
        }

        return configured.TrimEnd('/');
    }
}
