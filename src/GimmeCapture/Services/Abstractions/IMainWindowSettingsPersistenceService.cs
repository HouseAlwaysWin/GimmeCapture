using System.Threading.Tasks;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Services.Core.Infrastructure;

public interface IMainWindowSettingsPersistenceService
{
    Task<MainWindowSettingsSnapshot> LoadAsync(IAppSettingsService settingsService);

    Task SaveAsync(IAppSettingsService settingsService, MainWindowSettingsSnapshot snapshot);
}
