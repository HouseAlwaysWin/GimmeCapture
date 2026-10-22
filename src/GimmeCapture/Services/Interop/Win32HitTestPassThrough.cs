using System;
using System.Runtime.InteropServices;
using Avalonia;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Services.Interop;

/// <summary>
/// Lets clicks through parts of a window by subclassing it: a hit test (<c>WM_NCHITTEST</c>) at a point the predicate
/// passes through answers <c>HTTRANSPARENT</c>, and every other message reaches the window's own procedure as sent.
/// The snip overlay and the pinned image use it to take clicks only where their controls are.
/// </summary>
/// <remarks>
/// The subclass is Unicode, like the windows. An ANSI one makes Windows convert each message to the ANSI code page and
/// back on its way through, so a character that code page lacks (an emoji, or Thai on a Chinese system) reached the
/// window as '?'.
/// </remarks>
internal sealed class Win32HitTestPassThrough : IDisposable
{
    private const uint WM_NCHITTEST = 0x0084;
    private const int HTTRANSPARENT = -1;

    private readonly IntPtr _hwnd;
    private readonly Func<PixelPoint, bool> _passThrough;
    private readonly User32.WndProc _proc; // Windows calls it until Dispose, so it must stay reachable
    private IntPtr _previous;
    private bool _failureLogged;

    private Win32HitTestPassThrough(IntPtr hwnd, Func<PixelPoint, bool> passThrough)
    {
        _hwnd = hwnd;
        _passThrough = passThrough;
        _proc = WndProc;
    }

    /// <summary>Subclasses <paramref name="hwnd"/>, which must belong to the calling thread.</summary>
    /// <param name="passThrough">Whether clicks go through at a point of the client area, in physical pixels.</param>
    public static Win32HitTestPassThrough Install(IntPtr hwnd, Func<PixelPoint, bool> passThrough)
    {
        var subclass = new Win32HitTestPassThrough(hwnd, passThrough);
        subclass._previous = User32.SetWindowLongPtr(
            hwnd, User32.GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(subclass._proc));
        return subclass;
    }

    /// <summary>Gives the window its own procedure back.</summary>
    public void Dispose()
    {
        if (_previous != IntPtr.Zero)
        {
            User32.SetWindowLongPtr(_hwnd, User32.GWLP_WNDPROC, _previous);
            _previous = IntPtr.Zero;
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_NCHITTEST && IsPassThrough(hWnd, lParam))
        {
            return new IntPtr(HTTRANSPARENT);
        }

        return User32.CallWindowProc(_previous, hWnd, msg, wParam, lParam);
    }

    private bool IsPassThrough(IntPtr hWnd, IntPtr lParam)
    {
        try
        {
            // Signed screen coordinates: a monitor left of or above the primary one has negative ones.
            int lp = unchecked((int)lParam.ToInt64());
            var point = new User32.POINT { X = (short)(lp & 0xFFFF), Y = (short)((lp >> 16) & 0xFFFF) };
            User32.ScreenToClient(hWnd, ref point);
            return _passThrough(new PixelPoint(point.X, point.Y));
        }
        catch (Exception ex)
        {
            // An exception must not unwind into Windows, which ends the app. The window keeps the click instead.
            if (!_failureLogged)
            {
                _failureLogged = true;
                AppLog.Warning("Win32.HitTestPassThrough", ex);
            }

            return false;
        }
    }
}
