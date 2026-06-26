using GimmeCapture.Services.Core.Media.NativeFFmpeg;

namespace GimmeCapture.Tests;

public class KeystrokeOverlayRendererTests
{
    [Fact]
    public void BuildChord_PlainKey_HasNoModifiers()
    {
        Assert.Equal("A", KeystrokeOverlayRenderer.BuildChord(ctrl: false, alt: false, shift: false, win: false, "A"));
    }

    [Fact]
    public void BuildChord_OrdersModifiers_CtrlAltShiftWin()
    {
        Assert.Equal("Ctrl+Alt+Shift+Win+P",
            KeystrokeOverlayRenderer.BuildChord(ctrl: true, alt: true, shift: true, win: true, "P"));
    }

    [Theory]
    [InlineData(true, false, false, false, "C", "Ctrl+C")]
    [InlineData(false, false, true, false, "Tab", "Shift+Tab")]
    [InlineData(true, false, true, false, "S", "Ctrl+Shift+S")]
    [InlineData(false, true, false, false, "F4", "Alt+F4")]
    [InlineData(false, false, false, true, "D", "Win+D")]
    public void BuildChord_FormatsCommonCombos(bool ctrl, bool alt, bool shift, bool win, string key, string expected)
    {
        Assert.Equal(expected, KeystrokeOverlayRenderer.BuildChord(ctrl, alt, shift, win, key));
    }
}
