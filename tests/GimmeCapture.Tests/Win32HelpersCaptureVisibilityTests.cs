using System.Runtime.InteropServices;
using GimmeCapture.Services.Interop;

namespace GimmeCapture.Tests;

/// <summary>
/// <see cref="Win32Helpers.SetWindowCaptureVisibility"/> leaves a window that already has the affinity it asks for alone:
/// setting it anyway on the hidden capture overlay, drawn on the processor, put the overlay on screen as a white
/// rectangle (the method's remarks have the measurements). Its skip compares the affinity Windows reads back with the one
/// it would set, so this pins, on a real hidden window, that every real change is still made and read back as set.
/// </summary>
public sealed class Win32HelpersCaptureVisibilityTests
{
    private const uint WDA_NONE = 0x00000000;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
    private const uint WS_POPUP = 0x80000000;

    [WindowsFact]
    public void EveryChangeIsMade_AndAWindowAskedForWhatItHasIsLeftAsItWas()
    {
        IntPtr hwnd = CreateWindowEx(0, "STATIC", null, WS_POPUP, 0, 0, 100, 100, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Assert.NotEqual(IntPtr.Zero, hwnd);
        try
        {
            Assert.Equal(WDA_NONE, Affinity(hwnd));

            Win32Helpers.SetWindowCaptureVisibility(hwnd, visible: true);
            Assert.Equal(WDA_NONE, Affinity(hwnd));

            Win32Helpers.SetWindowCaptureVisibility(hwnd, visible: false);
            Assert.Equal(WDA_EXCLUDEFROMCAPTURE, Affinity(hwnd));

            Win32Helpers.SetWindowCaptureVisibility(hwnd, visible: false);
            Assert.Equal(WDA_EXCLUDEFROMCAPTURE, Affinity(hwnd));

            Win32Helpers.SetWindowCaptureVisibility(hwnd, visible: true);
            Assert.Equal(WDA_NONE, Affinity(hwnd));
        }
        finally
        {
            DestroyWindow(hwnd);
        }
    }

    private static uint Affinity(IntPtr hwnd)
    {
        Assert.True(GetWindowDisplayAffinity(hwnd, out uint affinity));
        return affinity;
    }

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint exStyle, string className, string? windowName, uint style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);
}
