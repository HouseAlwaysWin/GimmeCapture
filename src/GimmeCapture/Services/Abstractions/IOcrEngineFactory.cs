using GimmeCapture.Services.Core.AI;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Interfaces;

namespace GimmeCapture.Services.Abstractions;

public interface IOcrEngineFactory
{
    IOCREngine Create(
        AIResourceService aiResourceService,
        AppSettingsService settingsService);
}
