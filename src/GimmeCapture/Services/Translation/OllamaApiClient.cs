using System;
using System.Collections.Generic;
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
    private readonly object _modelsLock = new();
    private IReadOnlyList<string>? _cachedModels;
    private DateTime _modelsCachedAtUtc;

    public OllamaApiClient(HttpClient httpClient, AppSettingsService settingsService)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
    }

    public async Task<string> GenerateAsync(string model, string prompt, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(prompt))
        {
            return string.Empty;
        }

        var payload = new
        {
            model,
            prompt,
            stream = false
        };

        var url = BuildGenerateUrl();
        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(url, content, ct);
        if (!response.IsSuccessStatusCode)
        {
            System.Diagnostics.Debug.WriteLine($"[OllamaApiClient] API error: {response.StatusCode}");
            return string.Empty;
        }

        return await response.Content.ReadAsStringAsync(ct);
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

        try
        {
            var response = await _httpClient.GetAsync(BuildTagsUrl(), ct);
            if (!response.IsSuccessStatusCode) return Array.Empty<string>();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var models = doc.RootElement.GetProperty("models").EnumerateArray();

            var names = new List<string>();
            foreach (var model in models)
            {
                names.Add(model.GetProperty("name").GetString() ?? string.Empty);
            }

            lock (_modelsLock)
            {
                _cachedModels = names;
                _modelsCachedAtUtc = DateTime.UtcNow;
            }

            return names;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OllamaApiClient] GetModels failed: {ex.Message}");
            return Array.Empty<string>();
        }
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
