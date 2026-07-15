using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.AI;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Media;
using GimmeCapture.Services.Platforms.Avalonia;
using GimmeCapture.Services.Platforms.Desktop;
using GimmeCapture.Services.Platforms.Linux;
#if WINDOWS
using GimmeCapture.Services.Platforms.Windows;
#endif
using GimmeCapture.ViewModels.Main;
using System;

namespace GimmeCapture.Composition;

public static class MainWindowViewModelDependenciesFactory
{
    // The optional platform-service parameters let the design-time path (MainWindowViewModel.Design.cs) reuse this
    // one real graph while substituting no-op platform services, instead of re-declaring the whole ~14-service
    // wiring (which drifted). Runtime callers omit them and get the real platform services.
    public static MainWindowViewModelDependencies CreateDefault(
        IAppSettingsService? existingSettingsService = null,
        IStartupRegistrationService? existingStartupRegistrationService = null,
        IMainWindowSettingsPersistenceService? existingSettingsPersistenceService = null,
        IWindowManager? windowManager = null,
        IThemeResourceService? themeResourceService = null,
        IGlobalHotkeyService? hotkeyService = null,
        IGlobalHotkeySettingsCoordinator? globalHotkeySettingsCoordinator = null,
        ISettingsSaveCoordinatorFactory? settingsSaveCoordinatorFactory = null)
    {
        var settingsService = existingSettingsService ?? new AppSettingsService();
        windowManager ??= new AvaloniaWindowManager();
        themeResourceService ??= new AvaloniaThemeResourceService();
        // Global hotkeys are Windows-only today; other platforms get a no-op service until an X11 /
        // portal binding lands (docs/LINUX_PORT_FEASIBILITY.md, Phase 2).
        hotkeyService ??=
#if WINDOWS
            OperatingSystem.IsWindows()
            ? new WindowsGlobalHotkeyService()
            : new LinuxGlobalHotkeyService();
#else
            new LinuxGlobalHotkeyService();
#endif
        globalHotkeySettingsCoordinator ??= new GlobalHotkeySettingsCoordinator(hotkeyService);
        var startupRegistrationService = existingStartupRegistrationService ?? CreateStartupRegistrationService();
        settingsSaveCoordinatorFactory ??= new DebouncedSettingsSaveCoordinatorFactory();
        var settingsPersistenceService = existingSettingsPersistenceService ?? new MainWindowSettingsPersistenceService();
        var hotkeyMappingService = new HotkeyMappingService();
        var hotkeyRouterService = new HotkeyRouterService();
        var aiPathService = new AIPathService(settingsService);
        var nativeResolverService = new NativeResolverService(aiPathService);
        var aiModelDownloader = new AIModelDownloader();
        var aiModelCatalog = new AIModelCatalog();
        var resourceQueue = new ResourceQueueService();
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
        var translationResultLayer = new TranslationResultLayerService();

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
            resourceQueue,
            translationResultLayer);
    }

    internal static IStartupRegistrationService CreateStartupRegistrationService()
    {
        // Windows writes an HKCU Run key; other platforms get a no-op until XDG autostart lands.
#if WINDOWS
        return OperatingSystem.IsWindows()
            ? new WindowsStartupRegistrationService()
            : new LinuxStartupRegistrationService();
#else
        return new LinuxStartupRegistrationService();
#endif
    }
}
