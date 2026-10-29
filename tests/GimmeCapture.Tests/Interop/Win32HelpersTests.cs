using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using GimmeCapture.Services.Interop;

namespace GimmeCapture.Tests;

/// <summary>
/// Shapes a <see cref="Win32TestWindow"/> with the region helpers and counts the frame changes it is sent: every
/// SetWindowRgn is one, and it redraws the whole window. The snip overlay's region handlers re-apply an unchanged
/// region on most of their triggers, which repainted everything under the full-screen overlay each time.
/// </summary>
public sealed class Win32HelpersTests : IDisposable
{
    private const int RegionError = 0;

    private readonly Win32TestWindow _window = new(0, 0, width: 200, height: 100);

    public void Dispose() => _window.Dispose();

    [WindowsFact]
    public void SettingTheRegionTheWindowHas_LeavesTheWindowAlone()
    {
        var first = new List<Rect> { new(10, 10, 40, 20), new(0, 0, 1, 1) };
        var second = new List<Rect> { new(60, 30, 50, 40), new(0, 0, 1, 1) };

        Assert.True(Win32Helpers.SetDisjointOpaqueRegions(_window.Handle, first));
        int afterFirst = _window.FrameChanges;
        Assert.True(afterFirst > 0, "SetWindowRgn reached the window");

        Assert.True(Win32Helpers.SetDisjointOpaqueRegions(_window.Handle, first));
        Assert.Equal(afterFirst, _window.FrameChanges);
        Assert.Equal(new RECT(0, 0, 50, 30), RegionBox());

        Assert.True(Win32Helpers.SetDisjointOpaqueRegions(_window.Handle, second));
        Assert.True(_window.FrameChanges > afterFirst, "a different region still reaches the window");
        Assert.Equal(new RECT(0, 0, 110, 70), RegionBox());
    }

    [WindowsFact]
    public void ClearingTheRegion_OnlyTouchesAWindowThatHasOne()
    {
        Win32Helpers.ClearWindowRegion(_window.Handle);
        Assert.Equal(0, _window.FrameChanges);

        Win32Helpers.SetDisjointOpaqueRegions(_window.Handle, new List<Rect> { new(10, 10, 40, 20) });
        int withRegion = _window.FrameChanges;

        Win32Helpers.ClearWindowRegion(_window.Handle);

        Assert.True(_window.FrameChanges > withRegion, "the region was cleared");
        Assert.Equal(RegionError, GetWindowRgnBox(_window.Handle, out _));
    }

    private RECT RegionBox()
    {
        Assert.NotEqual(RegionError, GetWindowRgnBox(_window.Handle, out RECT box));
        return box;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct RECT(int Left, int Top, int Right, int Bottom);

    [DllImport("user32.dll")]
    private static extern int GetWindowRgnBox(IntPtr hWnd, out RECT lprc);
}
