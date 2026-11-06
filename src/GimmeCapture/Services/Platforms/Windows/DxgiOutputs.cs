using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using GimmeCapture.Services.Interop;

namespace GimmeCapture.Services.Platforms.Windows;

/// <summary>
/// Finds the display a recording region is on, through DXGI, and makes the Direct3D 11 device Desktop Duplication
/// duplicates it with. A display is duplicated through the adapter it hangs off: on a laptop with two graphics cards
/// a device on the other one cannot duplicate it ("Selected output not supported").
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe class DxgiOutputs
{
    private const int EnumAdaptersSlot = 7;    // IDXGIFactory::EnumAdapters, after IUnknown's 3 and IDXGIObject's 4
    private const int EnumOutputsSlot = 7;     // IDXGIAdapter::EnumOutputs
    private const int GetDescSlot = 7;         // IDXGIOutput::GetDesc
    private const int AdapterGetDescSlot = 8;  // IDXGIAdapter::GetDesc
    private const int SetProtectedSlot = 5;    // ID3D10Multithread::SetMultithreadProtected, after IUnknown, Enter, Leave
    private static readonly Guid D3D10Multithread = new("9b7e4e00-342c-4106-a19f-4f2704f689f0");

    // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2: rectangles in physical pixels, as a recording region is given.
    private const nint PerMonitorAwareV2 = -4;

    /// <summary>
    /// Makes the device to duplicate the display that holds the whole region (physical pixels of the virtual screen),
    /// on the adapter that display hangs off, or returns null where no display holds it or no device is made, saying
    /// why in the log. The adapter is taken as DXGI lists it, not by its number: once NVENC runs, Windows lists the
    /// NVIDIA card first, and FFmpeg, told to make its device on adapter 0, made it there.
    /// </summary>
    public static DuplicationDevice? TryCreateDuplicationDevice(int x, int y, int width, int height)
    {
        nint previousAwareness = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        IntPtr factory = IntPtr.Zero;
        try
        {
            int hr = Dxgi.CreateDXGIFactory1(in Dxgi.Factory1, out factory);
            if (hr < 0 || factory == IntPtr.Zero)
            {
                Log($"no DXGI factory (0x{hr:X8})");
                return null;
            }

            // One factory throughout: its adapters keep their order while it lives.
            if (DuplicationOutput.Holding(List(factory), x, y, width, height) is not { } output)
            {
                Log("no single upright display holds the region");
                return null;
            }

            if (Enumerate(factory, EnumAdaptersSlot, (uint)output.Adapter, out IntPtr adapter) < 0)
            {
                Log($"adapter {output.Adapter} went away");
                return null;
            }

            try
            {
                // Video support, as FFmpeg asks for its own Direct3D 11 devices: its device context wants the video device.
                hr = D3D11.D3D11CreateDevice(
                    adapter, D3D11.DriverTypeUnknown, IntPtr.Zero, D3D11.CreateDeviceVideoSupport,
                    IntPtr.Zero, 0, D3D11.SdkVersion, out IntPtr device, out _, out IntPtr context);
                if (context != IntPtr.Zero)
                {
                    Marshal.Release(context);
                }

                if (hr < 0 || device == IntPtr.Zero)
                {
                    Log($"no Direct3D 11 device on adapter {output.Adapter} (0x{hr:X8})");
                    return null;
                }

                ProtectFromThreads(device);
                Log($"display {output.Output} of adapter {output.Adapter} ({Describe(adapter)}), ({output.Left},{output.Top})-({output.Right},{output.Bottom})");
                return DuplicationDevice.Adopt(output, device);
            }
            finally
            {
                Marshal.Release(adapter);
            }
        }
        finally
        {
            if (factory != IntPtr.Zero)
            {
                Marshal.Release(factory);
            }

            if (previousAwareness != 0)
            {
                SetThreadDpiAwarenessContext(previousAwareness);
            }
        }
    }

    // Every display on every adapter of the factory, in its adapter order.
    private static List<DuplicationOutput> List(IntPtr factory)
    {
        var outputs = new List<DuplicationOutput>();
        for (uint a = 0; Enumerate(factory, EnumAdaptersSlot, a, out IntPtr adapter) >= 0; a++)
        {
            try
            {
                for (uint o = 0; Enumerate(adapter, EnumOutputsSlot, o, out IntPtr output) >= 0; o++)
                {
                    try
                    {
                        OutputDesc desc;
                        var getDesc = (delegate* unmanaged[Stdcall]<IntPtr, OutputDesc*, int>)(*(IntPtr**)output)[GetDescSlot];
                        if (getDesc(output, &desc) >= 0)
                        {
                            outputs.Add(new DuplicationOutput(
                                (int)a, (int)o, desc.Left, desc.Top, desc.Right, desc.Bottom, desc.AttachedToDesktop != 0, desc.Rotation));
                        }
                    }
                    finally
                    {
                        Marshal.Release(output);
                    }
                }
            }
            finally
            {
                Marshal.Release(adapter);
            }
        }

        return outputs;
    }

    // The adapter's name, for the log: which card a duplication went through is the first thing to know.
    private static string Describe(IntPtr adapter)
    {
        AdapterDesc desc;
        var getDesc = (delegate* unmanaged[Stdcall]<IntPtr, AdapterDesc*, int>)(*(IntPtr**)adapter)[AdapterGetDescSlot];
        return getDesc(adapter, &desc) >= 0 ? new string(desc.Description) : "(unknown)";
    }

    // What FFmpeg does with the devices it makes itself: its threads may use the device's context in turn.
    private static void ProtectFromThreads(IntPtr device)
    {
        Guid iid = D3D10Multithread;
        if (Marshal.QueryInterface(device, in iid, out IntPtr multithread) >= 0 && multithread != IntPtr.Zero)
        {
            var setProtected = (delegate* unmanaged[Stdcall]<IntPtr, int, int>)(*(IntPtr**)multithread)[SetProtectedSlot];
            setProtected(multithread, 1);
            Marshal.Release(multithread);
        }
    }

    // Calls a COM method of the shape HRESULT Method(UINT index, IUnknown** item), which fails past the last item.
    private static int Enumerate(IntPtr instance, int slot, uint index, out IntPtr item)
    {
        IntPtr found = IntPtr.Zero;
        var method = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)(*(IntPtr**)instance)[slot];
        int hr = method(instance, index, &found);
        item = found;
        return hr >= 0 && found == IntPtr.Zero ? -1 : hr;
    }

    private static void Log(string message) => AppLog.Information("RecordingNative", "Desktop Duplication: " + message);

    // DXGI_ADAPTER_DESC, up to the name: GetDesc writes the whole of it.
    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterDesc
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
    }

    // DXGI_OUTPUT_DESC.
    [StructLayout(LayoutKind.Sequential)]
    private struct OutputDesc
    {
        public fixed char DeviceName[32];
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public int AttachedToDesktop;
        public int Rotation;
        public IntPtr Monitor;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint SetThreadDpiAwarenessContext(nint dpiContext);
}
