using System;
using System.Runtime.Versioning;
using System.Threading;
using GimmeCapture.Services.Core.Infrastructure;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

namespace GimmeCapture.Services.Platforms.Windows;

/// <summary>
/// Continuously captures a single top-level window via Windows Graphics Capture (WGC), keeping only the
/// latest frame as a tightly-packed BGRA buffer (lock-protected). The recording encode loop polls
/// <see cref="TryCopyLatest"/> to pull frames. Because WGC captures the window's own composited surface,
/// the recording follows the window as it moves/resizes and keeps recording it even when occluded — the
/// gap that the gdigrab desktop-region path could not fill.
///
/// Mirrors the latest-frame-under-a-lock shape of
/// <see cref="GimmeCapture.Services.Core.Media.NativeFFmpeg.WebcamCaptureSource"/>.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed class WgcWindowCaptureSource : IDisposable
{
    private const DirectXPixelFormat PixelFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;
    private const int FramePoolBuffers = 2;

    private readonly IntPtr _hwnd;
    private readonly bool _captureCursor;

    private IDirect3DDevice? _device;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private SizeInt32 _poolSize;

    private readonly object _frameLock = new();    // guards _latest / _width / _height
    private readonly object _readbackLock = new();  // serializes FrameArrived readback (no reentrancy)
    private byte[]? _latest;                          // BGRA, tightly packed (stride == width*4)
    private int _width;
    private int _height;

    public WgcWindowCaptureSource(IntPtr hwnd, bool captureCursor)
    {
        _hwnd = hwnd;
        _captureCursor = captureCursor;
    }

    public static bool IsSupported => WgcInterop.IsSupported();

    /// <summary>Window size (physical px) observed at <see cref="Start"/>; used to size the fixed encoder.</summary>
    public int InitialWidth { get; private set; }
    public int InitialHeight { get; private set; }

    /// <summary>Creates the device/item/pool/session and begins capturing. False if WGC is unavailable or the window is gone.</summary>
    public bool Start()
    {
        if (!WgcInterop.IsSupported())
        {
            return false;
        }

        _item = WgcInterop.CreateCaptureItemForWindow(_hwnd);
        var size = _item.Size;
        if (size.Width <= 0 || size.Height <= 0)
        {
            return false;
        }

        InitialWidth = size.Width;
        InitialHeight = size.Height;

        _device = WgcInterop.CreateDirect3DDevice();
        _poolSize = size;
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, PixelFormat, FramePoolBuffers, size);
        _session = _framePool.CreateCaptureSession(_item);
        TrySetCursorCapture(_session, _captureCursor);

        _framePool.FrameArrived += OnFrameArrived;
        _session.StartCapture();
        return true;
    }

    /// <summary>Copies the latest captured frame (BGRA, tightly packed) into a caller buffer. False if none yet.</summary>
    public bool TryCopyLatest(ref byte[]? buffer, out int width, out int height)
    {
        lock (_frameLock)
        {
            if (_latest == null)
            {
                width = 0;
                height = 0;
                return false;
            }

            if (buffer == null || buffer.Length < _latest.Length)
            {
                buffer = new byte[_latest.Length];
            }

            Array.Copy(_latest, buffer, _latest.Length);
            width = _width;
            height = _height;
            return true;
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        // Skip if a readback is already running; the next FrameArrived will pick up the most recent frame.
        if (!Monitor.TryEnter(_readbackLock))
        {
            return;
        }

        try
        {
            // Drain to the most recent queued frame, disposing the older ones so the pool can reuse buffers.
            Direct3D11CaptureFrame? frame = null;
            while (true)
            {
                var next = sender.TryGetNextFrame();
                if (next == null)
                {
                    break;
                }

                frame?.Dispose();
                frame = next;
            }

            if (frame == null)
            {
                return;
            }

            try
            {
                // Follow window resizes: recreate the pool at the new content size so capture keeps working.
                var content = frame.ContentSize;
                if (content.Width > 0 && content.Height > 0
                    && (content.Width != _poolSize.Width || content.Height != _poolSize.Height)
                    && _device != null)
                {
                    _poolSize = content;
                    sender.Recreate(_device, PixelFormat, FramePoolBuffers, content);
                }

                using var softwareBitmap = SoftwareBitmap.CreateCopyFromSurfaceAsync(
                    frame.Surface, BitmapAlphaMode.Premultiplied).GetAwaiter().GetResult();
                var bgra = WgcInterop.CopyBgra(softwareBitmap, out int w, out int h);
                if (bgra != null)
                {
                    lock (_frameLock)
                    {
                        _latest = bgra;
                        _width = w;
                        _height = h;
                    }
                }
            }
            finally
            {
                frame.Dispose();
            }
        }
        catch (Exception ex)
        {
            // A transient readback failure must never crash the recording; the last good frame stays available.
            AppLog.Error("Wgc.Source.FrameArrived", ex);
        }
        finally
        {
            Monitor.Exit(_readbackLock);
        }
    }

    private static void TrySetCursorCapture(GraphicsCaptureSession session, bool enabled)
    {
        try
        {
            session.IsCursorCaptureEnabled = enabled;
        }
        catch
        {
            // IsCursorCaptureEnabled is unavailable on some builds; the default (cursor shown) is fine.
        }
    }

    public void Dispose()
    {
        try
        {
            if (_framePool != null)
            {
                _framePool.FrameArrived -= OnFrameArrived;
            }
        }
        catch
        {
            // best effort
        }

        // Take the readback lock so we don't dispose underneath an in-flight FrameArrived.
        lock (_readbackLock)
        {
            try { _session?.Dispose(); } catch { /* best effort */ }
            try { _framePool?.Dispose(); } catch { /* best effort */ }
            try { _device?.Dispose(); } catch { /* best effort */ }
            _session = null;
            _framePool = null;
            _device = null;
            _item = null;
        }

        lock (_frameLock)
        {
            _latest = null;
        }
    }
}
