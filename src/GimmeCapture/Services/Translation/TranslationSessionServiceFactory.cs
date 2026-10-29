using System;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.AI;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.OCR;

namespace GimmeCapture.Services.Translation;

public sealed class TranslationSessionServiceFactory : ITranslationSessionServiceFactory
{
    private readonly TranslationRuntimeService _translationRuntime;

    /// <param name="translationRuntime">The app's translation engine and cache, which every session shares.</param>
    public TranslationSessionServiceFactory(TranslationRuntimeService translationRuntime)
    {
        _translationRuntime = translationRuntime ?? throw new ArgumentNullException(nameof(translationRuntime));
    }

    public ITranslationSessionService Create(
        IAppSettingsService settingsService,
        AIResourceService aiResourceService,
        OcrRuntimeService ocrRuntimeService,
        IOcrScriptDetector scriptDetector)
    {
        ArgumentNullException.ThrowIfNull(settingsService);
        ArgumentNullException.ThrowIfNull(aiResourceService);
        ArgumentNullException.ThrowIfNull(ocrRuntimeService);
        ArgumentNullException.ThrowIfNull(scriptDetector);

        return new TranslationSessionService(
            aiResourceService,
            settingsService,
            ocrRuntimeService,
            scriptDetector,
            _translationRuntime.GetEngine(aiResourceService, settingsService));
    }
}
