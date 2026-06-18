using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Interfaces;

namespace GimmeCapture.Services.Translation;

internal sealed class TranslationEngineRunner
{
    private readonly IReadOnlyList<ITranslationEngine> _engines;

    public TranslationEngineRunner(IEnumerable<ITranslationEngine> engines)
    {
        ArgumentNullException.ThrowIfNull(engines);
        _engines = engines.AsValueEnumerable().ToList();
    }

    public async Task<string> TranslateSelectedAsync(
        string text,
        OCRLanguage sourceLanguage,
        TranslationLanguage targetLanguage,
        TranslationEngine selectedEngine,
        CancellationToken cancellationToken)
    {
        var engine = _engines.AsValueEnumerable()
            .FirstOrDefault(candidate => candidate.EngineType == selectedEngine);
        if (engine == null)
        {
            AppLog.Information("Translation.EngineNotFound");
            return string.Empty;
        }

        string? result = await engine.TranslateAsync(
            text,
            sourceLanguage,
            targetLanguage,
            cancellationToken);
        if (string.IsNullOrEmpty(result))
        {
            AppLog.Information("Translation.EngineReturnedEmpty");
        }

        return result ?? string.Empty;
    }

    public Task<string> TranslateWithLlamaAsync(
        string text,
        OCRLanguage sourceLanguage,
        TranslationLanguage targetLanguage,
        CancellationToken cancellationToken)
    {
        return TranslateSelectedAsync(
            text,
            sourceLanguage,
            targetLanguage,
            TranslationEngine.LlamaSharp,
            cancellationToken);
    }
}
