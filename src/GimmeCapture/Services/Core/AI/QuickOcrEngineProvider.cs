using System;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Interfaces;

namespace GimmeCapture.Services.Core.AI;

public sealed class QuickOcrEngineProvider : IQuickOcrEngineProvider
{
    private readonly AIResourceService _aiResourceService;
    private readonly IAppSettingsService _settingsService;
    private readonly OcrRuntimeService _ocrRuntimeService;
    private readonly IOcrEngineFactory _ocrEngineFactory;

    public QuickOcrEngineProvider(
        AIResourceService aiResourceService,
        IAppSettingsService settingsService,
        OcrRuntimeService ocrRuntimeService,
        IOcrEngineFactory ocrEngineFactory)
    {
        _aiResourceService = aiResourceService ?? throw new ArgumentNullException(nameof(aiResourceService));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _ocrRuntimeService = ocrRuntimeService ?? throw new ArgumentNullException(nameof(ocrRuntimeService));
        _ocrEngineFactory = ocrEngineFactory ?? throw new ArgumentNullException(nameof(ocrEngineFactory));
    }

    public bool IsReady(OCRLanguage language) => _aiResourceService.IsOCRReady(language);

    public IOCREngine Create() =>
        _ocrEngineFactory.Create(_aiResourceService, _settingsService, _ocrRuntimeService);
}
