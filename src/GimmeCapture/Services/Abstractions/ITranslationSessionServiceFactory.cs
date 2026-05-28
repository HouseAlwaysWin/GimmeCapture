using GimmeCapture.Services.Core.AI;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Abstractions;

namespace GimmeCapture.Services.Translation;

public interface ITranslationSessionServiceFactory
{
    ITranslationSessionService Create(
        AppSettingsService settingsService,
        AIResourceService aiResourceService,
        OcrRuntimeService ocrRuntimeService);
}
