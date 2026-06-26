using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.Infrastructure;
using SkiaSharp;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using WinRT;

namespace GimmeCapture.Services.Platforms.Windows;

/// <summary>
/// Step 2 WGC probe: grabs one frame of a top-level window through the Windows Graphics Capture API and
/// saves it as a PNG, proving the DirectX/WinRT interop works and produces a non-black image for
/// GPU/DWM-composited windows (where the old gdigrab BitBlt path failed). The BGRA readback here is the
/// same path that Step 3 will feed into the encoder.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WgcWindowCaptureProbe : IWgcWindowCaptureProbe
{
    // IID of Windows.Graphics.Capture.IGraphicsCaptureItem — passed to the interop factory's CreateForWindow.
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    public bool IsSupported
    {
        get
        {
            try
            {
                return GraphicsCaptureSession.IsSupported();
            }
            catch (Exception ex)
            {
                AppLog.Error("Wgc.IsSupported", ex);
                return false;
            }
        }
    }

    public async Task<bool> CaptureWindowToPngAsync(IntPtr hwnd, string outputPngPath)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        if (!IsSupported)
        {
            AppLog.Information("Wgc.Probe.Unsupported: Windows Graphics Capture is not supported on this machine.");
            return false;
        }

        IDirect3DDevice? device = null;
        Direct3D11CaptureFramePool? framePool = null;
        GraphicsCaptureSession? session = null;
        try
        {
            var item = CreateCaptureItemForWindow(hwnd);
            if (item.Size.Width <= 0 || item.Size.Height <= 0)
            {
                AppLog.Information($"Wgc.Probe.EmptyItem: capture item has empty size {item.Size.Width}x{item.Size.Height}.");
                return false;
            }

            device = CreateDirect3DDevice();

            // Free-threaded pool: FrameArrived fires on a thread-pool thread, so we don't need a
            // DispatcherQueue on the calling thread.
            framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
            session = framePool.CreateCaptureSession(item);

            var frameReady = new TaskCompletionSource<Direct3D11CaptureFrame>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            void OnFrameArrived(Direct3D11CaptureFramePool pool, object _)
            {
                var arrived = pool.TryGetNextFrame();
                if (arrived == null)
                {
                    return;
                }

                // Hand the first frame off to the awaiter; dispose it ourselves if it lost the race.
                if (!frameReady.TrySetResult(arrived))
                {
                    arrived.Dispose();
                }
            }

            framePool.FrameArrived += OnFrameArrived;
            session.StartCapture();

            var completed = await Task.WhenAny(frameReady.Task, Task.Delay(TimeSpan.FromSeconds(3)))
                .ConfigureAwait(false);
            framePool.FrameArrived -= OnFrameArrived;

            if (completed != frameReady.Task)
            {
                AppLog.Information("Wgc.Probe.Timeout: no WGC frame arrived within 3s.");
                return false;
            }

            using Direct3D11CaptureFrame frame = await frameReady.Task.ConfigureAwait(false);
            using var softwareBitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(
                frame.Surface, BitmapAlphaMode.Premultiplied);

            bool saved = SaveSoftwareBitmapAsPng(softwareBitmap, outputPngPath);
            if (saved)
            {
                AppLog.Information($"Wgc.Probe.Saved: WGC probe PNG written to {outputPngPath}");
            }

            return saved;
        }
        catch (Exception ex)
        {
            AppLog.Error("Wgc.Probe.Capture", ex);
            return false;
        }
        finally
        {
            session?.Dispose();
            framePool?.Dispose();
            device?.Dispose();
        }
    }

    private static bool SaveSoftwareBitmapAsPng(SoftwareBitmap softwareBitmap, string outputPngPath)
    {
        using var buffer = softwareBitmap.LockBuffer(BitmapBufferAccessMode.Read);
        var plane = buffer.GetPlaneDescription(0);
        using var reference = buffer.CreateReference();

        unsafe
        {
            var byteAccess = reference.As<IMemoryBufferByteAccess>();
            byteAccess.GetBuffer(out byte* dataPtr, out uint capacity);
            if (dataPtr == null || capacity == 0)
            {
                return false;
            }

            var info = new SKImageInfo(plane.Width, plane.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bitmap = new SKBitmap();
            if (!bitmap.InstallPixels(info, (IntPtr)dataPtr, plane.Stride))
            {
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPngPath) ?? ".");
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = File.Create(outputPngPath);
            data.SaveTo(stream);
            return true;
        }
    }

    private static GraphicsCaptureItem CreateCaptureItemForWindow(IntPtr hwnd)
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

    private static IDirect3DDevice CreateDirect3DDevice()
    {
        const uint D3D11_SDK_VERSION = 7;
        const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
        const uint D3D_DRIVER_TYPE_HARDWARE = 1;
        const uint D3D_DRIVER_TYPE_WARP = 5;

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

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(
        IntPtr pAdapter, uint driverType, IntPtr software, uint flags,
        IntPtr pFeatureLevels, uint featureLevels, uint sdkVersion,
        out IntPtr ppDevice, out uint pFeatureLevel, out IntPtr ppImmediateContext);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

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
