using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using GimmeCapture.Services.Interop;

namespace GimmeCapture.Tests;

/// <summary>
/// Subclasses a <see cref="Win32TestWindow"/> with <see cref="Win32HitTestPassThrough"/> and sends it messages, so a
/// test sees what got past the subclass. The window sits at negative screen coordinates, like one on a monitor left
/// of and above the primary one.
/// </summary>
public sealed class Win32HitTestPassThroughTests : IDisposable
{
    private const uint WM_NCHITTEST = 0x0084;
    private const uint WM_CHAR = 0x0102;
    private const int HTTRANSPARENT = -1;
    private const int OwnAnswer = Win32TestWindow.OwnHitTestAnswer;
    private const int Left = -300;
    private const int Top = -200;

    private readonly Win32TestWindow _window = new(Left, Top, width: 200, height: 100);

    public void Dispose() => _window.Dispose();

    [WindowsFact]
    public void AHitTestWherePassThroughSaysSo_IsTransparent_AndOthersAreTheWindows()
    {
        var seen = new List<PixelPoint>();
        using var subclass = Win32HitTestPassThrough.Install(_window.Handle, point =>
        {
            seen.Add(point);
            return point.X < 50;
        });

        Assert.Equal(HTTRANSPARENT, HitTest(Left + 10, Top + 20));
        Assert.Equal(OwnAnswer, HitTest(Left + 120, Top + 80));
        Assert.Equal(new[] { new PixelPoint(10, 20), new PixelPoint(120, 80) }, seen); // in client pixels
    }

    [WindowsFact]
    public void OtherMessages_ReachTheWindowAsSent()
    {
        using var subclass = Win32HitTestPassThrough.Install(_window.Handle, _ => true);

        // 😀, which arrives as its two UTF-16 halves, and ก (Thai), which no Chinese or Western code page has. Through
        // the ANSI subclass the overlay and the pinned image used to have, the window got "???".
        const string Typed = "😀ก";
        foreach (char c in Typed)
        {
            SendMessage(_window.Handle, WM_CHAR, new IntPtr(c), IntPtr.Zero);
        }

        Assert.Equal(Typed, new string(_window.Chars.ToArray()));
    }

    [WindowsFact]
    public void Dispose_GivesTheWindowItsOwnProcedureBack()
    {
        var subclass = Win32HitTestPassThrough.Install(_window.Handle, _ => true);
        Assert.Equal(HTTRANSPARENT, HitTest(Left + 10, Top + 20));

        subclass.Dispose();

        Assert.Equal(OwnAnswer, HitTest(Left + 10, Top + 20));
    }

    [WindowsFact]
    public void APredicateThatThrows_LeavesTheHitTestToTheWindow()
    {
        using var subclass = Win32HitTestPassThrough.Install(
            _window.Handle, _ => throw new InvalidOperationException("predicate failed"));

        Assert.Equal(OwnAnswer, HitTest(Left + 10, Top + 20));
    }

    // Windows packs the screen point into the low 32 bits as two signed 16-bit halves, sign-extended.
    private int HitTest(int screenX, int screenY) =>
        (int)SendMessage(_window.Handle, WM_NCHITTEST, IntPtr.Zero, new IntPtr((screenY << 16) | (screenX & 0xFFFF)));

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
