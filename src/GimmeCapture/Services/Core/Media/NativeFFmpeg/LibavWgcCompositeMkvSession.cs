using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using FFmpeg.AutoGen;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Media;
using GimmeCapture.Services.Platforms.Windows;
using SkiaSharp;

namespace GimmeCapture.Services.Core.Media.NativeFFmpeg;

/// <summary>
/// Records several windows at once via Windows Graphics Capture, tiling them into an auto-grid on a single
/// canvas and encoding ONE H.264/H.265-in-Matroska segment. Each window is an independent
/// <see cref="WgcWindowCaptureSource"/>; every output frame composites the latest BGRA of each source into
/// its grid cell (aspect-fit) with SkiaSharp, then runs the same sws → encoder → muxer path as
/// <see cref="LibavWgcMkvSession"/>. The single output means the existing audio/finalize pipeline is reused
/// unchanged.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed class LibavWgcCompositeMkvSession : IDisposable
{
    /// <summary>
    /// If no captured window has delivered a frame within this window, the start is treated as failed so the
    /// caller can fall back to gdigrab region capture rather than hang. See docs/WGC_HANDOFF.md "Fix A".
    /// 4000ms gives the cold first off-thread GPU→CPU readback headroom; see LibavWgcMkvSession for rationale.
    /// </summary>
    private const int FirstFrameTimeoutMs = 4000;

    /// <summary>
    /// Hard cap on each window's WGC/D3D bring-up (<see cref="WgcWindowCaptureSource.Start"/>). On the
    /// dual-monitor repro the bring-up itself can block for many seconds (not covered by the first-frame
    /// timeout). All windows are brought up concurrently under this single shared cap; any that don't finish
    /// in time are abandoned (detached) and skipped. See docs/WGC_HANDOFF.md Fix A.
    /// </summary>
    private const int BringupTimeoutMs = 1500;

    private CancellationTokenSource? _cts;
    private Task<bool>? _worker;

    public Task<bool>? Worker => _worker;
    public string? LastErrorMessage { get; private set; }
    public string? LastWarningMessage { get; private set; }
    public string? SelectedEncoderName { get; private set; }
    public bool PreferHardwareEncoder { get; set; } = true;

    /// <summary>True when no captured window delivered a first frame — the "no frames" repro. See
    /// <see cref="LibavWgcMkvSession.TimedOutWaitingForFrame"/>.</summary>
    public bool TimedOutWaitingForFrame { get; private set; }

    public Task<bool> StartAsync(string outputPath, IReadOnlyList<IntPtr> hwnds, int fps, bool drawMouse, bool useH265)
    {
        FFmpegRuntime.EnsureInitialized();
        AppLog.Information($"Wgc.Build sessionType=composite windows={hwnds.Count} bringupTimeoutMs={BringupTimeoutMs} firstFrameTimeoutMs={FirstFrameTimeoutMs}");
        LogNative($"StartAsync requested: out={outputPath}, windows={hwnds.Count}, fps={fps}, drawMouse={drawMouse}, useH265={useH265}");
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
            // ignore; replacing the session
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
        var hwndList = new List<IntPtr>(hwnds);

        _worker = Task.Run(() =>
        {
            try
            {
                RunTranscode(outputPath, hwndList, fps, drawMouse, useH265, ct, firstFrameTcs);
                LogNative("Worker completed successfully.");
                return true;
            }
            catch (OperationCanceledException)
            {
                firstFrameTcs.TrySetCanceled();
                return false;
            }
            catch (Exception ex)
            {
                LastErrorMessage = ex.Message;
                Debug.WriteLine($"[LibavWgcComposite] {ex}");
                LogNative($"Worker exception: {ex}");
                firstFrameTcs.TrySetException(ex);
                return false;
            }
        }, CancellationToken.None);

        return StartupGateAsync(_worker, firstFrameTcs.Task);
    }

    private static async Task<bool> StartupGateAsync(Task<bool> worker, Task<bool> firstFrame)
    {
        var timeout = Task.Delay(BringupTimeoutMs + FirstFrameTimeoutMs + 2000);
        var done = await Task.WhenAny(firstFrame, worker, timeout).ConfigureAwait(false);

        // Observe firstFrame's fault on the fallback paths (it faults when the worker bails before a real frame)
        // so it doesn't surface as an UnobservedTaskException ([ERR] noise).
        ObserveFaultedTask(firstFrame);

        if (done == firstFrame)
        {
            return await firstFrame.ConfigureAwait(false);
        }

        if (done == worker)
        {
            return await worker.ConfigureAwait(false);
        }

        // Report a FAILED start (not started=true) so the caller falls back to gdigrab and the worker can't
        // hang frameless on stop. See docs/WGC_HANDOFF.md Fix A.
        AppLog.Information("Wgc.Composite.StartupGate timeout → started=false (fallback to gdigrab)");
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
        List<IntPtr> hwnds,
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
        AVDictionary* encOpts = null;

        AVStream* outStream;
        long frameCounter = 0;
        long packetCounter = 0;
        bool encFrameInitialized = false;

        var sources = new List<WgcWindowCaptureSource>();
        var buffers = new List<byte[]?>();
        try
        {
            // Bring up every window's capture source CONCURRENTLY under a single shared timeout. WGC/D3D bring-up
            // can wedge for many seconds on the dual-monitor repro, so (a) never let a wedged Start() block the
            // worker and (b) don't pay the timeout once per window sequentially. Any source that doesn't come up
            // in time is abandoned (detached) and skipped. See docs/WGC_HANDOFF.md Fix A.
            var sizes = new List<(int Width, int Height)>();
            var bringups = new List<(IntPtr Hwnd, WgcWindowCaptureSource Src, Task<bool> Task)>();
            foreach (var hwnd in hwnds)
            {
                var src = new WgcWindowCaptureSource(hwnd, drawMouse);
                bringups.Add((hwnd, src, Task.Run(() => src.Start())));
            }

            var bringupTasks = new Task[bringups.Count];
            for (int i = 0; i < bringups.Count; i++)
            {
                bringupTasks[i] = bringups[i].Task;
            }

            try
            {
                Task.WaitAll(bringupTasks, BringupTimeoutMs, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Individual faults are inspected per-task below.
            }

            foreach (var (hwnd, src, task) in bringups)
            {
                if (task.IsCompletedSuccessfully && task.Result)
                {
                    sources.Add(src);
                    buffers.Add(null);
                    sizes.Add((src.InitialWidth, src.InitialHeight));
                    AppLog.Information($"Wgc.Composite.Start hwnd=0x{hwnd.ToInt64():X} size={src.InitialWidth}x{src.InitialHeight} adapter='{src.AdapterDescription}'");
                    AppLog.Information($"Wgc.Composite.Start hwnd=0x{hwnd.ToInt64():X} {WgcInterop.DescribeWindowMonitor(hwnd)}");
                }
                else
                {
                    AppLog.Information($"Wgc.Composite.Start.Timeout hwnd=0x{hwnd.ToInt64():X} bringupMs={BringupTimeoutMs} → skipped/abandoned");
                    // Abandon after the (possibly wedged) Start() settles; never block this loop on teardown.
                    WgcWindowCaptureSource.DisposeDetachedAfter(task, src, $"composite hwnd=0x{hwnd.ToInt64():X} (bringup-abandon)");
                }
            }

            if (sources.Count == 0)
            {
                throw new InvalidOperationException("None of the selected windows could be captured.");
            }

            if (sources.Count != hwnds.Count)
            {
                LastWarningMessage = $"{hwnds.Count - sources.Count} window(s) could not be captured and were skipped.";
            }

            var (canvasW, canvasH) = CompositeGridLayout.CanvasSize(sizes);
            var cells = CompositeGridLayout.ComputeCells(sources.Count, canvasW, canvasH);

            pkt = ffmpeg.av_packet_alloc();
            encFrame = ffmpeg.av_frame_alloc();

            encCtx = LibavRecordingEncoder.OpenRecordingEncoderContext(
                useH265, PreferHardwareEncoder, canvasW, canvasH, fps, &encOpts,
                out string encName, out string? warningMessage);
            if (encCtx == null)
            {
                throw new InvalidOperationException("No compatible video encoder available for recording.");
            }

            SelectedEncoderName = encName;
            if (!string.IsNullOrWhiteSpace(warningMessage))
            {
                LastWarningMessage = string.IsNullOrEmpty(LastWarningMessage) ? warningMessage : $"{LastWarningMessage} {warningMessage}";
            }
            LogNative($"Composite encoder: {encName} ({canvasW}x{canvasH}@{fps}, {sources.Count} windows)");

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

            // Persistent composite canvas (BGRA) reused every frame — no per-frame Skia allocation.
            using var canvas = new SKBitmap(new SKImageInfo(canvasW, canvasH, SKColorType.Bgra8888, SKAlphaType.Premul));
            using var skCanvas = new SKCanvas(canvas);
            IntPtr canvasPixels = canvas.GetPixels();
            int canvasStride = canvas.RowBytes;

            sws = ffmpeg.sws_getContext(
                canvasW, canvasH, AVPixelFormat.AV_PIX_FMT_BGRA,
                encCtx->width, encCtx->height, encCtx->pix_fmt,
                (int)SwsFlags.SWS_FAST_BILINEAR, null, null, null);
            if (sws == null)
            {
                throw new InvalidOperationException("sws_getContext failed for composite canvas.");
            }

            // Prime: wait until at least one source has delivered a frame. Bail out fast if none ever do (the
            // dual-monitor failure mode) so the caller can fall back to gdigrab instead of hanging.
            var primeClock = Stopwatch.StartNew();
            while (!ct.IsCancellationRequested)
            {
                bool any = false;
                for (int i = 0; i < sources.Count; i++)
                {
                    var buf = buffers[i];
                    if (sources[i].TryCopyLatest(ref buf, out _, out _))
                    {
                        buffers[i] = buf;
                        any = true;
                    }
                }

                if (any)
                {
                    AppLog.Information($"Wgc.Composite.FirstFrame arrivedMs={primeClock.ElapsedMilliseconds} windows={sources.Count}");
                    break;
                }

                if (primeClock.ElapsedMilliseconds >= FirstFrameTimeoutMs)
                {
                    AppLog.Information($"Wgc.Composite.FirstFrame.Timeout waitedMs={primeClock.ElapsedMilliseconds} windows={sources.Count} → fallback");
                    TimedOutWaitingForFrame = true;
                    throw new TimeoutException($"Composite WGC produced no first frame within {FirstFrameTimeoutMs}ms.");
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

                skCanvas.Clear(SKColors.Black);
                for (int i = 0; i < sources.Count; i++)
                {
                    var buf = buffers[i];
                    if (!sources[i].TryCopyLatest(ref buf, out int w, out int h) || buf == null || w <= 0 || h <= 0)
                    {
                        continue;
                    }

                    buffers[i] = buf;
                    DrawSourceIntoCell(skCanvas, buf, w, h, cells[i]);
                }
                skCanvas.Flush();

                if (!encFrameInitialized)
                {
                    encFrame->format = (int)encCtx->pix_fmt;
                    encFrame->width = encCtx->width;
                    encFrame->height = encCtx->height;
                    ThrowIfErr(ffmpeg.av_frame_get_buffer(encFrame, 32), "frame_get_buffer(enc)");
                    encFrameInitialized = true;
                }

                ThrowIfErr(ffmpeg.av_frame_make_writable(encFrame), "frame_make_writable(enc)");

                var srcData = new byte_ptrArray4();
                srcData[0] = (byte*)canvasPixels;
                var srcStride = new int_array4();
                srcStride[0] = canvasStride;
                ffmpeg.sws_scale(sws, srcData, srcStride, 0, canvasH, encFrame->data, encFrame->linesize);

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
                throw new InvalidOperationException("Composite WGC pipeline produced zero frames.");
            }

            AppLog.Information($"Wgc.Composite.Stop windows={sources.Count} frames={frameCounter} packets={packetCounter}");
            Debug.WriteLine($"[LibavWgcComposite] segment done: frames={frameCounter}, packets={packetCounter}, output='{outputPath}'");
        }
        finally
        {
            // Detach each source's disposal: a wedged WGC teardown (dual-monitor "no frames" repro) must not keep
            // this worker alive, or stop / pin / the gdigrab fallback would block on it. See docs/WGC_HANDOFF.md.
            for (int i = 0; i < sources.Count; i++)
            {
                WgcWindowCaptureSource.DisposeDetached(sources[i], $"composite#{i}");
            }

            SafeFreePkt(&pkt);
            SafeFreeFrame(&encFrame);

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

    private static void DrawSourceIntoCell(SKCanvas canvas, byte[] bgra, int width, int height, SKRect cell)
    {
        var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bmp = new SKBitmap();
            if (!bmp.InstallPixels(info, handle.AddrOfPinnedObject(), width * 4))
            {
                return;
            }

            canvas.DrawBitmap(bmp, CompositeGridLayout.FitInto(width, height, cell));
        }
        finally
        {
            handle.Free();
        }
    }

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
