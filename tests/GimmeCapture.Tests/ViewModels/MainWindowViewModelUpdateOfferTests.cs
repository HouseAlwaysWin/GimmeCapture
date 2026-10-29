using System.Collections.Generic;
using System.Threading.Tasks;
using GimmeCapture.ViewModels.Main;

namespace GimmeCapture.Tests.ViewModels;

// The update dialog is the main window's, and a launch to the tray builds that window only once the user opens it. The
// start-up check used to find an update there and drop it, for the whole session; it now holds it until the main window
// shows. With the main window up, an update is offered at once, as before.
public sealed class MainWindowViewModelUpdateOfferTests
{
    private static readonly ReleaseInfo Release = new() { TagName = "v99.0.0" };

    // The question that offers Release, in whatever the strings are here.
    private static string UpdateFoundQuestion => string.Format(LocalizationService.Instance["UpdateFound"], Release.TagName);

    private static async Task<MainWindowViewModel> LoadedViewModelAsync()
    {
        var viewModel = new MainWindowViewModel(MainWindowViewModel.CreateDesignDependencies());
        await viewModel.InitialSettingsLoadTask;
        return viewModel;
    }

    // Declines every offer, noting what it was asked.
    private static List<string> AnswerUpdateDialogs(MainWindowViewModel viewModel)
    {
        var asked = new List<string>();
        viewModel.ShowUpdateDialogAction = (message, _) =>
        {
            asked.Add(message);
            return Task.FromResult(false);
        };
        return asked;
    }

    [Fact]
    public async Task AnUpdateFoundWithNoMainWindow_IsOfferedOnceItShows()
    {
        var viewModel = await LoadedViewModelAsync();

        await viewModel.OfferUpdateAsync(Release);
        var asked = AnswerUpdateDialogs(viewModel);
        viewModel.OfferPendingUpdate();

        Assert.Equal([UpdateFoundQuestion], asked);
    }

    [Fact]
    public async Task AHeldBackUpdate_IsOfferedOnlyOnce()
    {
        var viewModel = await LoadedViewModelAsync();
        await viewModel.OfferUpdateAsync(Release);
        var asked = AnswerUpdateDialogs(viewModel);

        viewModel.OfferPendingUpdate();
        viewModel.OfferPendingUpdate();

        Assert.Single(asked);
    }

    [Fact]
    public async Task WithTheMainWindowUp_AnUpdateIsOfferedAtOnce_AndNotHeldBack()
    {
        var viewModel = await LoadedViewModelAsync();
        var asked = AnswerUpdateDialogs(viewModel);

        await viewModel.OfferUpdateAsync(Release);
        viewModel.OfferPendingUpdate();

        Assert.Equal([UpdateFoundQuestion], asked);
    }
}
