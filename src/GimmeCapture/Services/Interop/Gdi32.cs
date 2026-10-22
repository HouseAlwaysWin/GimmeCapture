using System;
using System.Runtime.InteropServices;

namespace GimmeCapture.Services.Interop;

/// <summary>The gdi32 functions more than one part of the app uses, declared once (see <see cref="User32"/>).</summary>
internal static class Gdi32
{
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(IntPtr hObject);
}
