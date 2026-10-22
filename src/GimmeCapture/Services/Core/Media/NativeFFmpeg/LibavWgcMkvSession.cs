using System;
using GimmeCapture.Models;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using FFmpeg.AutoGen;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Platforms.Windows;

namespace GimmeCapture.Services.Core.Media.NativeFFmpeg;

/// <summary>
/// Records a specific window via Windows Graphics Capture and writes an H.264/H.265-in-Matroska segment.
/// Unlike <see cref="LibavGdigrabMkvSession"/> (which grabs a fixed desktop rectangle), this pulls BGRA
/// frames from a <see cref="WgcWindowCaptureSource"/>, so the output follows the window as it moves and
/// resizes and keeps capturing it even when occluded or off the originally-selected screen area.
///
/// The output is constant-rate: a stopwatch-paced loop samples the latest captured frame at the target
/// fps (re-emitting the last frame when the window is static or minimized), which keeps the video length
/// aligned to wall-clock time so it stays in sync with the separately-recorded audio segments.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed class LibavWgcMkvSession : LibavRecordingSession
{
    /// <summary>
    /// If WGC delivers no first frame within this window, the start is treated as failed so
    /// <see cref="GimmeCapture.Services.Core.Media.RecordingService"/> can fall back to gdigrab region capture
    /// instead of hanging. Tuned per docs/WGC_HANDOFF.md "Fix A" (dual-monitor "no frames" repro).
    /// 4000ms: the first GPU→CPU readback on a cold D3D/MediaFoundation pipeline (NVIDIA) can take &gt;1.5s; the
    /// off-thread readback (WgcWindowCaptureSource) needs headroom or a working pipeline is failed prematurely.
    /// </summary>
    private const int FirstFrameTimeoutMs = 4000;

    /// <summary>
    /// Hard cap on the WGC/D3D bring-up (<see cref="WgcWindowCaptureSource.Start"/>) itself. On the dual-monitor
    /// repro <c>D3D11CreateDevice</c> / frame-pool creation can block for many seconds, and the first-frame
    /// timeout cannot cover that because it only starts measuring AFTER Start() returns. If bring-up doesn't
    /// finish in time we abandon it (detached) and fail the start so the caller falls back to gdigrab.
    /// </summary>
    private const int BringupTimeoutMs = 1500;

    /// <summary>
    /// True when WGC brought the capture pipeline up but never delivered a first frame (or the bring-up itself
    /// timed out) — the dual-monitor "no frames" repro. Lets the caller stop attempting WGC for the rest of the
    /// session and skip straight to the gdigrab fallback (avoids paying the first-frame timeout every recording).
    /// </summary>
    public bool TimedOutWaitingForFrame { get; private set; }

    // Backstop above bring-up + first-frame: the worker's prime loop / bring-up guard normally fail fast on
    // their own, but if WGC start/dispose blocks somewhere we never reached, still report failure so the
    // caller falls back. Sized to exceed the worker's own worst-case self-termination.
    protected override TimeSpan StartupGateTimeout =>
        TimeSpan.FromMilliseconds(BringupTimeoutMs + FirstFrameTimeoutMs + 2000);

    // The worker self-terminates promptly (bring-up is timeout-guarded, teardown is detached), so this short wait
    // normally returns at once; it just lets a finishing worker close its output file before the caller falls back
    // to gdigrab on the same path. Never blocks the app on a wedged native teardown.
    protected override TimeSpan DisposeWait => TimeSpan.FromSeconds(3);

    protected override bool OnStartupGateTimedOut()
    {
        // Treat the timeout as a FAILED start (was incorrectly reporting started=true, which suppressed the
        // gdigrab fallback and left a frameless WGC worker to hang on stop). See docs/WGC_HANDOFF.md Fix A.
        AppLog.Information("Wgc.StartupGate timeout → started=false (fallback to gdigrab)");
        LogNative("StartupGate: timeout, return started=false (fallback).");
        return false;
    }

    public Task<bool> StartAsync(string outputPath, IntPtr hwnd, int fps, bool drawMouse, VideoCodec codec)
    {
        FFmpegRuntime.EnsureInitialized();
        AppLog.Information($"Wgc.Build sessionType=window bringupTimeoutMs={BringupTimeoutMs} firstFrameTimeoutMs={FirstFrameTimeoutMs}");
        LogNative($"StartAsync requested: out={outputPath}, hwnd={hwnd}, fps={fps}, drawMouse={drawMouse}, codec={codec}");
        return StartWorker((ct, firstFrameTcs) => RunTranscode(outputPath, hwnd, fps, drawMouse, codec, ct, firstFrameTcs));
    }

    private unsafe void RunTranscode(
        string outputPath,
        IntPtr hwnd,
        int fps,
        bool drawMouse,
        VideoCodec codec,
        CancellationToken ct,
        TaskCompletionSource<bool> firstFrameTcs)
    {
        SwsContext* sws = null;
        AVFrame* bgraFrame = null;
        int lastSrcW = 0;
        int lastSrcH = 0;

        // NOT a `using`: WGC teardown can wedge on the dual-monitor "no frames" repro, so the source is disposed
        // on a detached background thread (see the outer finally) — that keeps THIS worker completing promptly so
        // the startup gate / stop / gdigrab fallback never block on a wedged teardown.
        var source = new WgcWindowCaptureSource(hwnd, drawMouse);
        bool sourceAbandoned = false;
        try
        {
        // Bring up WGC/D3D with a hard timeout. On the dual-monitor repro the bring-up itself (D3D11CreateDevice /
        // frame-pool creation) can block for many seconds — which the first-frame timeout cannot cover because it
        // only starts AFTER Start() returns. Run Start() on a throwaway task; if it doesn't finish in time, abandon
        // it (detached) and fail so the caller falls back to gdigrab. See docs/WGC_HANDOFF.md Fix A.
        var bringupClock = Stopwatch.StartNew();
        var bringup = Task.Run(() => source.Start());
        bool finishedInTime;
        try
        {
            finishedInTime = bringup.Wait(BringupTimeoutMs, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            finishedInTime = bringup.IsCompleted; // Start() threw within the window
        }

        if (!finishedInTime || !bringup.IsCompletedSuccessfully || !bringup.Result)
        {
            AppLog.Information($"Wgc.Start.Timeout hwnd=0x{hwnd.ToInt64():X} bringupMs={bringupClock.ElapsedMilliseconds} finishedInTime={finishedInTime} → fallback (abandon)");
            // Only a genuine bring-up *wedge* signals "WGC broken on this box"; a fast Start() returning false is
            // usually a transient (window gone) and must NOT disable WGC for the whole session.
            TimedOutWaitingForFrame = !finishedInTime;
            sourceAbandoned = true;
            WgcWindowCaptureSource.DisposeDetachedAfter(bringup, source, $"hwnd=0x{hwnd.ToInt64():X} (bringup-abandon)");
            throw new InvalidOperationException("Windows Graphics Capture could not start in time for the selected window.");
        }

        // Diagnostics (file log): which GPU WGC bound and which monitor owns the window. On the dual-monitor
        // repro this is the first thing to inspect when no frame arrives. See docs/WGC_HANDOFF.md Fix A.
        AppLog.Information($"Wgc.Start hwnd=0x{hwnd.ToInt64():X} size={source.InitialWidth}x{source.InitialHeight} adapter='{source.AdapterDescription}'");
        AppLog.Information($"Wgc.Start hwnd=0x{hwnd.ToInt64():X} {WgcInterop.DescribeWindowMonitor(hwnd)}");

        int encWidth = MakeEven(source.InitialWidth);
        int encHeight = MakeEven(source.InitialHeight);
        if (encWidth < 2 || encHeight < 2)
        {
            throw new InvalidOperationException($"Invalid capture size {source.InitialWidth}x{source.InitialHeight}.");
        }

        RecordingSegmentWriter? writer = null;
        try
        {
            writer = RecordingSegmentWriter.Open(
                outputPath, codec, PreferHardwareEncoder, encWidth, encHeight, fps, VideoCrf, VideoBitrate);
            AVCodecContext* encCtx = writer.Encoder;

            SelectedEncoderName = writer.EncoderName;
            LastWarningMessage = writer.EncoderWarning;
            if (!string.IsNullOrWhiteSpace(writer.EncoderWarning))
            {
                LogNative(writer.EncoderWarning);
            }
            LogNative($"Selected encoder: {writer.EncoderName} ({encWidth}x{encHeight}@{fps})");

            byte[]? buf = null;

            // Prime: wait for the first captured frame so the encoder sees real content from pts 0. If WGC
            // never delivers a frame (the dual-monitor failure mode) bail out fast so the start is reported as
            // failed and the caller falls back to gdigrab, instead of blocking here forever.
            var primeClock = Stopwatch.StartNew();
            while (!ct.IsCancellationRequested)
            {
                if (source.TryCopyLatest(ref buf, out _, out _) && buf != null)
                {
                    AppLog.Information($"Wgc.FirstFrame hwnd=0x{hwnd.ToInt64():X} arrivedMs={primeClock.ElapsedMilliseconds} size={source.InitialWidth}x{source.InitialHeight}");
                    break;
                }

                if (primeClock.ElapsedMilliseconds >= FirstFrameTimeoutMs)
                {
                    AppLog.Information($"Wgc.FirstFrame.Timeout hwnd=0x{hwnd.ToInt64():X} waitedMs={primeClock.ElapsedMilliseconds} adapter='{source.AdapterDescription}' → fallback");
                    TimedOutWaitingForFrame = true;
                    throw new TimeoutException($"WGC produced no first frame within {FirstFrameTimeoutMs}ms.");
                }

                Thread.Sleep(5);
            }

            var clock = Stopwatch.StartNew();
            while (!ct.IsCancellationRequested)
            {
                if (!RecordingFramePacer.IsFrameDue(clock, writer.FrameCount, fps))
                {
                    continue;
                }

                if (!source.TryCopyLatest(ref buf, out int srcW, out int srcH) || buf == null || srcW <= 0 || srcH <= 0)
                {
                    Thread.Sleep(2);
                    continue;
                }

                if (sws == null || srcW != lastSrcW || srcH != lastSrcH)
                {
                    if (sws != null)
                    {
                        ffmpeg.sws_freeContext(sws);
                        sws = null;
                    }
                    if (bgraFrame != null)
                    {
                        var old = bgraFrame;
                        ffmpeg.av_frame_free(&old);
                        bgraFrame = null;
                    }

                    sws = ffmpeg.sws_getContext(
                        srcW, srcH, AVPixelFormat.AV_PIX_FMT_BGRA,
                        encCtx->width, encCtx->height, encCtx->pix_fmt,
                        (int)SwsFlags.SWS_FAST_BILINEAR, null, null, null);
                    if (sws == null)
                    {
                        throw new InvalidOperationException($"sws_getContext failed for BGRA {srcW}x{srcH} -> {encCtx->pix_fmt} {encCtx->width}x{encCtx->height}.");
                    }

                    bgraFrame = ffmpeg.av_frame_alloc();
                    bgraFrame->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
                    bgraFrame->width = srcW;
                    bgraFrame->height = srcH;
                    ThrowIfErr(ffmpeg.av_frame_get_buffer(bgraFrame, 32), "frame_get_buffer(bgra)");

                    lastSrcW = srcW;
                    lastSrcH = srcH;
                }

                ThrowIfErr(ffmpeg.av_frame_make_writable(bgraFrame), "frame_make_writable(bgra)");
                CopyBgraIntoFrame(buf, srcW, srcH, bgraFrame);

                AVFrame* encFrame = writer.FrameToFill();
                ffmpeg.sws_scale(sws, bgraFrame->data, bgraFrame->linesize, 0, srcH, encFrame->data, encFrame->linesize);

                if (writer.FrameCount == 0)
                {
                    firstFrameTcs.TrySetResult(true);
                }

                writer.EncodeFrame(pts: writer.FrameCount);
            }

            writer.Flush();
            writer.WriteTrailer();

            if (writer.FrameCount == 0)
            {
                throw new InvalidOperationException("WGC recording pipeline produced zero frames.");
            }

            AppLog.Information($"Wgc.Stop hwnd=0x{hwnd.ToInt64():X} frames={writer.FrameCount} packets={writer.PacketCount}");
            AppLog.Debug("LibavWgcMkvSession", $"[LibavWgc] segment done: frames={writer.FrameCount}, packets={writer.PacketCount}, output='{outputPath}'");
        }
        finally
        {
            writer?.Dispose();
            SafeFreeFrame(&bgraFrame);

            if (sws != null)
            {
                ffmpeg.sws_freeContext(sws);
            }
        }
        }
        finally
        {
            // Detach so a wedged WGC teardown can't keep this worker alive (which would block stop / fallback).
            // Skip when the bring-up was abandoned: that path already handed the source to DisposeDetachedAfter
            // (a still-running Start() owns it), so disposing again here would race its field writes.
            if (!sourceAbandoned)
            {
                WgcWindowCaptureSource.DisposeDetached(source, $"hwnd=0x{hwnd.ToInt64():X}");
            }
        }
    }

    /// <summary>Copies a tightly-packed BGRA buffer into a libav BGRA frame, respecting its padded stride.</summary>
    private static unsafe void CopyBgraIntoFrame(byte[] buf, int width, int height, AVFrame* bgraFrame)
    {
        int rowBytes = width * 4;
        int stride = bgraFrame->linesize[0];
        byte* dstBase = bgraFrame->data[0];
        fixed (byte* srcBase = buf)
        {
            for (int row = 0; row < height; row++)
            {
                Buffer.MemoryCopy(srcBase + (long)row * rowBytes, dstBase + (long)row * stride, rowBytes, rowBytes);
            }
        }
    }

    private static int MakeEven(int value) => value % 2 == 0 ? value : value - 1;

    private static unsafe void SafeFreeFrame(AVFrame** frame)
    {
        if (frame != null && *frame != null)
        {
            ffmpeg.av_frame_free(frame);
        }
    }

    private static void ThrowIfErr(int err, string ctx)
    {
        if (err < 0)
        {
            throw FFmpegErrors.ToException(err, ctx);
        }
    }
}
