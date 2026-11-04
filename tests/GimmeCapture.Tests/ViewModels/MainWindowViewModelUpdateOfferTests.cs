using System.Collections.Generic;
using System.Threading.Tasks;
using GimmeCapture.ViewModels.Main;

namespace GimmeCapture.Tests.ViewModels;

// The update dialog is the main window's, and a launch to the tray builds that window only once the user opens it. The
// start-up check used to find an update there and drop it, for the whole session; it now holds it until the main window
// shows, and a toast says so, once a run, opening the main window when clicked: a launch to the tray, as at sign-in, may
// not open the main window for days. With the main window up, an update is offered at once, as before.
public sealed class MainWindowViewModelUpdateOfferTests
{
    private static readonly ReleaseInfo Release = new() { TagName = "v99.0.0" };

    // The question that offers Release, in whatever the strings are here.
    private static string UpdateFoundQuestion => string.Format(LocalizationService.Instance["UpdateFound"], Release.TagName);

    // The toast that announces a release, in whatever the strings are here.
    private static string UpdateToast(ReleaseInfo release) =>
        string.Format(LocalizationService.Instance["UpdateAvailableToast"], release.TagName);

    // Notes every clickable toast, with what a click on it does.
    private static List<(string Message, Action Click)> CollectClickableToasts(MainWindowViewModel viewModel)
    {
        var toasts = new List<(string Message, Action Click)>();
        viewModel.ShowClickableToastAction = (message, _, click) => toasts.Add((message, click));
        return toasts;
    }

    [Fact]
    public async Task AnUpdateFoundWithNoMainWindow_IsAnnouncedOnce_AndTheToastOpensTheMainWindow()
    {
        var viewModel = await LoadedViewModelAsync();
        var toasts = CollectClickableToasts(viewModel);
        int opened = 0;
        viewModel.ShowMainWindowAction = () => opened++;

        await viewModel.OfferUpdateAsync(Release);
        await viewModel.OfferUpdateAsync(Release);

        var toast = Assert.Single(toasts);
        Assert.Equal(UpdateToast(Release), toast.Message);
        Assert.Equal(0, opened);
        toast.Click();
        Assert.Equal(1, opened);
    }

    [Fact]
    public async Task ANewerReleaseFoundLater_IsAnnouncedToo()
    {
        var viewModel = await LoadedViewModelAsync();
        var toasts = CollectClickableToasts(viewModel);
        viewModel.ShowMainWindowAction = () => { };
        var newer = new ReleaseInfo { TagName = "v99.1.0" };

        await viewModel.OfferUpdateAsync(Release);
        await viewModel.OfferUpdateAsync(newer);

        Assert.Equal([UpdateToast(Release), UpdateToast(newer)], toasts.ConvertAll(t => t.Message));
    }

    [Fact]
    public async Task WithTheMainWindowUp_OrNoWayToOpenIt_NoToastIsShown()
    {
        var up = await LoadedViewModelAsync();
        var toastsWhileUp = CollectClickableToasts(up);
        up.ShowMainWindowAction = () => { };
        AnswerUpdateDialogs(up);
        await up.OfferUpdateAsync(Release);

        var trayless = await LoadedViewModelAsync();
        var toastsWithoutTray = CollectClickableToasts(trayless);
        await trayless.OfferUpdateAsync(Release);

        Assert.Empty(toastsWhileUp);
        Assert.Empty(toastsWithoutTray);
    }

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
