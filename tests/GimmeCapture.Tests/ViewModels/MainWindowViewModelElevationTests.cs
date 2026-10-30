using System;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.ViewModels.Main;

namespace GimmeCapture.Tests.ViewModels;

// While a window run as administrator has the foreground, Windows withholds every hotkey from an app that is not
// elevated. The first such window explains it once and offers "run as administrator"; turning that on offers a restart.
public sealed class MainWindowViewModelElevationTests
{
    private static async Task<(MainWindowViewModel ViewModel, CountingSaveCoordinatorFactory Saves)> LoadedViewModelAsync(
        AdministratorRights rights)
    {
        var saves = new CountingSaveCoordinatorFactory();
        var viewModel = new MainWindowViewModel(MainWindowViewModel.CreateDesignDependencies() with
        {
            SettingsSaveCoordinatorFactory = saves
        });
        await viewModel.InitialSettingsLoadTask;
        viewModel.AdministratorRightsProvider = () => rights;
        return (viewModel, saves);
    }

    [Fact]
    public async Task TheFirstAdministratorWindow_ExplainsOnce_AndTheSettingsRememberIt()
    {
        var (viewModel, saves) = await LoadedViewModelAsync(AdministratorRights.CanElevate);
        int prompts = 0;
        viewModel.RequestElevatedWindowPromptAction = () => prompts++;
        int savesBefore = saves.Requests;

        viewModel.OfferRunAsAdministratorOnce();
        viewModel.OfferRunAsAdministratorOnce();

        Assert.Equal(1, prompts);
        Assert.True(viewModel.AppSettingsService.Settings.ElevatedWindowNoticeShown);
        Assert.True(saves.Requests > savesBefore);
    }

    [Theory]
    // Already elevated: nothing is withheld. A standard user: the setting cannot elevate them as themselves.
    [InlineData(AdministratorRights.Elevated)]
    [InlineData(AdministratorRights.Unavailable)]
    public async Task NoExplanation_WhereRunningAsAdministratorCannotHelp(AdministratorRights rights)
    {
        var (viewModel, _) = await LoadedViewModelAsync(rights);
        int prompts = 0;
        viewModel.RequestElevatedWindowPromptAction = () => prompts++;

        viewModel.OfferRunAsAdministratorOnce();

        Assert.Equal(0, prompts);
        Assert.False(viewModel.AppSettingsService.Settings.ElevatedWindowNoticeShown);
    }

    [Fact]
    public async Task NoExplanation_WhenTheSettingIsAlreadyOn()
    {
        // On, but this run declined the UAC prompt: the user has chosen already.
        var (viewModel, _) = await LoadedViewModelAsync(AdministratorRights.CanElevate);
        viewModel.SetRunAsAdministrator(true);
        int prompts = 0;
        viewModel.RequestElevatedWindowPromptAction = () => prompts++;

        viewModel.OfferRunAsAdministratorOnce();

        Assert.Equal(0, prompts);
    }

    [Fact]
    public async Task NoExplanation_DuringACapture_ALaterWindowAsks()
    {
        var (viewModel, _) = await LoadedViewModelAsync(AdministratorRights.CanElevate);
        using var overlay = new SnipWindowViewModel();
        SnipWindowViewModel? open = overlay;
        viewModel.GetActiveSnipViewModelAction = () => open;
        int prompts = 0;
        viewModel.RequestElevatedWindowPromptAction = () => prompts++;

        viewModel.OfferRunAsAdministratorOnce();
        Assert.Equal(0, prompts);
        Assert.False(viewModel.AppSettingsService.Settings.ElevatedWindowNoticeShown);

        open = null;
        viewModel.OfferRunAsAdministratorOnce();
        Assert.Equal(1, prompts);
    }

    [Fact]
    public async Task TurningTheSettingOn_OffersARestart()
    {
        var (viewModel, _) = await LoadedViewModelAsync(AdministratorRights.CanElevate);
        int restarts = 0;
        viewModel.RequestRestartAsAdministratorAction = () => restarts++;

        viewModel.RunAsAdministrator = true;
        Assert.Equal(1, restarts);
        Assert.True(viewModel.AppSettingsService.Settings.RunAsAdministrator);

        // Off takes effect at the next launch: an elevated process cannot drop its rights.
        viewModel.RunAsAdministrator = false;
        Assert.Equal(1, restarts);
        Assert.False(viewModel.AppSettingsService.Settings.RunAsAdministrator);
    }

    [Fact]
    public async Task TurningTheSettingOn_WhileElevated_OffersNoRestart()
    {
        var (viewModel, _) = await LoadedViewModelAsync(AdministratorRights.Elevated);
        int restarts = 0;
        viewModel.RequestRestartAsAdministratorAction = () => restarts++;

        viewModel.RunAsAdministrator = true;

        Assert.Equal(0, restarts);
        Assert.True(viewModel.AppSettingsService.Settings.RunAsAdministrator);
    }

    [Fact]
    public async Task SetRunAsAdministrator_SavesWithoutOfferingARestart()
    {
        // The explanation's Yes turns the setting on and restarts by itself; a second question would be one too many.
        var (viewModel, saves) = await LoadedViewModelAsync(AdministratorRights.CanElevate);
        int restarts = 0;
        viewModel.RequestRestartAsAdministratorAction = () => restarts++;
        int savesBefore = saves.Requests;

        Assert.True(viewModel.SetRunAsAdministrator(true));
        Assert.False(viewModel.SetRunAsAdministrator(true));

        Assert.Equal(0, restarts);
        Assert.True(viewModel.RunAsAdministrator);
        Assert.True(viewModel.AppSettingsService.Settings.RunAsAdministrator);
        Assert.True(saves.Requests > savesBefore);
    }

    [Theory]
    [InlineData(AdministratorRights.CanElevate, true)]
    [InlineData(AdministratorRights.Elevated, true)]
    [InlineData(AdministratorRights.Unavailable, false)]
    public async Task TheSettingShows_OnlyWhereItCanWork(AdministratorRights rights, bool shown)
    {
        var (viewModel, _) = await LoadedViewModelAsync(rights);

        Assert.Equal(shown, viewModel.CanRunAsAdministrator);
    }

    private sealed class CountingSaveCoordinatorFactory : ISettingsSaveCoordinatorFactory, ISettingsSaveCoordinator
    {
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        public ISettingsSaveCoordinator Create(Func<Task<bool>> saveAsync) => this;

        public void RequestSave() => Interlocked.Increment(ref _requests);
    }
}
