using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using FFmpeg.AutoGen;
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
    private CancellationTokenSource? _cts;
    private Task<bool>? _worker;

    public Task<bool>? Worker => _worker;
    public string? LastErrorMessage { get; private set; }
    public string? LastWarningMessage { get; private set; }
    public string? SelectedEncoderName { get; private set; }
    public bool PreferHardwareEncoder { get; set; } = true;

    public Task<bool> StartAsync(string outputPath, IReadOnlyList<IntPtr> hwnds, int fps, bool drawMouse, bool useH265)
    {
        FFmpegRuntime.EnsureInitialized();
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
        var timeout = Task.Delay(5000);
        var done = await Task.WhenAny(firstFrame, worker, timeout).ConfigureAwait(false);
        if (done == firstFrame)
        {
            return await firstFrame.ConfigureAwait(false);
        }

        if (done == worker)
        {
            return await worker.ConfigureAwait(false);
        }

        return true;
    }

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
            // Start every window's capture source; drop (with a warning) any that can't start.
            var sizes = new List<(int Width, int Height)>();
            foreach (var hwnd in hwnds)
            {
                var src = new WgcWindowCaptureSource(hwnd, drawMouse);
                bool ok;
                try
                {
                    ok = src.Start();
                }
                catch (Exception ex)
                {
                    LogNative($"Window source failed to start ({hwnd}): {ex.Message}");
                    ok = false;
                }

                if (ok)
                {
                    sources.Add(src);
                    buffers.Add(null);
                    sizes.Add((src.InitialWidth, src.InitialHeight));
                }
                else
                {
                    src.Dispose();
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

            // Prime: wait until at least one source has delivered a frame.
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
                    break;
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

            Debug.WriteLine($"[LibavWgcComposite] segment done: frames={frameCounter}, packets={packetCounter}, output='{outputPath}'");
        }
        finally
        {
            foreach (var src in sources)
            {
                src.Dispose();
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
            _worker?.Wait(TimeSpan.FromSeconds(8));
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
