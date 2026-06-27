using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using WinRT;

namespace GimmeCapture.Services.Platforms.Windows;

/// <summary>
/// Low-level Windows Graphics Capture (WGC) interop shared by the capture sources. Wraps the small bits
/// of DirectX/WinRT plumbing that the projections don't surface directly: creating a D3D11-backed
/// <see cref="IDirect3DDevice"/>, building a <see cref="GraphicsCaptureItem"/> for an HWND, and reading a
/// captured <see cref="SoftwareBitmap"/> back into a tightly-packed BGRA byte buffer.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
internal static class WgcInterop
{
    // IID of Windows.Graphics.Capture.IGraphicsCaptureItem — passed to the interop factory's CreateForWindow.
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    public static bool IsSupported()
    {
        try
        {
            return GraphicsCaptureSession.IsSupported();
        }
        catch
        {
            return false;
        }
    }

    public static GraphicsCaptureItem CreateCaptureItemForWindow(IntPtr hwnd)
    {
        var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        var interop = factory.AsInterface<IGraphicsCaptureItemInterop>();
        Guid iid = GraphicsCaptureItemIid;
        IntPtr itemPtr = interop.CreateForWindow(hwnd, ref iid);
        try
        {
            return GraphicsCaptureItem.FromAbi(itemPtr);
        }
        finally
        {
            Marshal.Release(itemPtr);
        }
    }

    public static IDirect3DDevice CreateDirect3DDevice() => CreateDirect3DDevice(out _);

