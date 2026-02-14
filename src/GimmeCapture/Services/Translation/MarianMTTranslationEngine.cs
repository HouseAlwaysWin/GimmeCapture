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
        try
        {
            await _marianMTService.EnsureLoadedAsync();
            return await _marianMTService.TranslateAsync(text, targetLang, sourceLang, ct);
        }
        catch (Exception)
        {
            return text;
        }
    }
}
