using GimmeCapture.Services.Interop;

namespace GimmeCapture.Tests;

public sealed class User32Tests : IDisposable
{
    private const long ClickThrough = User32.WS_EX_TRANSPARENT | User32.WS_EX_LAYERED | User32.WS_EX_NOACTIVATE;

    private readonly Win32TestWindow _window = new(left: 0, top: 0, width: 100, height: 100);

    public void Dispose() => _window.Dispose();

    [WindowsFact]
    public void SetExStyleFlags_SetsAndClearsOnlyTheBitsGiven()
    {
        long before = ExStyle();

        User32.SetExStyleFlags(_window.Handle, ClickThrough, set: true);
        Assert.Equal(before | ClickThrough, ExStyle());

        User32.SetExStyleFlags(_window.Handle, User32.WS_EX_TRANSPARENT, set: false);
        Assert.Equal((before | ClickThrough) & ~User32.WS_EX_TRANSPARENT, ExStyle());
    }

    [WindowsFact]
    public void SetExStyleFlags_LeavesTheWindowAlone_WhenNothingChanges()
    {
        User32.SetExStyleFlags(_window.Handle, User32.WS_EX_NOACTIVATE, set: true);
        int changes = _window.StyleChanges;

        User32.SetExStyleFlags(_window.Handle, User32.WS_EX_NOACTIVATE, set: true);
        User32.SetExStyleFlags(_window.Handle, User32.WS_EX_TRANSPARENT, set: false);

        Assert.Equal(changes, _window.StyleChanges);
        Assert.True(changes > 0, "the first call should have changed the style");
    }

    private long ExStyle() => User32.GetWindowLong(_window.Handle, User32.GWL_EXSTYLE);
}
