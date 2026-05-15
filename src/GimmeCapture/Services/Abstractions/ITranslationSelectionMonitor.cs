using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Services.Translation;

namespace GimmeCapture.Services.Translation;

public interface ITranslationSelectionMonitor
{
    Task<IReadOnlyList<TranslationSelectionUpdate>> ProcessAsync(
        TranslationSelectionMonitorRequest request,
        CancellationToken ct = default);
}
