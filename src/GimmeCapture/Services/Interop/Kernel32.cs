using System;
using System.Runtime.InteropServices;

namespace GimmeCapture.Services.Interop;

/// <summary>The kernel32 functions more than one part of the app uses, declared once (see <see cref="User32"/>).</summary>
internal static class Kernel32
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);
}
