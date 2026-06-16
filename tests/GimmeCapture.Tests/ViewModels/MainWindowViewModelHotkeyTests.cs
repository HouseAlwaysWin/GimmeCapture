using System;
using System.IO;
using System.Threading.Tasks;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.AI;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Media;
using GimmeCapture.Services.Platforms.Desktop;
using GimmeCapture.Services.Platforms.Windows;
using GimmeCapture.ViewModels.Main;

namespace GimmeCapture.Tests.ViewModels;

public class MainWindowViewModelHotkeyTests
{
    [Fact]
    public void GlobalHotkeySetter_Registers_Even_When_Value_Is_Unchanged()
    {
        var tempDir = Path.Combine(
            Path.GetTempPath(),
            "GimmeCapture.Tests",
            nameof(MainWindowViewModelHotkeyTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var settingsService = new AppSettingsService(tempDir);
        var aiPathService = new AIPathService(settingsService);
        var nativeResolverService = new NativeResolverService(aiPathService);
        var ffmpegDownloader = new FFmpegDownloaderService(settingsService);
        var aiModelCatalog = new AIModelCatalog();
        var aiResourceService = new AIResourceService(settingsService, aiPathService, nativeResolverService, new AIModelDownloader(), aiModelCatalog);
        var sam2RuntimeService = new SAM2RuntimeService(aiPathService, nativeResolverService);
        var ocrRuntimeService = new OcrRuntimeService(aiResourceService);
        var aiResourceOrchestrator = new Lazy<AIResourceOrchestrator>(() => aiResourceService.Orchestrator);
        var hotkeyCoordinator = new CountingGlobalHotkeySettingsCoordinator();
        var dependencies = new MainWindowViewModelDependencies(
            settingsService,
            new AvaloniaWindowManager(),
            new AvaloniaThemeResourceService(),
            new WindowsGlobalHotkeyService(),
            hotkeyCoordinator,
            new NoOpStartupRegistrationService(),
            new ImmediateSettingsSaveCoordinatorFactory(),
            new MainWindowSettingsPersistenceService(),
            new HotkeyMappingService(),
            new HotkeyRouterService(),
            new Lazy<FFmpegDownloaderService>(() => ffmpegDownloader),
            new Lazy<RecordingService>(() => new RecordingService(ffmpegDownloader, settingsService)),
            new Lazy<UpdateService>(() => new UpdateService("1.2.3")),
            aiModelCatalog,
            new Lazy<AIResourceService>(() => aiResourceService),
            aiResourceOrchestrator,
            new Lazy<SAM2RuntimeService>(() => sam2RuntimeService),
            new Lazy<OcrRuntimeService>(() => ocrRuntimeService),
            aiPathService,
            new ResourceQueueService());

        var viewModel = new MainWindowViewModel(dependencies);

        int before = hotkeyCoordinator.RegisterCallCount;
        viewModel.SnipHotkey = "Shift+F1";

        Assert.Equal(before + 1, hotkeyCoordinator.RegisterCallCount);
        Assert.Equal(HotkeyIds.Snip, hotkeyCoordinator.LastRegisteredId);
        Assert.Equal("Shift+F1", hotkeyCoordinator.LastRegisteredHotkey);
    }

    [Fact]
    public void CheckHotkeyConflict_Finds_New_DefaultActionKeyConflicts()
    {
        var viewModel = CreateViewModel();
        viewModel.Snip_Pin = "F6";
        viewModel.Record_Action = "F6";
        viewModel.Translate_Action = "F8";
        viewModel.Translate_Pin = "F6";

        Assert.Equal("MenuPinTranslation", viewModel.CheckHotkeyConflict("Translate_Action", "F6"));
        Assert.Null(viewModel.CheckHotkeyConflict("Record_Action", "F6"));
        Assert.Equal("ActionHideTranslate", viewModel.CheckHotkeyConflict("Translate_Pin", "F8"));
        Assert.Null(viewModel.CheckHotkeyConflict("Snip_Pin", "F6"));
    }

    [Fact]
    public void UnifiedPinHotkeys_StayInSync_WhenAnyOneChanges()
    {
        var viewModel = CreateViewModel();

        viewModel.Record_Action = "Ctrl+F6";

        Assert.Equal("Ctrl+F6", viewModel.Snip_Pin);
        Assert.Equal("Ctrl+F6", viewModel.Record_Action);
        Assert.Equal("Ctrl+F6", viewModel.Translate_Pin);
    }

    [Fact]
    public void EnableOcrSelectionDetection_KeepsUnderlyingFlagsInSync()
    {
        var viewModel = CreateViewModel();

        viewModel.EnableOcrSelectionDetection = false;

        Assert.False(viewModel.EnableAIScan);
        Assert.False(viewModel.ShowAIScanBox);

        viewModel.EnableOcrSelectionDetection = true;

        Assert.True(viewModel.EnableAIScan);
        Assert.True(viewModel.ShowAIScanBox);
    }

    [Fact]
    public void RunOnStartup_DelegatesToStartupRegistrationCoordinator()
    {
        var startupRegistration = new CountingStartupRegistrationService();
        var viewModel = CreateViewModel(startupRegistrationService: startupRegistration);

        viewModel.RunOnStartup = true;

        Assert.True(startupRegistration.LastRunOnStartupValue);
        Assert.Equal(1, startupRegistration.SetCallCount);
    }

    private static MainWindowViewModel CreateViewModel(IStartupRegistrationService? startupRegistrationService = null)
    {
        var tempDir = Path.Combine(
            Path.GetTempPath(),
            "GimmeCapture.Tests",
            nameof(MainWindowViewModelHotkeyTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var settingsService = new AppSettingsService(tempDir);
        var aiPathService = new AIPathService(settingsService);
        var nativeResolverService = new NativeResolverService(aiPathService);
        var ffmpegDownloader = new FFmpegDownloaderService(settingsService);
        var aiModelCatalog = new AIModelCatalog();
        var aiResourceService = new AIResourceService(settingsService, aiPathService, nativeResolverService, new AIModelDownloader(), aiModelCatalog);
        var sam2RuntimeService = new SAM2RuntimeService(aiPathService, nativeResolverService);
        var ocrRuntimeService = new OcrRuntimeService(aiResourceService);
        var aiResourceOrchestrator = new Lazy<AIResourceOrchestrator>(() => aiResourceService.Orchestrator);
        var hotkeyCoordinator = new CountingGlobalHotkeySettingsCoordinator();
        var dependencies = new MainWindowViewModelDependencies(
            settingsService,
            new AvaloniaWindowManager(),
            new AvaloniaThemeResourceService(),
            new WindowsGlobalHotkeyService(),
            hotkeyCoordinator,
            startupRegistrationService ?? new NoOpStartupRegistrationService(),
            new ImmediateSettingsSaveCoordinatorFactory(),
            new MainWindowSettingsPersistenceService(),
            new HotkeyMappingService(),
            new HotkeyRouterService(),
            new Lazy<FFmpegDownloaderService>(() => ffmpegDownloader),
            new Lazy<RecordingService>(() => new RecordingService(ffmpegDownloader, settingsService)),
            new Lazy<UpdateService>(() => new UpdateService("1.2.3")),
            aiModelCatalog,
            new Lazy<AIResourceService>(() => aiResourceService),
            aiResourceOrchestrator,
            new Lazy<SAM2RuntimeService>(() => sam2RuntimeService),
            new Lazy<OcrRuntimeService>(() => ocrRuntimeService),
            aiPathService,
            new ResourceQueueService());

        return new MainWindowViewModel(dependencies);
    }

    private sealed class CountingGlobalHotkeySettingsCoordinator : IGlobalHotkeySettingsCoordinator
    {
        public int RegisterCallCount { get; private set; }
        public int LastRegisteredId { get; private set; }
        public string LastRegisteredHotkey { get; private set; } = string.Empty;

        public void RegisterGlobalHotkey(int id, string hotkey)
        {
            RegisterCallCount++;
            LastRegisteredId = id;
            LastRegisteredHotkey = hotkey;
        }
    }

    private sealed class NoOpStartupRegistrationService : IStartupRegistrationService
    {
        public void SetStartup(bool runOnStartup)
        {
        }

        public bool IsRegistered()
        {
            return false;
        }
    }

    private sealed class CountingStartupRegistrationService : IStartupRegistrationService
    {
        public int SetCallCount { get; private set; }
        public bool LastRunOnStartupValue { get; private set; }

        public void SetStartup(bool runOnStartup)
        {
            SetCallCount++;
            LastRunOnStartupValue = runOnStartup;
        }

        public bool IsRegistered()
        {
            return LastRunOnStartupValue;
        }
    }

    private sealed class ImmediateSettingsSaveCoordinatorFactory : ISettingsSaveCoordinatorFactory
    {
        public ISettingsSaveCoordinator Create(Func<Task<bool>> saveAsync)
        {
            return new ImmediateSettingsSaveCoordinator(saveAsync);
        }
    }

    private sealed class ImmediateSettingsSaveCoordinator : ISettingsSaveCoordinator
    {
        private readonly Func<Task<bool>> _saveAsync;

        public ImmediateSettingsSaveCoordinator(Func<Task<bool>> saveAsync)
        {
            _saveAsync = saveAsync;
        }

        public void RequestSave()
        {
            _ = _saveAsync();
        }
    }
}
