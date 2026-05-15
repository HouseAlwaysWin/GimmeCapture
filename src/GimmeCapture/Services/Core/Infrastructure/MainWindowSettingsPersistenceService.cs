using System;
using System.Threading.Tasks;
using GimmeCapture.Services.Abstractions;

namespace GimmeCapture.Services.Core.Infrastructure;

public sealed class MainWindowSettingsPersistenceService : IMainWindowSettingsPersistenceService
{
    public async Task<MainWindowSettingsSnapshot> LoadAsync(AppSettingsService settingsService)
    {
        ArgumentNullException.ThrowIfNull(settingsService);

        await settingsService.LoadAsync();
        return MainWindowSettingsSnapshot.FromAppSettings(settingsService.Settings);
    }

    public async Task SaveAsync(AppSettingsService settingsService, MainWindowSettingsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(settingsService);
        ArgumentNullException.ThrowIfNull(snapshot);

        snapshot.ApplyTo(settingsService.Settings);
        await settingsService.SaveAsync();
    }
}
