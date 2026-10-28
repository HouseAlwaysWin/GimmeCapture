using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace GimmeCapture.Services.Platforms.Windows;

/// <summary>
/// Reads the frames of a WGC capture back from the GPU into a <see cref="WgcFrameExchange"/>: each frame's texture is
/// copied into a CPU-readable staging texture, made once (again when the window is resized), which is mapped and copied
/// row by row into a buffer the exchange reuses. <c>SoftwareBitmap.CreateCopyFromSurfaceAsync</c>, used before, made a
/// new bitmap of the frame's size every frame, and the projections of that bitmap and its buffer add GC memory pressure
/// that only their finalizers take back, so a window that kept changing set off a gen2 collection every three frames.
/// </summary>
/// <remarks>
/// One readback at a time, under a lock <see cref="Dispose"/> also takes, so the D3D objects are never released under a
/// readback. The vtable slots are counted in the Windows SDK's d3d11.h, d3d10.h and
/// windows.graphics.directx.direct3d11.interop.h.
/// </remarks>
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed unsafe class WgcSurfaceReader : IDisposable
{
    private const uint FormatBgra = 87;         // DXGI_FORMAT_B8G8R8A8_UNORM, the frame pool's format
    private const uint FormatBgraSrgb = 91;     // DXGI_FORMAT_B8G8R8A8_UNORM_SRGB: the same bytes
    private const uint UsageStaging = 3;        // D3D11_USAGE_STAGING
    private const uint CpuAccessRead = 0x20000; // D3D11_CPU_ACCESS_READ
    private const uint MapRead = 1;             // D3D11_MAP_READ
    private const uint MaxDimension = 16384;    // D3D11_REQ_TEXTURE2D_U_OR_V_DIMENSION

    private const int GetInterfaceSlot = 3;              // IDirect3DDxgiInterfaceAccess::GetInterface
    private const int CreateTexture2DSlot = 5;           // ID3D11Device::CreateTexture2D
    private const int GetImmediateContextSlot = 40;      // ID3D11Device::GetImmediateContext
    private const int MapSlot = 14;                      // ID3D11DeviceContext::Map
    private const int UnmapSlot = 15;                    // ID3D11DeviceContext::Unmap
    private const int CopyResourceSlot = 47;             // ID3D11DeviceContext::CopyResource
    private const int GetDescSlot = 10;                  // ID3D11Texture2D::GetDesc
    private const int SetMultithreadProtectedSlot = 5;   // ID3D10Multithread::SetMultithreadProtected

    private static readonly Guid DxgiInterfaceAccessIid = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid DeviceIid = new("db6f6ddb-ac77-4e88-8253-819df9bbf140");
    private static readonly Guid Texture2DIid = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    private static readonly Guid MultithreadIid = new("9B7E4E00-342C-4106-A19F-4F2704F689F0");

    private readonly object _gate = new(); // everything below is under it
    private IntPtr _device;  // ID3D11Device
    private IntPtr _context; // its immediate context
    private IntPtr _staging; // ID3D11Texture2D the CPU can read, for textures like _stagingFor
    private Texture2DDesc _stagingFor;
    private bool _disposed;

    /// <summary>Reads surfaces of <paramref name="device"/>, the device the capture's frame pool was made on.</summary>
    public WgcSurfaceReader(IDirect3DDevice device)
    {
        _device = GetDxgiInterface(device, DeviceIid);

        // The frame pool works on threads of its own and frames are read back on the thread pool. In case anything else
        // on this device uses its immediate context meanwhile, have D3D serialize it.
        if (Marshal.QueryInterface(_device, in MultithreadIid, out IntPtr multithread) >= 0)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Slot(multithread, SetMultithreadProtectedSlot))(multithread, 1);
            Marshal.Release(multithread);
        }

        IntPtr context;
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, void>)Slot(_device, GetImmediateContextSlot))(_device, &context);
        _context = context;
    }

    /// <summary>
    /// Reads <paramref name="surface"/> into a buffer rented from <paramref name="frames"/> and publishes it as the latest
    /// frame. False when it is not a BGRA texture, or after <see cref="Dispose"/>.
    /// </summary>
    public bool TryRead(IDirect3DSurface surface, WgcFrameExchange frames, out int width, out int height)
    {
        width = 0;
        height = 0;
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            IntPtr texture = GetDxgiInterface(surface, Texture2DIid);
            try
            {
                Texture2DDesc desc;
                ((delegate* unmanaged[Stdcall]<IntPtr, Texture2DDesc*, void>)Slot(texture, GetDescSlot))(texture, &desc);
                if ((desc.Format != FormatBgra && desc.Format != FormatBgraSrgb)
                    || desc.Width == 0 || desc.Height == 0 || desc.Width > MaxDimension || desc.Height > MaxDimension)
                {
                    return false;
                }

                EnsureStaging(desc);
                ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)Slot(_context, CopyResourceSlot))(_context, _staging, texture);

                // Waits for the copy to finish on the GPU.
                MappedSubresource mapped;
                Marshal.ThrowExceptionForHR(
                    ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, MappedSubresource*, int>)Slot(_context, MapSlot))(
                        _context, _staging, 0, MapRead, 0, &mapped));
                try
                {
                    int w = (int)desc.Width;
                    int h = (int)desc.Height;
                    int rowBytes = w * 4;
                    if (mapped.Data == IntPtr.Zero || mapped.RowPitch < rowBytes)
                    {
                        return false;
                    }

                    WgcFrameExchange.FrameBuffer target = frames.Rent(w, h);
                    bool filled = false;
                    try
                    {
                        byte* source = (byte*)mapped.Data;
                        byte* destination = (byte*)target.Pixels;
                        for (int row = 0; row < h; row++)
                        {
                            Buffer.MemoryCopy(source + (long)row * mapped.RowPitch, destination + (long)row * target.Stride, rowBytes, rowBytes);
                        }

                        filled = true;
                    }
                    finally
                    {
                        if (filled)
                        {
                            frames.Publish(target);
                        }
                        else
                        {
                            frames.Return(target);
                        }
                    }

                    width = w;
                    height = h;
                    return true;
                }
                finally
                {
                    ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)Slot(_context, UnmapSlot))(_context, _staging, 0);
                }
            }
            finally
            {
                Marshal.Release(texture);
            }
        }
    }

    /// <summary>Releases the D3D objects, after any readback still running.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Release(ref _staging);
            Release(ref _context);
            Release(ref _device);
        }
    }

    // CopyResource needs a destination of the source's size, format, mip levels, array size and samples.
    private void EnsureStaging(in Texture2DDesc source)
    {
        if (_staging != IntPtr.Zero
            && _stagingFor.Width == source.Width && _stagingFor.Height == source.Height
            && _stagingFor.Format == source.Format && _stagingFor.MipLevels == source.MipLevels
            && _stagingFor.ArraySize == source.ArraySize
            && _stagingFor.SampleCount == source.SampleCount && _stagingFor.SampleQuality == source.SampleQuality)
        {
            return;
        }

        Release(ref _staging);
        Texture2DDesc desc = source;
        desc.Usage = UsageStaging;
        desc.BindFlags = 0;
        desc.CpuAccessFlags = CpuAccessRead;
        desc.MiscFlags = 0;
        IntPtr staging;
        Marshal.ThrowExceptionForHR(
            ((delegate* unmanaged[Stdcall]<IntPtr, Texture2DDesc*, void*, IntPtr*, int>)Slot(_device, CreateTexture2DSlot))(
                _device, &desc, null, &staging));
        _staging = staging;
        _stagingFor = source;
    }

    /// <summary>The D3D object behind a WinRT Direct3D wrapper (device or surface), with a reference the caller releases.</summary>
    private static IntPtr GetDxgiInterface(object wrapper, Guid iid)
    {
        if (wrapper is not IWinRTObject winrt)
        {
            throw new ArgumentException("Not a WinRT Direct3D object.", nameof(wrapper));
        }

        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(winrt.NativeObject.ThisPtr, in DxgiInterfaceAccessIid, out IntPtr access));
        try
        {
            IntPtr result;
            Marshal.ThrowExceptionForHR(
                ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Slot(access, GetInterfaceSlot))(access, &iid, &result));
            return result;
        }
        finally
        {
            Marshal.Release(access);
            GC.KeepAlive(wrapper);
        }
    }

    private static void* Slot(IntPtr com, int index) => (*(void***)com)[index];

    private static void Release(ref IntPtr com)
    {
        if (com != IntPtr.Zero)
        {
            Marshal.Release(com);
            com = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Texture2DDesc // D3D11_TEXTURE2D_DESC
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;
        public uint SampleCount;
        public uint SampleQuality;
        public uint Usage;
        public uint BindFlags;
        public uint CpuAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MappedSubresource // D3D11_MAPPED_SUBRESOURCE
    {
        public IntPtr Data;
        public uint RowPitch;
        public uint DepthPitch;
    }
}
