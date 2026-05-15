using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.AI;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Media;
using GimmeCapture.Services.Platforms.Desktop;
using GimmeCapture.Services.Platforms.Windows;

namespace GimmeCapture.Composition;

public sealed record MainWindowViewModelDependencies(
    AppSettingsService SettingsService,
    IWindowManager WindowManager,
    IThemeResourceService ThemeResourceService,
    WindowsGlobalHotkeyService HotkeyService,
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
    ResourceQueueService ResourceQueue)
{
    public static MainWindowViewModelDependencies CreateDefault()
    {
        var settingsService = new AppSettingsService();
        var windowManager = new AvaloniaWindowManager();
        var themeResourceService = new AvaloniaThemeResourceService();
        var hotkeyService = new WindowsGlobalHotkeyService();
        var globalHotkeySettingsCoordinator = new GlobalHotkeySettingsCoordinator(hotkeyService);
        var startupRegistrationService = new WindowsStartupRegistrationService();
        var settingsSaveCoordinatorFactory = new DebouncedSettingsSaveCoordinatorFactory();
        var settingsPersistenceService = new MainWindowSettingsPersistenceService();
        var hotkeyMappingService = new HotkeyMappingService();
        var hotkeyRouterService = new HotkeyRouterService();
        var ffmpegDownloader = new FFmpegDownloaderService(settingsService);
        var recordingService = new RecordingService(ffmpegDownloader, settingsService);
        var updateService = new UpdateService(AppVersionInfo.CurrentVersion);
        var aiPathService = new AIPathService(settingsService);
        var nativeResolverService = new NativeResolverService(aiPathService);
        var aiModelDownloader = new AIModelDownloader();
        var aiResourceService = new AIResourceService(settingsService, aiPathService, nativeResolverService, aiModelDownloader);

        return new MainWindowViewModelDependencies(
            settingsService,
            windowManager,
            themeResourceService,
            hotkeyService,
            globalHotkeySettingsCoordinator,
            startupRegistrationService,
            settingsSaveCoordinatorFactory,
            settingsPersistenceService,
            hotkeyMappingService,
            hotkeyRouterService,
            ffmpegDownloader,
            recordingService,
            updateService,
            aiResourceService,
            aiPathService,
            ResourceQueueService.Instance);
    }
}
