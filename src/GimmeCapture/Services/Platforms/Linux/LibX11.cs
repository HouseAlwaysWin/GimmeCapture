using System;
using System.Runtime.InteropServices;

namespace GimmeCapture.Services.Platforms.Linux;

/// <summary>
/// The libX11 functions more than one Linux service uses, declared once; a function only one service uses stays
/// declared there. Each service opens a display connection of its own.
/// </summary>
internal static class LibX11
{
    private const string Library = "libX11.so.6";

    [DllImport(Library)]
    public static extern IntPtr XOpenDisplay(string? displayName);

    [DllImport(Library)]
    public static extern int XCloseDisplay(IntPtr display);

    [DllImport(Library)]
    public static extern IntPtr XDefaultRootWindow(IntPtr display);
}
