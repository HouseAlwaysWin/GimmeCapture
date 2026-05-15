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
            ffmpegDownloader,
            new RecordingService(ffmpegDownloader, settingsService),
            new UpdateService("1.2.3"),
            new AIResourceService(settingsService, aiPathService, nativeResolverService, new AIModelDownloader()),
            new SAM2RuntimeService(aiPathService, nativeResolverService),
            aiPathService,
            ResourceQueueService.Instance);

        var viewModel = new MainWindowViewModel(dependencies);

        int before = hotkeyCoordinator.RegisterCallCount;
        viewModel.SnipHotkey = "Shift+F1";

        Assert.Equal(before + 1, hotkeyCoordinator.RegisterCallCount);
        Assert.Equal(HotkeyIds.Snip, hotkeyCoordinator.LastRegisteredId);
        Assert.Equal("Shift+F1", hotkeyCoordinator.LastRegisteredHotkey);
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
