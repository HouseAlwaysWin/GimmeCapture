using System;
using System.Runtime.InteropServices;

namespace GimmeCapture.Services.Interop;

/// <summary>The dxgi functions more than one part of the app uses, declared once (see <see cref="User32"/>).</summary>
internal static class Dxgi
{
    /// <summary>IID_IDXGIFactory1.</summary>
    public static readonly Guid Factory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    [DllImport("dxgi.dll", ExactSpelling = true)]
    public static extern int CreateDXGIFactory1(in Guid riid, out IntPtr factory);
}
