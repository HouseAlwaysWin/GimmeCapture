using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.AI;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Media;

namespace GimmeCapture.ViewModels.Main;

public sealed record MainWindowViewModelDependencies(
    AppSettingsService SettingsService,
    IWindowManager WindowManager,
    IThemeResourceService ThemeResourceService,
    IGlobalHotkeyService HotkeyService,
    IGlobalHotkeySettingsCoordinator GlobalHotkeySettingsCoordinator,
    IStartupRegistrationService StartupRegistrationService,
    ISettingsSaveCoordinatorFactory SettingsSaveCoordinatorFactory,
    IMainWindowSettingsPersistenceService SettingsPersistenceService,
    HotkeyMappingService HotkeyMappingService,
    HotkeyRouterService HotkeyRouterService,
    FFmpegDownloaderService FfmpegDownloader,
    RecordingService RecordingService,
    UpdateService UpdateService,
    AIResourceService AIResourceService,
    AIPathService AIPathService,
    ResourceQueueService ResourceQueue);
