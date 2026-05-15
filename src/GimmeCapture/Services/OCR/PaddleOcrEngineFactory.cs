using System;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.AI;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Interfaces;

namespace GimmeCapture.Services.OCR;

public sealed class PaddleOcrEngineFactory : IOcrEngineFactory
{
    public IOCREngine Create(
        AIResourceService aiResourceService,
        AppSettingsService settingsService)
    {
        ArgumentNullException.ThrowIfNull(aiResourceService);
        ArgumentNullException.ThrowIfNull(settingsService);

        return new PaddleOCREngine(aiResourceService, settingsService);
    }
}
