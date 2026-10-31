using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.ViewModels.Main;

namespace GimmeCapture.Tests.ViewModels;

// "Use less memory" draws every window on the CPU. The renderer is picked as the app starts, so the setting is saved and
// a restart offered; the bootstrapper asks only when it differs from what this process runs on.
public sealed class MainWindowViewModelRenderingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));

    public MainWindowViewModelRenderingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public async Task TheSavedSetting_IsLoaded()
    {
        File.WriteAllText(Path.Combine(_dir, "config.json"), """{ "SoftwareRendering": true }""");
        var viewModel = new MainWindowViewModel(MainWindowViewModel.CreateDesignDependencies() with
        {
            SettingsService = new AppSettingsService(_dir)
        });

        await viewModel.InitialSettingsLoadTask;

        Assert.True(viewModel.SoftwareRendering);
    }

    [Fact]
    public async Task EachChange_IsSaved_AndOffersARestart()
    {
        var saves = new CountingSaveCoordinatorFactory();
        var viewModel = new MainWindowViewModel(MainWindowViewModel.CreateDesignDependencies() with
        {
            SettingsService = new AppSettingsService(_dir),
            SettingsSaveCoordinatorFactory = saves
        });
        await viewModel.InitialSettingsLoadTask;
        int restarts = 0;
        viewModel.RequestRestartForRenderingAction = () => restarts++;
        int savesBefore = saves.Requests;

        viewModel.SoftwareRendering = true;
        Assert.Equal(1, restarts);
        Assert.True(viewModel.AppSettingsService.Settings.SoftwareRendering);
        Assert.True(saves.Requests > savesBefore);

        // Off again needs a restart as much as on did, unless the app started on the CPU; the bootstrapper knows which.
        viewModel.SoftwareRendering = false;
        Assert.Equal(2, restarts);
        Assert.False(viewModel.AppSettingsService.Settings.SoftwareRendering);

        viewModel.SoftwareRendering = false;
        Assert.Equal(2, restarts);
    }

    [Fact]
    public async Task TheSettingShows_OnlyWhereItPicksTheRenderer()
    {
        var viewModel = new MainWindowViewModel(MainWindowViewModel.CreateDesignDependencies());
        await viewModel.InitialSettingsLoadTask;

        Assert.Equal(OperatingSystem.IsWindows(), viewModel.CanUseSoftwareRendering);
    }

    private sealed class CountingSaveCoordinatorFactory : ISettingsSaveCoordinatorFactory, ISettingsSaveCoordinator
    {
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        public ISettingsSaveCoordinator Create(Func<Task<bool>> saveAsync) => this;

        public void RequestSave() => Interlocked.Increment(ref _requests);
    }
}
