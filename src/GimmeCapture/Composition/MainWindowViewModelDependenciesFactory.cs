using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.AI;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Media;
using GimmeCapture.Services.Platforms.Desktop;
using GimmeCapture.Services.Platforms.Windows;
using GimmeCapture.ViewModels.Main;
using System;

namespace GimmeCapture.Composition;

public static class MainWindowViewModelDependenciesFactory
{
    public static MainWindowViewModelDependencies CreateDefault(
        AppSettingsService? existingSettingsService = null,
        IStartupRegistrationService? existingStartupRegistrationService = null,
        IMainWindowSettingsPersistenceService? existingSettingsPersistenceService = null)
    {
        var settingsService = existingSettingsService ?? new AppSettingsService();
        var windowManager = new AvaloniaWindowManager();
        var themeResourceService = new AvaloniaThemeResourceService();
        var hotkeyService = new WindowsGlobalHotkeyService();
        var globalHotkeySettingsCoordinator = new GlobalHotkeySettingsCoordinator(hotkeyService);
        var startupRegistrationService = existingStartupRegistrationService ?? new WindowsStartupRegistrationService();
        var settingsSaveCoordinatorFactory = new DebouncedSettingsSaveCoordinatorFactory();
        var settingsPersistenceService = existingSettingsPersistenceService ?? new MainWindowSettingsPersistenceService();
        var hotkeyMappingService = new HotkeyMappingService();
        var hotkeyRouterService = new HotkeyRouterService();
        var aiPathService = new AIPathService(settingsService);
        var nativeResolverService = new NativeResolverService(aiPathService);
        var aiModelDownloader = new AIModelDownloader();
        var aiModelCatalog = new AIModelCatalog();
        var ffmpegDownloader = new Lazy<FFmpegDownloaderService>(() => new FFmpegDownloaderService(settingsService));
        var recordingService = new Lazy<RecordingService>(() => new RecordingService(ffmpegDownloader.Value, settingsService));
        var updateService = new Lazy<UpdateService>(() => new UpdateService(AppVersionInfo.CurrentVersion));
        var sam2RuntimeService = new Lazy<SAM2RuntimeService>(() => new SAM2RuntimeService(aiPathService, nativeResolverService));
        var aiResourceService = new Lazy<AIResourceService>(() => new AIResourceService(
            settingsService,
            aiPathService,
            nativeResolverService,
            aiModelDownloader,
            aiModelCatalog,
            sam2RuntimeService.Value.UnloadModels));
        var aiResourceOrchestrator = new Lazy<AIResourceOrchestrator>(() => aiResourceService.Value.Orchestrator);
        var ocrRuntimeService = new Lazy<OcrRuntimeService>(() => new OcrRuntimeService(aiResourceService.Value));

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
            aiModelCatalog,
            aiResourceService,
            aiResourceOrchestrator,
            sam2RuntimeService,
            ocrRuntimeService,
            aiPathService,
            ResourceQueueService.Instance);
    }
}
