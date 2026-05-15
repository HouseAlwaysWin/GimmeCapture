using GimmeCapture.Services.Core.AI;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Services.Abstractions;

public interface ITranslationSessionServiceFactory
{
    ITranslationSessionService Create(
        AppSettingsService settingsService,
        AIResourceService aiResourceService);
}
