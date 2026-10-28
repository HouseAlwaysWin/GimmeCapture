using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Services.Core.Infrastructure;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace GimmeCapture.Services.Platforms.Windows;

/// <summary>
/// Continuously captures a single top-level window via Windows Graphics Capture (WGC), keeping only the
/// latest frame, as BGRA, in a <see cref="WgcFrameExchange"/> (read back from the GPU by a
/// <see cref="WgcSurfaceReader"/>). The single-window encoder reads each new frame in place
/// (<see cref="TryReadFrameAfter"/>); the multi-window one copies the latest (<see cref="TryCopyLatest"/>).
/// Because WGC captures the window's own composited surface, the recording follows the window as it
/// moves/resizes and keeps recording it even when occluded — the gap that the gdigrab desktop-region path
/// could not fill.
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

    private readonly object _readbackLock = new();  // serializes FrameArrived readback (no reentrancy)
    private readonly WgcFrameExchange _frames = new();
    private volatile WgcSurfaceReader? _reader;

    private int _frameArrivedCount;                   // diagnostic: how many FrameArrived callbacks fired
    private int _readbackInFlight;                     // 0/1 guard: at most one off-thread readback at a time

    public WgcWindowCaptureSource(IntPtr hwnd, bool captureCursor)
    {
        _hwnd = hwnd;
        _captureCursor = captureCursor;
    }

    public static bool IsSupported => WgcInterop.IsSupported();

    /// <summary>Window size (physical px) observed at <see cref="Start"/>; used to size the fixed encoder.</summary>
    public int InitialWidth { get; private set; }
    public int InitialHeight { get; private set; }

    /// <summary>DXGI description of the GPU adapter WGC bound to (diagnostic; "(unknown)" until <see cref="Start"/>).</summary>
    public string AdapterDescription { get; private set; } = "(unknown)";

    /// <summary>Creates the device/item/pool/session and begins capturing. False if WGC is unavailable or the window is gone.</summary>
    public bool Start()
    {
        AppLog.Information(
            $"Wgc.Start.Thread apt={Thread.CurrentThread.GetApartmentState()} tid={Environment.CurrentManagedThreadId} hwnd=0x{_hwnd.ToInt64():X}");

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

        _device = WgcInterop.CreateDirect3DDevice(out string adapterDescription);
        AdapterDescription = adapterDescription;
        _reader = new WgcSurfaceReader(_device);
        _poolSize = size;
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, PixelFormat, FramePoolBuffers, size);
        _session = _framePool.CreateCaptureSession(_item);
        TrySetCursorCapture(_session, _captureCursor);

        _framePool.FrameArrived += OnFrameArrived;
        _session.StartCapture();
        return true;
    }

    /// <summary>How many frames have been read back so far: it changes whenever a new frame is in.</summary>
    public long FramesReadBack => _frames.Sequence;

    /// <summary>Copies the latest captured frame (BGRA, tightly packed) into a caller buffer. False if none yet.</summary>
    public bool TryCopyLatest(ref byte[]? buffer, out int width, out int height) =>
        _frames.TryCopyLatest(ref buffer, out width, out height);

    /// <summary>
    /// The latest captured frame if it came in after the one numbered <paramref name="afterSequence"/> (0: any), to
    /// read in place: its buffer is not written again until <paramref name="frame"/> is disposed. One at a time.
    /// </summary>
    public bool TryReadFrameAfter(long afterSequence, out WgcFrameExchange.FrameLease frame) =>
        _frames.TryRead(afterSequence, out frame);

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        int n = Interlocked.Increment(ref _frameArrivedCount);
        if (n <= 3)
        {
            AppLog.Information(
                $"Wgc.Source.FrameArrived#{n} thread={Environment.CurrentManagedThreadId} apt={Thread.CurrentThread.GetApartmentState()}");
        }
        else if (n % 300 == 0)
        {
            // Still arriving: worth a line when chasing a problem, not in every recording's log.
            AppLog.Debug(
                "Wgc.Source.FrameArrived",
                $"#{n} thread={Environment.CurrentManagedThreadId} apt={Thread.CurrentThread.GetApartmentState()}");
        }

        Direct3D11CaptureFrame? frame = null;
        try
        {
            // Drain to the most recent queued frame, disposing the older ones so the pool can reuse buffers.
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

            // Follow window resizes: recreate the pool at the new content size so capture keeps working.
            var content = frame.ContentSize;
            if (content.Width > 0 && content.Height > 0
                && (content.Width != _poolSize.Width || content.Height != _poolSize.Height)
                && _device != null)
            {
                _poolSize = content;
                sender.Recreate(_device, PixelFormat, FramePoolBuffers, content);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Wgc.Source.FrameArrived.Drain", ex);
            frame?.Dispose();
            return;
        }

        // The readback does not run on this free-threaded WGC callback thread. It once did, as
        // `SoftwareBitmap.CreateCopyFromSurfaceAsync(...).GetAwaiter().GetResult()` — blocking the callback thread on
        // a WinRT async whose completion is marshaled back to a thread we are blocking. On the dual-monitor repro that
        // deadlocked: the handler never returned, no frame was ever published, and it looked exactly like "WGC
        // delivers no frames". The readback is plain D3D now, but it still waits for the GPU, so the newest frame
        // goes to a thread-pool readback and this returns at once. While a readback is in flight we drop newer
        // frames — only the latest matters and the pool has limited buffers.
        if (Interlocked.CompareExchange(ref _readbackInFlight, 1, 0) != 0)
        {
            frame.Dispose();
            return;
        }

        Direct3D11CaptureFrame readbackFrame = frame;
        int seq = n;
        _ = Task.Run(() =>
        {
            long started = Stopwatch.GetTimestamp();
            if (seq <= 3)
            {
                AppLog.Information($"Wgc.Source.Readback.Begin#{seq}");
            }

            try
            {
                // Into a buffer the encoder is not reading, reused frame after frame. Each frame used to be a new
                // SoftwareBitmap and a new array of the frame's size, and the bitmap's GC memory pressure set off a
                // gen2 collection every three frames of a window that kept changing.
                int w = 0;
                int h = 0;
                WgcSurfaceReader? reader = _reader;
                bool read = reader != null && reader.TryRead(readbackFrame.Surface, _frames, out w, out h);
                if (seq <= 3)
                {
                    AppLog.Information(
                        $"Wgc.Source.Readback.End#{seq} tookMs={(long)Stopwatch.GetElapsedTime(started).TotalMilliseconds} {w}x{h} read={read}");
                }
            }
            catch (Exception ex)
            {
                // A transient readback failure must never crash the recording; the last good frame stays available.
                AppLog.Error("Wgc.Source.Readback", ex);
            }
            finally
            {
                readbackFrame.Dispose();
                Interlocked.Exchange(ref _readbackInFlight, 0);
            }
        });
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

    /// <summary>
    /// Disposes a capture source on a detached background thread so a wedged WGC teardown never stalls the
    /// caller. On the dual-monitor "no frames" repro the frame pool / capture session <see cref="Dispose"/>
    /// (or a readback stuck holding the readback lock) can block indefinitely; if the recording worker waited
    /// on that, stop / pin / the start→gdigrab fallback would all hang and the app becomes unclosable. The
    /// disposer thread is a background thread and may leak if the native teardown never returns — a small leak
    /// is preferable to a frozen app. See docs/WGC_HANDOFF.md "Fix A".
    /// </summary>
    public static void DisposeDetached(WgcWindowCaptureSource? source, string label)
    {
        if (source == null)
        {
            return;
        }

        DisposeDetachedAfter(null, source, label);
    }

    /// <summary>
    /// Like <see cref="DisposeDetached"/>, but first waits for a still-running <paramref name="pending"/>
    /// <see cref="Start"/> task to settle before disposing. Used when a WGC bring-up (<c>Start()</c>) is
    /// abandoned on timeout: <c>Start()</c> may still be mutating the source's fields on a throwaway thread,
    /// so we must not dispose underneath it. Both the wait and the dispose run on a detached background thread
    /// that may leak if the native bring-up never returns — preferable to a frozen app. See Fix A.
    /// </summary>
    public static void DisposeDetachedAfter(Task? pending, WgcWindowCaptureSource? source, string label)
    {
        if (source == null)
        {
            return;
        }

        // Remove the Windows WGC capture border NOW, on the caller's thread, before anything that can wedge:
        // disposing the session is fast and must not be gated behind the (possibly wedged) pending Start()
        // wait or the readback lock. The heavy frame-pool/device teardown still runs detached below (it may
        // wedge harmlessly), but the yellow "being captured" border is already gone.
        try { source.StopSessionImmediate(); } catch { /* best effort */ }

        var thread = new Thread(() =>
        {
            // Let the (possibly wedged) Start() settle first so we never race its field writes — but bound the
            // wait so a permanently wedged bring-up can't keep this disposer (and the source) alive forever.
            try
            {
                pending?.Wait(3000);
            }
            catch
            {
                // Start() faulted/canceled — dispose whatever it managed to build anyway.
            }

            var sw = Stopwatch.StartNew();
            try
            {
                source.Dispose();
            }
            catch (Exception ex)
            {
                AppLog.Error("Wgc.Source.DisposeDetached", ex);
            }

            if (sw.ElapsedMilliseconds > 750)
            {
                AppLog.Information(
                    $"Wgc.Source.DisposeDetached {label} tookMs={sw.ElapsedMilliseconds} (slow/wedged WGC teardown abandoned in background)");
            }
        })
        {
            IsBackground = true,
            Name = "WgcSourceDispose",
        };
        thread.Start();
    }

    /// <summary>
    /// Synchronously stops the capture session WITHOUT taking the readback lock or waiting on anything.
    /// Disposing the <see cref="GraphicsCaptureSession"/> is the ONLY thing that removes Windows' yellow
    /// "this window is being captured" border (on Windows 10 there is no IsBorderRequired to suppress it),
    /// so it must run even when the heavy frame-pool/device teardown — or an in-flight <c>FrameArrived</c>
    /// holding <c>_readbackLock</c> — is wedged (the dual-monitor "no frames" repro). Idempotent; any thread.
    /// </summary>
    public void StopSessionImmediate()
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

        GraphicsCaptureSession? session = Interlocked.Exchange(ref _session, null);
        if (session != null)
        {
            // This is what clears the on-screen WGC capture border. Do it outside any lock/wait.
            try { session.Dispose(); } catch { /* best effort */ }
        }
    }

    public void Dispose()
    {
        // Kill the capture session (and the Windows yellow capture border) FIRST, outside the readback lock,
        // so the border is removed even if an in-flight FrameArrived is wedged holding _readbackLock.
        StopSessionImmediate();

        // Take the readback lock so we don't dispose underneath an in-flight FrameArrived.
        lock (_readbackLock)
        {
            try { _framePool?.Dispose(); } catch { /* best effort */ }
            try { _device?.Dispose(); } catch { /* best effort */ }
            _framePool = null;
            _device = null;
            _item = null;
        }

        // After a readback still in flight, which publishes into nothing; a frame the encoder is reading stays valid
        // until it is released.
        _reader?.Dispose();
        _frames.Dispose();
    }
}
