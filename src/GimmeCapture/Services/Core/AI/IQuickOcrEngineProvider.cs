using GimmeCapture.Models;
using GimmeCapture.Services.Core.Interfaces;

namespace GimmeCapture.Services.Core.AI;

public interface IQuickOcrEngineProvider
{
    bool IsReady(OCRLanguage language);
    IOCREngine Create();
}
