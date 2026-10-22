using Avalonia.Media;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.ViewModels.Main;

namespace GimmeCapture.Tests.ViewModels;

/// <summary>
/// View models read their strings through ViewModelBase.Localization, so a stand-in that marks every string with its
/// key shows exactly which one a view model asked for.
/// </summary>
public class ViewModelLocalizationTests
{
    [Fact]
    public async Task TheMainWindowsStatus_ComesFromTheLocalizationItIsGiven()
    {
        var vm = new MainWindowViewModel(MainWindowViewModel.CreateDesignDependencies() with { Localization = Marking() });
        await vm.InitialSettingsLoadTask; // the settings load sets a status of its own

        vm.SetStatus("StatusCopied");

        Assert.Equal("[StatusCopied]", vm.StatusText);
    }

    [Fact]
    public void TheOverlaysTooltips_ComeFromTheLocalizationItIsGiven()
    {
        using var vm = new SnipWindowViewModel(
            Colors.Red, 2.0, new Mock<IScreenCaptureService>().Object, localization: Marking());

        Assert.Equal("[Undo] (Ctrl+Z)", vm.UndoTooltip);
    }

    private static ILocalizationService Marking()
    {
        var localization = new Mock<ILocalizationService>();
        localization.Setup(l => l[It.IsAny<string>()]).Returns((string key) => $"[{key}]");
        return localization.Object;
    }
}
