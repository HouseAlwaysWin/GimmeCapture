using System;
using System.Runtime.InteropServices;

namespace GimmeCapture.Services.Interop;

/// <summary>The d3d11 functions more than one part of the app uses, declared once (see <see cref="User32"/>).</summary>
internal static class D3D11
{
    public const uint SdkVersion = 7;
    public const uint CreateDeviceBgraSupport = 0x20;
    public const uint CreateDeviceVideoSupport = 0x800;

    /// <summary>D3D_DRIVER_TYPE_UNKNOWN, the one to pass with an adapter.</summary>
    public const uint DriverTypeUnknown = 0;
    public const uint DriverTypeHardware = 1;
    public const uint DriverTypeWarp = 5;

    [DllImport("d3d11.dll", ExactSpelling = true)]
    public static extern int D3D11CreateDevice(
        IntPtr pAdapter, uint driverType, IntPtr software, uint flags,
        IntPtr pFeatureLevels, uint featureLevels, uint sdkVersion,
        out IntPtr ppDevice, out uint pFeatureLevel, out IntPtr ppImmediateContext);
}
