using System;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Interfaces;
using GimmeCapture.Services.Core.AI;

namespace GimmeCapture.Services.OCR;

public sealed class PaddleOcrEngineFactory : IOcrEngineFactory
{
    public IOCREngine Create(
        AIResourceService aiResourceService,
        IAppSettingsService settingsService,
        OcrRuntimeService ocrRuntimeService)
    {
        ArgumentNullException.ThrowIfNull(aiResourceService);
        ArgumentNullException.ThrowIfNull(settingsService);
        ArgumentNullException.ThrowIfNull(ocrRuntimeService);

        return new PaddleOCREngine(aiResourceService, settingsService, ocrRuntimeService);
    }
}