    /// <summary>
    /// Creates the WGC <see cref="IDirect3DDevice"/> on the default hardware adapter (WARP fallback if there
    /// is no GPU) and reports the DXGI description of the adapter that backs it. The description is purely
    /// diagnostic — on a multi-monitor box it lets the log show which GPU WGC actually bound, which is the
    /// first thing to check when the frame pool never raises FrameArrived.
    /// </summary>
    public static IDirect3DDevice CreateDirect3DDevice(out string adapterDescription)
    {
        const uint D3D11_SDK_VERSION = 7;
        const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
        const uint D3D_DRIVER_TYPE_HARDWARE = 1;
        const uint D3D_DRIVER_TYPE_WARP = 5;

        adapterDescription = "(unknown)";

        int hr = D3D11CreateDevice(
            IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            IntPtr.Zero, 0, D3D11_SDK_VERSION, out IntPtr d3dDevice, out _, out IntPtr context);
        if (hr < 0)
        {
            // No GPU / headless: fall back to the WARP software rasterizer.
            hr = D3D11CreateDevice(
                IntPtr.Zero, D3D_DRIVER_TYPE_WARP, IntPtr.Zero, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                IntPtr.Zero, 0, D3D11_SDK_VERSION, out d3dDevice, out _, out context);
            Marshal.ThrowExceptionForHR(hr);
        }

        try
        {
            Guid iidDxgiDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3dDevice, in iidDxgiDevice, out IntPtr dxgiDevice));
            try
            {
                adapterDescription = TryGetAdapterDescription(dxgiDevice);
                Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out IntPtr inspectable));
                try
                {
                    return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
                }
                finally
                {
                    Marshal.Release(inspectable);
                }
            }
            finally
            {
                Marshal.Release(dxgiDevice);
            }
        }
        finally
        {
            if (context != IntPtr.Zero)
            {
                Marshal.Release(context);
            }

            Marshal.Release(d3dDevice);
        }
    }

    /// <summary>Reads the DXGI adapter description backing a D3D device (best effort; "(unknown)" on failure).</summary>
    private static string TryGetAdapterDescription(IntPtr dxgiDevice)
    {
        try
        {
            var dev = (IDXGIDevice)Marshal.GetObjectForIUnknown(dxgiDevice);
            try
            {
                dev.GetAdapter(out IntPtr adapterPtr);
                if (adapterPtr == IntPtr.Zero)
                {
                    return "(unknown)";
                }

                try
                {
                    var adapter = (IDXGIAdapter)Marshal.GetObjectForIUnknown(adapterPtr);
                    try
                    {
                        adapter.GetDesc(out DxgiAdapterDesc desc);
                        string name = desc.Description?.Trim() ?? string.Empty;
                        return string.IsNullOrEmpty(name) ? "(unnamed)" : name;
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(adapter);
                    }
                }
                finally
                {
                    Marshal.Release(adapterPtr);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(dev);
            }
        }
        catch
        {
            return "(unknown)";
        }
    }

    /// <summary>
    /// Returns a compact, single-line description of the monitor that owns a window
    /// (e.g. <c>mon=0x10001 rect=2560x1440@(2560,0) primary=False dev=\\.\DISPLAY2</c>) for diagnostics.
    /// Never throws — returns a "(failed)" marker if the Win32 calls fail.
    /// </summary>
    public static string DescribeWindowMonitor(IntPtr hwnd)
    {
        try
        {
            const uint MONITOR_DEFAULTTONEAREST = 2;
            IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var info = new MonitorInfoEx { cbSize = Marshal.SizeOf<MonitorInfoEx>() };
            if (!GetMonitorInfo(mon, ref info))
            {
                return $"mon=0x{mon.ToInt64():X} (GetMonitorInfo failed)";
            }

            int w = info.rcMonitor.Right - info.rcMonitor.Left;
            int h = info.rcMonitor.Bottom - info.rcMonitor.Top;
            bool primary = (info.dwFlags & 1) != 0; // MONITORINFOF_PRIMARY
            return $"mon=0x{mon.ToInt64():X} rect={w}x{h}@({info.rcMonitor.Left},{info.rcMonitor.Top}) " +
                   $"primary={primary} dev={info.szDevice}";
        }
        catch
        {
            return "(monitor lookup failed)";
        }
    }

    /// <summary>
    /// Copies a BGRA <see cref="SoftwareBitmap"/> into a freshly-allocated, tightly-packed (stride ==
    /// width*4) managed byte buffer. A new array is returned each call so a background capture thread can
    /// hand off the latest frame without the reader racing a buffer it is reused into.
    /// </summary>
    public static unsafe byte[]? CopyBgra(SoftwareBitmap softwareBitmap, out int width, out int height)
    {
        width = 0;
        height = 0;
        using var buffer = softwareBitmap.LockBuffer(BitmapBufferAccessMode.Read);
        var plane = buffer.GetPlaneDescription(0);
        using var reference = buffer.CreateReference();

        var byteAccess = reference.As<IMemoryBufferByteAccess>();
        byteAccess.GetBuffer(out byte* dataPtr, out uint capacity);
        if (dataPtr == null || capacity == 0 || plane.Width <= 0 || plane.Height <= 0)
        {
            return null;
        }

        int w = plane.Width;
        int h = plane.Height;
        int rowBytes = w * 4;
        var managed = new byte[rowBytes * h];
        fixed (byte* dst = managed)
        {
            for (int row = 0; row < h; row++)
            {
                Buffer.MemoryCopy(dataPtr + (long)row * plane.Stride, dst + (long)row * rowBytes, rowBytes, rowBytes);
            }
        }

        width = w;
        height = h;
        return managed;
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(
        IntPtr pAdapter, uint driverType, IntPtr software, uint flags,
        IntPtr pFeatureLevels, uint featureLevels, uint sdkVersion,
        out IntPtr ppDevice, out uint pFeatureLevel, out IntPtr ppImmediateContext);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoEx lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfoEx
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    // Minimal DXGI projections — only the vtable slots up to the method we call are declared, in order.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiAdapterDesc
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public long AdapterLuid;
    }

    [ComImport]
    [Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIDevice
    {
        // IDXGIObject
        void SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
        void SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        void GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
        void GetParent(ref Guid riid, out IntPtr parent);

        // IDXGIDevice
        void GetAdapter(out IntPtr adapter);
    }

    [ComImport]
    [Guid("2411e7e1-12ac-4ccf-bd14-9798e8534dc0")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter
    {
        // IDXGIObject
        void SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
        void SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        void GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
        void GetParent(ref Guid riid, out IntPtr parent);

        // IDXGIAdapter
        void EnumOutputs(uint index, out IntPtr output);
        void GetDesc(out DxgiAdapterDesc desc);
    }

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMemoryBufferByteAccess
    {
        unsafe void GetBuffer(out byte* buffer, out uint capacity);
    }
}
