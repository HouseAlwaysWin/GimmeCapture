#if WINDOWS // WgcSurfaceReader reads Direct3D 11 textures, which only the Windows head builds
using System.Runtime.InteropServices;
using GimmeCapture.Services.Platforms.Windows;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace GimmeCapture.Tests;

// Reads textures back the way a window recording reads Windows Graphics Capture's frames, on a Direct3D 11 device of the
// test's own (the GPU's, or WARP where there is none), so it needs no window and no capture. The reader replaced
// SoftwareBitmap.CreateCopyFromSurfaceAsync, whose bitmaps added GC memory pressure that only their finalizers took back:
// a window that kept changing set off a gen2 collection every three frames.
public sealed unsafe class WgcSurfaceReaderTests
{
    [Fact]
    public void AFrame_ArrivesWhole_RowByRow()
    {
        using var device = new TestDevice();
        using var reader = new WgcSurfaceReader(device.Device);
        using var frames = new WgcFrameExchange();
        var (surface, pixels) = device.CreateSurface(13, 7, seed: 1); // 52 bytes a row: unlike any row pitch

        Assert.True(reader.TryRead(surface, frames, out int width, out int height));

        Assert.Equal((13, 7), (width, height));
        Assert.Equal(pixels, Latest(frames));
    }

    [Fact]
    public void AResizedWindowsFrames_AreReadAtTheirOwnSize()
    {
        using var device = new TestDevice();
        using var reader = new WgcSurfaceReader(device.Device);
        using var frames = new WgcFrameExchange();

        foreach (var (width, height, seed) in new[] { (13, 7, 1), (40, 9, 2), (13, 7, 3), (8, 30, 4) })
        {
            var (surface, pixels) = device.CreateSurface(width, height, (byte)seed);
            Assert.True(reader.TryRead(surface, frames, out int readWidth, out int readHeight));
            Assert.Equal((width, height), (readWidth, readHeight));
            Assert.Equal(pixels, Latest(frames));
        }
    }

    [Fact]
    public void ASteadyStreamOfFrames_AllocatesNothing()
    {
        using var device = new TestDevice();
        using var reader = new WgcSurfaceReader(device.Device);
        using var frames = new WgcFrameExchange();
        var (surface, _) = device.CreateSurface(64, 48, seed: 5);
        long sequence = 0;
        int failed = 0;

        void ReadAndConvert()
        {
            if (!reader.TryRead(surface, frames, out _, out _) || !frames.TryRead(sequence, out var frame))
            {
                failed++;
                return;
            }

            sequence = frame.Sequence;
            frame.Dispose();
        }

        for (int i = 0; i < 10; i++)
        {
            ReadAndConvert();
        }

        int gen2 = GC.CollectionCount(2);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200; i++)
        {
            ReadAndConvert();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, failed);
        Assert.Equal(0, allocated);
        Assert.Equal(gen2, GC.CollectionCount(2));
    }

    [Fact]
    public void AfterDispose_NothingIsRead()
    {
        using var device = new TestDevice();
        var reader = new WgcSurfaceReader(device.Device);
        using var frames = new WgcFrameExchange();
        var (surface, _) = device.CreateSurface(4, 4, seed: 6);

        reader.Dispose();
        reader.Dispose();

        Assert.False(reader.TryRead(surface, frames, out _, out _));
        Assert.Equal(0, frames.Sequence);
    }

    private static byte[] Latest(WgcFrameExchange frames)
    {
        Assert.True(frames.TryRead(frames.Sequence - 1, out var frame));
        using (frame)
        {
            int rowBytes = frame.Width * 4;
            var pixels = new byte[rowBytes * frame.Height];
            for (int row = 0; row < frame.Height; row++)
            {
                new ReadOnlySpan<byte>((byte*)frame.Pixels + (long)row * frame.Stride, rowBytes).CopyTo(pixels.AsSpan(row * rowBytes));
            }

            return pixels;
        }
    }

    /// <summary>The device a capture would use, and BGRA textures on it wrapped the way WGC hands its frames over.</summary>
    private sealed class TestDevice : IDisposable
    {
        private static readonly Guid DxgiInterfaceAccessIid = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
        private static readonly Guid D3D11DeviceIid = new("db6f6ddb-ac77-4e88-8253-819df9bbf140");
        private static readonly Guid DxgiSurfaceIid = new("cafcb56c-6ac3-4889-bf47-9e23bbd260ec");
        private readonly IntPtr _d3dDevice;

        public TestDevice()
        {
            Device = WgcInterop.CreateDirect3DDevice();
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(((IWinRTObject)Device).NativeObject.ThisPtr, in DxgiInterfaceAccessIid, out IntPtr access));
            try
            {
                IntPtr d3dDevice;
                Guid iid = D3D11DeviceIid;
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Slot(access, 3))(access, &iid, &d3dDevice));
                _d3dDevice = d3dDevice;
            }
            finally
            {
                Marshal.Release(access);
            }
        }

        public IDirect3DDevice Device { get; }

        public (IDirect3DSurface Surface, byte[] Pixels) CreateSurface(int width, int height, byte seed)
        {
            var pixels = new byte[width * height * 4];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = (byte)(seed + (i * 7));
            }

            // B8G8R8A8_UNORM, default usage, bound as a shader resource: like the frame pool's buffers.
            var desc = new Texture2DDesc { Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = 87, SampleCount = 1, BindFlags = 0x8 };
            IntPtr texture;
            fixed (byte* data = pixels)
            {
                var initial = new SubresourceData { SysMem = (IntPtr)data, SysMemPitch = (uint)(width * 4) };
                Marshal.ThrowExceptionForHR(
                    ((delegate* unmanaged[Stdcall]<IntPtr, Texture2DDesc*, SubresourceData*, IntPtr*, int>)Slot(_d3dDevice, 5))(_d3dDevice, &desc, &initial, &texture));
            }

            try
            {
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(texture, in DxgiSurfaceIid, out IntPtr dxgiSurface));
                try
                {
                    Marshal.ThrowExceptionForHR(CreateDirect3D11SurfaceFromDXGISurface(dxgiSurface, out IntPtr inspectable));
                    try
                    {
                        return (MarshalInterface<IDirect3DSurface>.FromAbi(inspectable), pixels);
                    }
                    finally
                    {
                        Marshal.Release(inspectable);
                    }
                }
                finally
                {
                    Marshal.Release(dxgiSurface);
                }
            }
            finally
            {
                Marshal.Release(texture);
            }
        }

        public void Dispose()
        {
            Marshal.Release(_d3dDevice);
            Device.Dispose();
        }

        private static void* Slot(IntPtr com, int index) => (*(void***)com)[index];

        [DllImport("d3d11.dll", ExactSpelling = true)]
        private static extern int CreateDirect3D11SurfaceFromDXGISurface(IntPtr dxgiSurface, out IntPtr graphicsSurface);

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

        private struct SubresourceData // D3D11_SUBRESOURCE_DATA
        {
            public IntPtr SysMem;
            public uint SysMemPitch;
            public uint SysMemSlicePitch;
        }
    }
}
#endif
