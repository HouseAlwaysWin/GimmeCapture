using System.Threading.Tasks;
using GimmeCapture.Models;

namespace GimmeCapture.Services.Core.Infrastructure;

/// <summary>
/// The persisted application settings plus their load/save lifecycle. Extracted so services and view
/// models depend on this abstraction (and tests can substitute it) instead of the concrete
/// <see cref="AppSettingsService"/>. Construction stays concrete (composition root / tests); everything
/// that only reads or persists settings takes the interface.
/// </summary>
public interface IAppSettingsService
{
    /// <summary>The live settings object (mutated in place, then persisted via <see cref="SaveAsync"/>).</summary>
    AppSettings Settings { get; }

    /// <summary>Directory the config + per-version state live under.</summary>
    string BaseDataDirectory { get; }

    Task LoadAsync();
    void LoadSync();
    Task SaveAsync();
    void UpdateSettings(AppSettings source);
    void DebugLog(string message);
}
