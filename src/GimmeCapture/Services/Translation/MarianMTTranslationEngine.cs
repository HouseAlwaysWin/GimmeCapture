using System;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Core;
using GimmeCapture.Services.Core.Interfaces;

namespace GimmeCapture.Services.Translation;

public class MarianMTTranslationEngine : ITranslationEngine
{
    public TranslationEngine EngineType => TranslationEngine.MarianMT;

    private readonly MarianMTService _marianMTService;

    public MarianMTTranslationEngine(MarianMTService marianMTService)
    {
        _marianMTService = marianMTService ?? throw new ArgumentNullException(nameof(marianMTService));
    }

    public async Task<string> TranslateAsync(string text, OCRLanguage sourceLang, TranslationLanguage targetLang, CancellationToken ct = default)
    {
        return await TranslationExecutionHelper.ExecuteAsync(
            async token =>
            {
                await _marianMTService.EnsureLoadedAsync(token);
                var result = await _marianMTService.TranslateAsync(text, targetLang, sourceLang, token);
                return string.IsNullOrWhiteSpace(result) ? string.Empty : result;
            },
            ct,
            TimeSpan.FromSeconds(30),
            () => string.Empty,
            "MarianMTTranslationEngine.Translate");
    }
}
