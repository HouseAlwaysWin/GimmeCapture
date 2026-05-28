using System;
using System.IO;
using GimmeCapture.Services.Core.AI;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Media;
using GimmeCapture.Services.Platforms.Desktop;
using GimmeCapture.Services.Platforms.Windows;
using GimmeCapture.ViewModels.Main;

namespace GimmeCapture.Tests.ViewModels;

public class MainWindowViewModelConstructionTests
{
    [Fact]
    public void Constructor_Uses_Injected_SharedServices()
    {
        var baseDataDirectory = Path.Combine(
            Path.GetTempPath(),
            "GimmeCapture.Tests",
            nameof(MainWindowViewModelConstructionTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDataDirectory);

        var settingsService = new AppSettingsService(baseDataDirectory);
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
        var updateService = new UpdateService("1.2.3");
        var aiPathService = new AIPathService(settingsService);
        var nativeResolverService = new NativeResolverService(aiPathService);
        var aiResourceService = new AIResourceService(settingsService, aiPathService, nativeResolverService, new AIModelDownloader());
        var sam2RuntimeService = new SAM2RuntimeService(aiPathService, nativeResolverService);
        var ocrRuntimeService = new OcrRuntimeService(aiResourceService);
        var resourceQueue = ResourceQueueService.Instance;

        var dependencies = new MainWindowViewModelDependencies(
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
            resourceQueue);

        var viewModel = new MainWindowViewModel(dependencies);

        Assert.Same(settingsService, viewModel.AppSettingsService);
        Assert.Same(hotkeyService, viewModel.HotkeyService);
        Assert.Same(recordingService, viewModel.RecordingService);
        Assert.Same(updateService, viewModel.UpdateService);
        Assert.Same(aiPathService, viewModel.AIPathService);
        Assert.Same(aiResourceService, viewModel.AIResourceService);
        Assert.Same(sam2RuntimeService, viewModel.SAM2RuntimeService);
        Assert.Same(ocrRuntimeService, viewModel.OcrRuntimeService);
        Assert.Same(resourceQueue, viewModel.ResourceQueue);
    }
}
