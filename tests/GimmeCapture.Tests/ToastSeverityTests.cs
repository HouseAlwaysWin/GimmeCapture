using GimmeCapture.ViewModels.Main;
using Xunit;

namespace GimmeCapture.Tests;

public class ToastSeverityTests
{
    [Theory]
    [InlineData("SaveFailed")]
    [InlineData("StatusError")]
    [InlineData("StatusOCRNotReady")]
    [InlineData("QuickOcrModuleMissing")]
    [InlineData("GifUnavailableReason")]
    [InlineData("StatusSAM2NotFound")]
    [InlineData("QuickOcrNoText")]
    [InlineData("StatusTranslateNoSelection")]
    public void ClassifyToastSeverity_ErrorKeys(string key)
    {
        Assert.Equal(MainWindowViewModel.ToastSeverity.Error, MainWindowViewModel.ClassifyToastSeverity(key));
    }

    [Theory]
    [InlineData("StatusCopied")]
    [InlineData("StatusSaved")]
    [InlineData("QuickOcrCopied")]
    public void ClassifyToastSeverity_SuccessKeys(string key)
    {
        Assert.Equal(MainWindowViewModel.ToastSeverity.Success, MainWindowViewModel.ClassifyToastSeverity(key));
    }

    [Theory]
    [InlineData("StatusReady")]
    [InlineData("CheckingUpdate")]
    [InlineData("StatusTranslating")]
    public void ClassifyToastSeverity_InfoKeys(string key)
    {
        Assert.Equal(MainWindowViewModel.ToastSeverity.Info, MainWindowViewModel.ClassifyToastSeverity(key));
    }
}
