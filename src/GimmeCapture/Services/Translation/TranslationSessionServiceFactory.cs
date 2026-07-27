using System;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.AI;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.OCR;

namespace GimmeCapture.Services.Translation;

public sealed class TranslationSessionServiceFactory : ITranslationSessionServiceFactory
{
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

        return new TranslationSessionService(aiResourceService, settingsService, ocrRuntimeService, scriptDetector);
    }
}
