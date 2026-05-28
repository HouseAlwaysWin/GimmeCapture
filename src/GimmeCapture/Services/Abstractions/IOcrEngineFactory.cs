using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Interfaces;

namespace GimmeCapture.Services.Core.AI;

public interface IOcrEngineFactory
{
    IOCREngine Create(
        AIResourceService aiResourceService,
        AppSettingsService settingsService,
        OcrRuntimeService ocrRuntimeService);
}
