using System.Threading.Tasks;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Services.Core.Infrastructure;

public interface IMainWindowSettingsPersistenceService
{
    Task<MainWindowSettingsSnapshot> LoadAsync(AppSettingsService settingsService);

    Task SaveAsync(AppSettingsService settingsService, MainWindowSettingsSnapshot snapshot);
}
