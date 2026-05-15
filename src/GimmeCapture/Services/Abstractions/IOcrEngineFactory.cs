using GimmeCapture.Services.Core.AI;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Interfaces;

namespace GimmeCapture.Services.OCR;

public interface IOcrEngineFactory
{
    IOCREngine Create(
        AIResourceService aiResourceService,
        AppSettingsService settingsService);
}
