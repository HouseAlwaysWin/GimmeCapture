using System;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.AI;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Services.Translation;

public sealed class TranslationSessionServiceFactory : ITranslationSessionServiceFactory
{
    public ITranslationSessionService Create(
        IAppSettingsService settingsService,
        AIResourceService aiResourceService,
        OcrRuntimeService ocrRuntimeService)
    {
        ArgumentNullException.ThrowIfNull(settingsService);
        ArgumentNullException.ThrowIfNull(aiResourceService);
        ArgumentNullException.ThrowIfNull(ocrRuntimeService);

        return new TranslationSessionService(aiResourceService, settingsService, ocrRuntimeService);
    }
}
