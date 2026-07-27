using GimmeCapture.Services.Core.AI;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.OCR;

namespace GimmeCapture.Services.Translation;

public interface ITranslationSessionServiceFactory
{
    /// <summary>
    /// <paramref name="scriptDetector"/> is expected to be the snip session's shared detector, so translate mode and
    /// quick OCR agree on the language and only pay for probing it once per session.
    /// </summary>
    ITranslationSessionService Create(
        IAppSettingsService settingsService,
        AIResourceService aiResourceService,
        OcrRuntimeService ocrRuntimeService,
        IOcrScriptDetector scriptDetector);
}
