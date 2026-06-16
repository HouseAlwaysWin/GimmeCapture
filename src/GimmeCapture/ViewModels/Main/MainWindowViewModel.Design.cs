using System;
using System.IO;
using System.Threading.Tasks;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.AI;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Media;

namespace GimmeCapture.ViewModels.Main;

public partial class MainWindowViewModel
{
    private static MainWindowViewModelDependencies CreateDesignDependencies()
    {
        var baseDataDirectory = Path.Combine(
            Path.GetTempPath(),
            "GimmeCapture.Design",
            nameof(MainWindowViewModel));
        var settingsService = new AppSettingsService(baseDataDirectory);
        var hotkeyService = new NoOpGlobalHotkeyService();
        var globalHotkeySettingsCoordinator = new NoOpGlobalHotkeySettingsCoordinator();
        var startupRegistrationService = new NoOpStartupRegistrationService();
        var settingsSaveCoordinatorFactory = new NoOpSettingsSaveCoordinatorFactory();
        var settingsPersistenceService = new MainWindowSettingsPersistenceService();
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
            new NoOpWindowManager(),
            new NoOpThemeResourceService(),
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
            new ResourceQueueService());
    }

    private sealed class NoOpWindowManager : IWindowManager
    {
        public Avalonia.Controls.Window? GetMainWindow() => null;
        public Avalonia.Controls.Window? GetActiveWindow() => null;
        public Avalonia.Controls.Window? FindWindowByDataContext(object dataContext) => null;
        public TWindow? FindWindowOfType<TWindow>() where TWindow : Avalonia.Controls.Window => null;
        public TWindow? GetActiveWindowOfType<TWindow>() where TWindow : Avalonia.Controls.Window => null;
        public TViewModel? GetWindowDataContext<TWindow, TViewModel>()
            where TWindow : Avalonia.Controls.Window
            where TViewModel : class => null;
    }

    private sealed class NoOpThemeResourceService : IThemeResourceService
    {
        public void UpdateThemeColors(Avalonia.Media.Color accentColor, Avalonia.Media.Color deepColor)
        {
        }
    }

    private sealed class NoOpGlobalHotkeyService : IGlobalHotkeyService
    {
        public Action<int>? OnHotkeyPressed { get; set; }
        public Action<int, string, int>? OnHotkeyRegistrationFailed { get; set; }
        public Action? OnElevatedWindowFocused { get; set; }
        public void Initialize(Avalonia.Controls.Window window) { }
        public void Register(int id, string hotkey) { }
        public void Unregister(int id) { }
        public void SuspendAll() { }
        public void ResumeAll() { }
        public void Dispose() { }
    }

    private sealed class NoOpGlobalHotkeySettingsCoordinator : IGlobalHotkeySettingsCoordinator
    {
        public void RegisterGlobalHotkey(int id, string hotkey) { }
    }

    private sealed class NoOpStartupRegistrationService : IStartupRegistrationService
    {
        public void SetStartup(bool runOnStartup) { }
        public bool IsRegistered() => false;
    }

    private sealed class NoOpSettingsSaveCoordinatorFactory : ISettingsSaveCoordinatorFactory
    {
        public ISettingsSaveCoordinator Create(Func<Task<bool>> saveAsync) => new NoOpSettingsSaveCoordinator();
    }

    private sealed class NoOpSettingsSaveCoordinator : ISettingsSaveCoordinator
    {
        public void RequestSave() { }
    }
}
