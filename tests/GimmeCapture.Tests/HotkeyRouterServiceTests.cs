using GimmeCapture.Models;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Tests;

public class HotkeyRouterServiceTests
{
    [Fact]
    public void TextCopyGlobalHotkeyMapsToTextCopyCaptureMode()
    {
        var router = new HotkeyRouterService();

        bool mapped = router.TryMapGlobalHotkeyToCaptureMode(HotkeyIds.TextCopy, out var mode);

        Assert.True(mapped);
        Assert.Equal(CaptureMode.TextCopy, mode);
        Assert.Equal(
            HotkeyRouterService.SnipGlobalHotkeyAction.TextCopyAutoAction,
            router.ResolveSnipGlobalHotkeyAction(HotkeyIds.TextCopy, "", "", ""));
    }
}
