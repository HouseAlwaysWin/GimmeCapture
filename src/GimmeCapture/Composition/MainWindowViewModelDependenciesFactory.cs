using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.AI;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Media;
using GimmeCapture.Services.Platforms.Desktop;
using GimmeCapture.Services.Platforms.Windows;
using GimmeCapture.ViewModels.Main;

namespace GimmeCapture.Composition;

public static class MainWindowViewModelDependenciesFactory
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
        var aiModelCatalog = new AIModelCatalog();
        var sam2RuntimeService = new SAM2RuntimeService(aiPathService, nativeResolverService);
        var aiResourceService = new AIResourceService(
            settingsService,
            aiPathService,
            nativeResolverService,
            aiModelDownloader,
            aiModelCatalog,
            sam2RuntimeService.UnloadModels);
        var ocrRuntimeService = new OcrRuntimeService(aiResourceService);

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
            sam2RuntimeService,
            ocrRuntimeService,
            aiPathService,
            ResourceQueueService.Instance);
    }
}
