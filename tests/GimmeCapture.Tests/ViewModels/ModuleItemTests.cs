using GimmeCapture.Services.Abstractions;
using GimmeCapture.ViewModels.Main;

namespace GimmeCapture.Tests.ViewModels;

// A Modules tab card. OCR installs five languages and counts as installed only with all of them; with two it said "Not
// Installed", which read as broken to a user whose OCR was reading those two. It now says how many it has.
public sealed class ModuleItemTests
{
    [Fact]
    public void AModuleWithSomeOfItsParts_SaysHowManyItHas_AndStillOffersToInstall()
    {
        var ocr = new MainWindowViewModel.ModuleItem("PaddleOCR v5", "ModuleOCRDescription", Strings());

        ocr.SetInstalledParts(2, 5);

        Assert.False(ocr.IsInstalled);
        Assert.Equal("Installed 2/5", ocr.StatusText);
        // Installing fetches only the parts missing.
        Assert.Equal("[InstallModule]", ocr.ActionButtonText);
    }

    [Theory]
    [InlineData(0, false, "NotInstalled", "InstallModule")]
    [InlineData(5, true, "Installed", "RemoveModule")]
    public void AModuleWithNoneOrAllOfItsParts_ReadsAsAnyOther(
        int installed, bool isInstalled, string statusKey, string actionKey)
    {
        var ocr = new MainWindowViewModel.ModuleItem("PaddleOCR v5", "ModuleOCRDescription", Strings());

        ocr.SetInstalledParts(installed, 5);

        Assert.Equal(isInstalled, ocr.IsInstalled);
        Assert.Equal($"[{statusKey}]", ocr.StatusText);
        Assert.Equal($"[{actionKey}]", ocr.ActionButtonText);
    }

    [Fact]
    public void ADownloadUnderWay_OutranksThePartCount()
    {
        var ocr = new MainWindowViewModel.ModuleItem("PaddleOCR v5", "ModuleOCRDescription", Strings());
        ocr.SetInstalledParts(2, 5);

        ocr.IsPending = true;

        Assert.Equal("[Pending]", ocr.StatusText);
    }

    // Marks every string with its key, except the one the count is formatted into, which is the app's English.
    private static ILocalizationService Strings()
    {
        var localization = new Mock<ILocalizationService>();
        localization.Setup(l => l[It.IsAny<string>()]).Returns((string key) => $"[{key}]");
        localization.Setup(l => l["ModulePartlyInstalled"]).Returns("Installed {0}/{1}");
        return localization.Object;
    }
}
