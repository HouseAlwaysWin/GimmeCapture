using System;
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
internal sealed class LibavWgcMkvSession : IDisposable
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

    private CancellationTokenSource? _cts;
    private Task<bool>? _worker;

    public Task<bool>? Worker => _worker;
    public string? LastErrorMessage { get; private set; }
    public string? LastWarningMessage { get; private set; }
    public string? SelectedEncoderName { get; private set; }

    /// <summary>
    /// True when WGC brought the capture pipeline up but never delivered a first frame (or the bring-up itself
    /// timed out) — the dual-monitor "no frames" repro. Lets the caller stop attempting WGC for the rest of the
    /// session and skip straight to the gdigrab fallback (avoids paying the first-frame timeout every recording).
    /// </summary>
    public bool TimedOutWaitingForFrame { get; private set; }

    /// <summary>
    /// When true (default) GPU / Media-Foundation encoders (NVENC/QSV/AMF/MF) are tried before the
    /// software libx264/265 fallback.
    /// </summary>
    public bool PreferHardwareEncoder { get; set; } = true;

    public Task<bool> StartAsync(string outputPath, IntPtr hwnd, int fps, bool drawMouse, bool useH265)
    {
        FFmpegRuntime.EnsureInitialized();
        AppLog.Information($"Wgc.Build sessionType=window bringupTimeoutMs={BringupTimeoutMs} firstFrameTimeoutMs={FirstFrameTimeoutMs}");
        LogNative($"StartAsync requested: out={outputPath}, hwnd={hwnd}, fps={fps}, drawMouse={drawMouse}, useH265={useH265}");
        LastErrorMessage = null;
        LastWarningMessage = null;
        SelectedEncoderName = null;

        try
        {
            _cts?.Cancel();
            _worker?.Wait(TimeSpan.FromMilliseconds(500));
        }
        catch
        {
            // ignore; we are replacing the session
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _worker = null;
        }

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var firstFrameTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _worker = Task.Run(() =>
        {
            try
            {
                RunTranscode(outputPath, hwnd, fps, drawMouse, useH265, ct, firstFrameTcs);
                LogNative("Worker completed successfully.");
                return true;
            }
            catch (OperationCanceledException)
            {
                firstFrameTcs.TrySetCanceled();
                LogNative("Worker canceled.");
                return false;
            }
            catch (Exception ex)
            {
                LastErrorMessage = ex.Message;
                Debug.WriteLine($"[LibavWgc] {ex}");
                LogNative($"Worker exception: {ex}");
                firstFrameTcs.TrySetException(ex);
                return false;
            }
        }, CancellationToken.None);

        return StartupGateAsync(_worker, firstFrameTcs.Task);
    }

    private static async Task<bool> StartupGateAsync(Task<bool> worker, Task<bool> firstFrame)
    {
        // Backstop above bring-up + first-frame: the worker's prime loop / bring-up guard normally fail fast on
        // their own, but if WGC start/dispose blocks somewhere we never reached, still report failure so the
        // caller falls back. Sized to exceed the worker's own worst-case self-termination.
        var timeout = Task.Delay(BringupTimeoutMs + FirstFrameTimeoutMs + 2000);
        var done = await Task.WhenAny(firstFrame, worker, timeout).ConfigureAwait(false);

        // firstFrame faults (TrySetException) when the worker bails before a real frame. On the worker/timeout
        // fallback paths below we don't await it, so observe its exception to avoid UnobservedTaskException noise.
        ObserveFaultedTask(firstFrame);

        if (done == firstFrame)
        {
            LogNative("StartupGate: first frame observed.");
            return await firstFrame.ConfigureAwait(false);
        }

        if (done == worker)
        {
            LogNative("StartupGate: worker completed before first frame.");
            return await worker.ConfigureAwait(false);
        }

        // Treat the timeout as a FAILED start (was incorrectly reporting started=true, which suppressed the
        // gdigrab fallback and left a frameless WGC worker to hang on stop). See docs/WGC_HANDOFF.md Fix A.
        AppLog.Information("Wgc.StartupGate timeout → started=false (fallback to gdigrab)");
        LogNative("StartupGate: timeout, return started=false (fallback).");
        return false;
    }

    /// <summary>Reads a faulted task's exception so it isn't surfaced as an UnobservedTaskException ([ERR] noise).</summary>
    private static void ObserveFaultedTask(Task t) =>
        t.ContinueWith(
            static x => { _ = x.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    public async Task StopAsync()
    {
        try
        {
            LogNative("StopAsync requested.");
            _cts?.Cancel();
            if (_worker != null)
            {
                await _worker.ConfigureAwait(false);
            }
        }
        finally
        {
            _worker = null;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private unsafe void RunTranscode(
        string outputPath,
        IntPtr hwnd,
        int fps,
        bool drawMouse,
        bool useH265,
        CancellationToken ct,
        TaskCompletionSource<bool> firstFrameTcs)
    {
        AVFormatContext* outputFmt = null;
        AVCodecContext* encCtx = null;
        SwsContext* sws = null;
        AVPacket* pkt = null;
        AVFrame* encFrame = null;
        AVFrame* bgraFrame = null;
        AVDictionary* encOpts = null;

        AVStream* outStream;
        long frameCounter = 0;
        long packetCounter = 0;
        bool encFrameInitialized = false;
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

        pkt = ffmpeg.av_packet_alloc();
        encFrame = ffmpeg.av_frame_alloc();

        try
        {
            encCtx = LibavRecordingEncoder.OpenRecordingEncoderContext(
                useH265, PreferHardwareEncoder, encWidth, encHeight, fps, &encOpts,
                out string encName, out string? warningMessage);
            if (encCtx == null)
            {
                throw new InvalidOperationException("No compatible video encoder available for recording.");
            }

            SelectedEncoderName = encName;
            LastWarningMessage = warningMessage;
            if (!string.IsNullOrWhiteSpace(warningMessage))
            {
                LogNative(warningMessage);
            }
            LogNative($"Selected encoder: {encName} ({encWidth}x{encHeight}@{fps})");

            ThrowIfErr(
                ffmpeg.avformat_alloc_output_context2(&outputFmt, null, "matroska", outputPath),
                "avformat_alloc_output_context2");

            outStream = ffmpeg.avformat_new_stream(outputFmt, null);
            if (outStream == null)
            {
                throw new OutOfMemoryException("new_stream");
            }

            ThrowIfErr(ffmpeg.avcodec_parameters_from_context(outStream->codecpar, encCtx), "codec_parameters_from_context");
            outStream->codecpar->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
            outStream->codecpar->codec_id = encCtx->codec_id;
            outStream->time_base = encCtx->time_base;
            outStream->avg_frame_rate = encCtx->framerate;
            outStream->r_frame_rate = encCtx->framerate;
            outStream->codecpar->codec_tag = 0;

            if ((outputFmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
            {
                ThrowIfErr(ffmpeg.avio_open(&outputFmt->pb, outputPath, ffmpeg.AVIO_FLAG_WRITE), "avio_open");
            }

            ThrowIfErr(ffmpeg.avformat_write_header(outputFmt, null), "write_header");

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
                long target = (long)(clock.Elapsed.TotalSeconds * fps);
                if (frameCounter > target)
                {
                    double nextDueMs = frameCounter * 1000.0 / fps;
                    double sleepMs = nextDueMs - clock.Elapsed.TotalMilliseconds;
                    Thread.Sleep(sleepMs > 1 ? (int)Math.Min(sleepMs, 16) : 1);
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

                if (!encFrameInitialized)
                {
                    encFrame->format = (int)encCtx->pix_fmt;
                    encFrame->width = encCtx->width;
                    encFrame->height = encCtx->height;
                    ThrowIfErr(ffmpeg.av_frame_get_buffer(encFrame, 32), "frame_get_buffer(enc)");
                    encFrameInitialized = true;
                }

                ThrowIfErr(ffmpeg.av_frame_make_writable(bgraFrame), "frame_make_writable(bgra)");
                CopyBgraIntoFrame(buf, srcW, srcH, bgraFrame);

                ThrowIfErr(ffmpeg.av_frame_make_writable(encFrame), "frame_make_writable(enc)");
                ffmpeg.sws_scale(sws, bgraFrame->data, bgraFrame->linesize, 0, srcH, encFrame->data, encFrame->linesize);

                encFrame->pts = frameCounter;
                if (frameCounter == 0)
                {
                    firstFrameTcs.TrySetResult(true);
                }
                frameCounter++;

                ThrowIfErr(ffmpeg.avcodec_send_frame(encCtx, encFrame), "send_frame(enc)");
                LibavRecordingEncoder.WriteEncodedPackets(encCtx, outputFmt, outStream, pkt, ref packetCounter);
            }

            ThrowIfErr(ffmpeg.avcodec_send_frame(encCtx, null), "flush_encoder");
            LibavRecordingEncoder.WriteEncodedPackets(encCtx, outputFmt, outStream, pkt, ref packetCounter);

            ffmpeg.av_write_trailer(outputFmt);

            if (frameCounter == 0)
            {
                throw new InvalidOperationException("WGC recording pipeline produced zero frames.");
            }

            AppLog.Information($"Wgc.Stop hwnd=0x{hwnd.ToInt64():X} frames={frameCounter} packets={packetCounter}");
            Debug.WriteLine($"[LibavWgc] segment done: frames={frameCounter}, packets={packetCounter}, output='{outputPath}'");
        }
        finally
        {
            SafeFreePkt(&pkt);
            SafeFreeFrame(&encFrame);
            SafeFreeFrame(&bgraFrame);

            if (sws != null)
            {
                ffmpeg.sws_freeContext(sws);
            }

            if (encCtx != null)
            {
                ffmpeg.avcodec_free_context(&encCtx);
            }

            if (outputFmt != null)
            {
                if ((outputFmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0 && outputFmt->pb != null)
                {
                    ffmpeg.avio_closep(&outputFmt->pb);
                }

                ffmpeg.avformat_free_context(outputFmt);
            }

            if (encOpts != null)
            {
                ffmpeg.av_dict_free(&encOpts);
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

    private static unsafe void SafeFreePkt(AVPacket** pkt)
    {
        if (pkt != null && *pkt != null)
        {
            ffmpeg.av_packet_free(pkt);
        }
    }

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

    public void Dispose()
    {
        try
        {
            _cts?.Cancel();
            // The worker now self-terminates promptly (bring-up is timeout-guarded, teardown is detached), so this
            // short wait normally returns at once; it just lets a finishing worker close its output file before the
            // caller falls back to gdigrab on the same path. Never blocks the app on a wedged native teardown.
            _worker?.Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // ignore
        }

        _cts?.Dispose();
        _cts = null;
        _worker = null;
    }

    private static void LogNative(string message)
    {
        System.Diagnostics.Debug.WriteLine($"[RecordingNative] {message}");
    }
}
