using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GimmeCapture.Services.Translation;

public interface IOllamaApiClient
{
    Task<string> GenerateAsync(string model, string prompt, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetModelsAsync(CancellationToken ct = default);
    Task<bool> IsReadyAsync(string model, CancellationToken ct = default);
}
