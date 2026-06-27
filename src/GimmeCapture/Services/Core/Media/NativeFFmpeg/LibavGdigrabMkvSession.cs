using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FFmpeg.AutoGen;
using SkiaSharp;

namespace GimmeCapture.Services.Core.Media.NativeFFmpeg;

/// <summary>
/// Captures screen via libavdevice gdigrab and writes H.264/x265-in-Matroska segment.
/// </summary>
internal sealed class LibavGdigrabMkvSession : IDisposable
{
    private CancellationTokenSource? _cts;
    private Task<bool>? _worker;

    public Task<bool>? Worker => _worker;
    public string? LastErrorMessage { get; private set; }
    public string? LastWarningMessage { get; private set; }
    public string? SelectedEncoderName { get; private set; }

    /// <summary>Composite a webcam picture-in-picture into each frame.</summary>
    public bool EnableWebcam { get; set; }

    /// <summary>dshow device name of the webcam (as listed by ffmpeg -list_devices).</summary>
    public string WebcamDeviceName { get; set; } = string.Empty;

    /// <summary>PiP corner: 0 = top-left, 1 = top-right, 2 = bottom-left, 3 = bottom-right.</summary>
    public int WebcamCorner { get; set; } = 3;

    /// <summary>Burn a cursor spotlight ring into each frame.</summary>
    public bool HighlightCursor { get; set; }

    /// <summary>Burn a click ripple into each frame.</summary>
    public bool HighlightClicks { get; set; }

    /// <summary>Burn recently pressed keys (as chord pills) into each frame.</summary>
    public bool ShowKeystrokes { get; set; }

    /// <summary>
    /// When true (default) GPU / Media-Foundation encoders (NVENC/QSV/AMF/MF) are tried before the
    /// software libx264/265 fallback. Set false to force software encoding.
    /// </summary>
    public bool PreferHardwareEncoder { get; set; } = true;

    /// <summary>
    /// Experimental: run capture/composite and encoding on separate threads (producer/consumer) so they
    /// overlap, reducing dropped frames at high resolution/fps with overlays. Default false keeps the
    /// proven single-threaded loop. Drops frames under sustained back-pressure rather than stalling capture.
    /// </summary>
    public bool PipelinedEncoding { get; set; }

    /// <summary>
    /// Timestamp output frames by wall-clock arrival time instead of a frame counter. Needed when gdigrab can't
    /// keep up with the target fps (e.g. several large window regions captured concurrently): a frame counter at
    /// 1/fps spacing then yields a sped-up video (5 s captured → ~1 s playback). Wall-clock PTS keeps the output
    /// duration equal to real time (lower effective fps, but correct speed). Off by default so the proven
    /// single-region path is unchanged. Forces the single-threaded encode loop.
    /// </summary>
    public bool UseWallClockPts { get; set; }

    public Task<bool> StartAsync(string outputPath, int offsetX, int offsetY, int width, int height, int fps, bool drawMouse, bool useH265)
    {
        FFmpegRuntime.EnsureInitialized();
        LogNative($"StartAsync requested: out={outputPath}, x={offsetX}, y={offsetY}, w={width}, h={height}, fps={fps}, drawMouse={drawMouse}, useH265={useH265}");
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
                RunTranscode(outputPath, offsetX, offsetY, width, height, fps, drawMouse, useH265, ct, firstFrameTcs);
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
                Debug.WriteLine($"[LibavGdigrab] {ex}");
                LogNative($"Worker exception: {ex}");
                firstFrameTcs.TrySetException(ex);
                return false;
            }
        }, CancellationToken.None);

        return StartupGateAsync(_worker, firstFrameTcs.Task);
    }

    private static async Task<bool> StartupGateAsync(Task<bool> worker, Task<bool> firstFrame)
    {
        // Consider recording started only after first encoded frame is observed.
        // This avoids false "started" states that later produce a 1-frame output.
        var timeout = Task.Delay(4000);
        var done = await Task.WhenAny(firstFrame, worker, timeout).ConfigureAwait(false);
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

        // Live capture may need longer than startup window before first frame is encoded.
        // Keep session running and report started; runtime errors are still surfaced by worker completion.
        LogNative("StartupGate: timeout, return started=true.");
        return true;
    }

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
        int offsetX,
        int offsetY,
        int width,
        int height,
        int fps,
        bool drawMouse,
        bool useH265,
        CancellationToken ct,
        TaskCompletionSource<bool> firstFrameTcs)
    {
        AVFormatContext* inputFmt = null;
        AVFormatContext* outputFmt = null;
        AVCodecContext* decCtx = null;
        AVCodecContext* encCtx = null;
        SwsContext* sws = null;
        SwsContext* swsToBgra = null;
        AVPacket* pkt = null;
        AVFrame* decFrame = null;
        AVFrame* encFrame = null;
        AVFrame* bgraFrame = null;
        AVDictionary* demuxOpts = null;
        AVDictionary* encOpts = null;

        int videoIn = -1;
        AVStream* outStream = null;
        long frameCounter = 0;
        long packetCounter = 0;
        bool encFrameInitialized = false;

        // Optional webcam picture-in-picture, composited per frame via a BGRA round-trip in the encode loop.
        WebcamPipCompositor? webcam = null;
        if (EnableWebcam && !string.IsNullOrWhiteSpace(WebcamDeviceName))
        {
            try
            {
                webcam = new WebcamPipCompositor(WebcamDeviceName, WebcamCorner);
                webcam.Start();
            }
            catch (Exception ex)
            {
                LogNative($"Webcam PiP unavailable: {ex.Message}");
                webcam = null;
            }
        }

        // Optional per-frame cursor / click overlay, burned in via the same BGRA round-trip.
        CursorOverlayRenderer? overlay = (HighlightCursor || HighlightClicks)
            ? new CursorOverlayRenderer(offsetX, offsetY, HighlightCursor, HighlightClicks)
            : null;

        // Optional keystroke overlay (recent keys as fading pills along the bottom).
        KeystrokeOverlayRenderer? keystrokes = ShowKeystrokes ? new KeystrokeOverlayRenderer() : null;

        // One per-frame draw combining all overlays: cursor/click first, webcam PiP, then keystrokes on top.
        Action<SKBitmap>? composite = (overlay != null || webcam != null || keystrokes != null)
            ? sk => { overlay?.Draw(sk); webcam?.Draw(sk); keystrokes?.Draw(sk); }
            : null;

        pkt = ffmpeg.av_packet_alloc();
        decFrame = ffmpeg.av_frame_alloc();
        encFrame = ffmpeg.av_frame_alloc();

        try
        {
            var ifmt = ffmpeg.av_find_input_format("gdigrab");
            if (ifmt == null)
            {
                throw new InvalidOperationException("gdigrab demuxer missing (need avdevice DLL).");
            }

            ffmpeg.av_dict_set(&demuxOpts, "framerate", fps.ToString(), 0);
            ffmpeg.av_dict_set(&demuxOpts, "offset_x", offsetX.ToString(), 0);
            ffmpeg.av_dict_set(&demuxOpts, "offset_y", offsetY.ToString(), 0);
            ffmpeg.av_dict_set(&demuxOpts, "video_size", $"{width}x{height}", 0);
            ffmpeg.av_dict_set(&demuxOpts, "draw_mouse", drawMouse ? "1" : "0", 0);
            ffmpeg.av_dict_set(&demuxOpts, "probesize", "32768", 0);
            ffmpeg.av_dict_set(&demuxOpts, "analyzeduration", "0", 0);

            ThrowIfErr(
                ffmpeg.avformat_open_input(&inputFmt, "desktop", ifmt, &demuxOpts),
                "avformat_open_input");

            // Skip avformat_find_stream_info for gdigrab live input.
            // On some Windows systems it repeatedly logs probe warnings and delays startup.

            videoIn = ffmpeg.av_find_best_stream(inputFmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            if (videoIn < 0)
            {
                throw new InvalidOperationException("No gdigrab video stream.");
            }

            AVCodecParameters* inPar = inputFmt->streams[videoIn]->codecpar;
            AVCodec* dec = ffmpeg.avcodec_find_decoder(inPar->codec_id);
            if (dec == null)
            {
                throw new InvalidOperationException($"No decoder for codec_id={inPar->codec_id}");
            }

            decCtx = ffmpeg.avcodec_alloc_context3(dec);
            ThrowIfErr(ffmpeg.avcodec_parameters_to_context(decCtx, inPar), "parameters_to_context(dec)");
            ThrowIfErr(ffmpeg.avcodec_open2(decCtx, dec, null), "avcodec_open2(dec)");

            encCtx = LibavRecordingEncoder.OpenRecordingEncoderContext(
                useH265,
                PreferHardwareEncoder,
                width,
                height,
                fps,
                &encOpts,
                out string encName,
                out string? warningMessage);
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
            LogNative($"Selected encoder: {encName}");

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

            if (PipelinedEncoding && !UseWallClockPts)
            {
                RunPipelinedEncode(inputFmt, videoIn, pkt, decCtx, encCtx, ref sws, ref swsToBgra, decFrame, ref bgraFrame, composite, outputFmt, outStream, ref frameCounter, ref packetCounter, firstFrameTcs, ct);
            }
            else
            {
                // Wall-clock PTS clock (only consulted when UseWallClockPts). Started lazily on the first encoded
                // frame so that frame lands at pts 0 regardless of capture-startup latency.
                var encodeClock = new System.Diagnostics.Stopwatch();
                long lastEncPts = -1;

                while (!ct.IsCancellationRequested)
                {
                    int rr = ffmpeg.av_read_frame(inputFmt, pkt);
                    if (rr == ffmpeg.AVERROR_EOF)
                    {
                        break;
                    }
                    if (rr == ffmpeg.AVERROR(11))
                    {
                        // Live input can temporarily have no packet ready yet.
                        Thread.Sleep(1);
                        continue;
                    }

                    ThrowIfErr(rr, "av_read_frame");

                    if (pkt->stream_index != videoIn)
                    {
                        ffmpeg.av_packet_unref(pkt);
                        continue;
                    }

                    ThrowIfErr(ffmpeg.avcodec_send_packet(decCtx, pkt), "send_packet(dec)");
                    ffmpeg.av_packet_unref(pkt);

                    DecodeEncodeLoop(decCtx, encCtx, ref sws, ref swsToBgra, decFrame, encFrame, ref bgraFrame, ref encFrameInitialized, composite, outputFmt, outStream, pkt, ref frameCounter, ref packetCounter, firstFrameTcs, UseWallClockPts, encodeClock, ref lastEncPts);
                }

                ThrowIfErr(ffmpeg.avcodec_send_packet(decCtx, null), "flush_decoder");
                DecodeEncodeLoop(decCtx, encCtx, ref sws, ref swsToBgra, decFrame, encFrame, ref bgraFrame, ref encFrameInitialized, composite, outputFmt, outStream, pkt, ref frameCounter, ref packetCounter, firstFrameTcs, UseWallClockPts, encodeClock, ref lastEncPts);

                ThrowIfErr(ffmpeg.avcodec_send_frame(encCtx, null), "flush_encoder");
                LibavRecordingEncoder.WriteEncodedPackets(encCtx, outputFmt, outStream, pkt, ref packetCounter);
            }

            ffmpeg.av_write_trailer(outputFmt);

            if (frameCounter == 0)
            {
                throw new InvalidOperationException("Recording pipeline produced zero frames.");
            }

            long outputBytes = 0;
            try
            {
                if (System.IO.File.Exists(outputPath))
                {
                    outputBytes = new System.IO.FileInfo(outputPath).Length;
                }
            }
            catch
            {
                // ignore telemetry failure
            }

            Debug.WriteLine($"[LibavGdigrab] segment done: frames={frameCounter}, packets={packetCounter}, output='{outputPath}', bytes={outputBytes}");
        }
        finally
        {
            webcam?.Dispose();

            SafeFreePkt(&pkt);
            SafeFreeFrame(&decFrame);
            SafeFreeFrame(&encFrame);
            SafeFreeFrame(&bgraFrame);

            if (sws != null)
            {
                ffmpeg.sws_freeContext(sws);
            }

            if (swsToBgra != null)
            {
                ffmpeg.sws_freeContext(swsToBgra);
            }

            if (decCtx != null)
            {
                ffmpeg.avcodec_free_context(&decCtx);
            }

            if (encCtx != null)
            {
                ffmpeg.avcodec_free_context(&encCtx);
            }

            if (inputFmt != null)
            {
                ffmpeg.avformat_close_input(&inputFmt);
            }

            if (outputFmt != null)
            {
                if ((outputFmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0 && outputFmt->pb != null)
                {
                    ffmpeg.avio_closep(&outputFmt->pb);
                }

                ffmpeg.avformat_free_context(outputFmt);
            }

            if (demuxOpts != null)
            {
                ffmpeg.av_dict_free(&demuxOpts);
            }

            if (encOpts != null)
            {
                ffmpeg.av_dict_free(&encOpts);
            }
        }
    }

    private static unsafe void DecodeEncodeLoop(
        AVCodecContext* decCtx,
        AVCodecContext* encCtx,
        ref SwsContext* sws,
        ref SwsContext* swsToBgra,
        AVFrame* decFrame,
        AVFrame* encFrame,
        ref AVFrame* bgraFrame,
        ref bool encFrameInitialized,
        Action<SKBitmap>? composite,
        AVFormatContext* outputFmt,
        AVStream* outStream,
        AVPacket* pkt,
        ref long frameCounter,
        ref long packetCounter,
        TaskCompletionSource<bool> firstFrameTcs,
        bool useWallClockPts,
        System.Diagnostics.Stopwatch encodeClock,
        ref long lastEncPts)
    {
        while (true)
        {
            ffmpeg.av_frame_unref(decFrame);
            int gr = ffmpeg.avcodec_receive_frame(decCtx, decFrame);
            if (gr == ffmpeg.AVERROR_EOF || gr == ffmpeg.AVERROR(11))
            {
                break;
            }
            ThrowIfErr(gr, "receive_frame(dec)");

            var srcFmt = (AVPixelFormat)decFrame->format;
            int srcW = decFrame->width > 0 ? decFrame->width : decCtx->width;
            int srcH = decFrame->height > 0 ? decFrame->height : decCtx->height;

            if (sws == null)
            {
                if (composite != null)
                {
                    // Composite path: decode → BGRA (overlay + webcam PiP drawn here) → encoder pixel format.
                    swsToBgra = ffmpeg.sws_getContext(
                        srcW, srcH, srcFmt,
                        srcW, srcH, AVPixelFormat.AV_PIX_FMT_BGRA,
                        (int)SwsFlags.SWS_FAST_BILINEAR, null, null, null);
                    sws = ffmpeg.sws_getContext(
                        srcW, srcH, AVPixelFormat.AV_PIX_FMT_BGRA,
                        encCtx->width, encCtx->height, encCtx->pix_fmt,
                        (int)SwsFlags.SWS_FAST_BILINEAR, null, null, null);
                    if (swsToBgra == null || sws == null)
                    {
                        throw new InvalidOperationException("sws_getContext failed for BGRA composite path.");
                    }

                    bgraFrame = ffmpeg.av_frame_alloc();
                    bgraFrame->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
                    bgraFrame->width = srcW;
                    bgraFrame->height = srcH;
                    ThrowIfErr(ffmpeg.av_frame_get_buffer(bgraFrame, 32), "frame_get_buffer(bgra)");
                }
                else
                {
                    sws = ffmpeg.sws_getContext(
                        srcW, srcH, srcFmt,
                        encCtx->width, encCtx->height, encCtx->pix_fmt,
                        (int)SwsFlags.SWS_FAST_BILINEAR, null, null, null);
                    if (sws == null)
                    {
                        throw new InvalidOperationException($"sws_getContext failed for src={srcFmt} {srcW}x{srcH} -> dst={encCtx->pix_fmt} {encCtx->width}x{encCtx->height}");
                    }
                }
            }

            if (!encFrameInitialized)
            {
                encFrame->format = (int)encCtx->pix_fmt;
                encFrame->width = encCtx->width;
                encFrame->height = encCtx->height;
                ThrowIfErr(ffmpeg.av_frame_get_buffer(encFrame, 32), "frame_get_buffer(enc)");
                encFrameInitialized = true;
            }

            ThrowIfErr(ffmpeg.av_frame_make_writable(encFrame), "frame_make_writable(enc)");

            if (composite != null && bgraFrame != null && swsToBgra != null)
            {
                // decode → BGRA
                ffmpeg.sws_scale(swsToBgra, decFrame->data, decFrame->linesize, 0, decFrame->height, bgraFrame->data, bgraFrame->linesize);

                // Draw the cursor ring / click ripple and/or webcam PiP onto the BGRA pixels in place.
                var info = new SKImageInfo(bgraFrame->width, bgraFrame->height, SKColorType.Bgra8888, SKAlphaType.Premul);
                using (var sk = new SKBitmap())
                {
                    if (sk.InstallPixels(info, (IntPtr)bgraFrame->data[0], bgraFrame->linesize[0]))
                    {
                        composite(sk);
                    }
                }

                // BGRA → encoder pixel format
                ffmpeg.sws_scale(sws, bgraFrame->data, bgraFrame->linesize, 0, bgraFrame->height, encFrame->data, encFrame->linesize);
            }
            else
            {
                ffmpeg.sws_scale(sws, decFrame->data, decFrame->linesize, 0, decFrame->height, encFrame->data, encFrame->linesize);
            }

            if (useWallClockPts)
            {
                // Pace output PTS by real elapsed time so a slow/behind gdigrab capture stays the correct speed.
                // time_base is 1/fps, so pts (in time_base units) == elapsed_seconds * fps. Started lazily here so
                // the first frame is pts 0. Bumped to stay strictly monotonic if two frames land in the same tick.
                if (!encodeClock.IsRunning)
                {
                    encodeClock.Start();
                }

                double fps = encCtx->time_base.num != 0
                    ? encCtx->time_base.den / (double)encCtx->time_base.num
                    : 30.0;
                long pts = (long)(encodeClock.Elapsed.TotalSeconds * fps);
                if (pts <= lastEncPts)
                {
                    pts = lastEncPts + 1;
                }

                lastEncPts = pts;
                encFrame->pts = pts;
            }
            else
            {
                encFrame->pts = frameCounter;
            }

            frameCounter++;
            if (frameCounter == 1)
            {
                firstFrameTcs.TrySetResult(true);
            }

            ThrowIfErr(ffmpeg.avcodec_send_frame(encCtx, encFrame), "send_frame(enc)");
            LibavRecordingEncoder.WriteEncodedPackets(encCtx, outputFmt, outStream, pkt, ref packetCounter);
        }
    }

    // Producer/consumer variant of the encode loop. This (the worker) thread reads + decodes + composites
    // each frame into a fresh encoder-format frame and hands it to a bounded queue; a dedicated encoder
    // thread drains the queue and muxes. Capture/composite and the (CPU-heavy) software encode overlap.
    // The encoder + muxer are touched only by the consumer thread; under sustained back-pressure full-queue
    // frames are dropped rather than stalling capture. Each queued frame is owned by exactly one side
    // (consumer after encode, producer on drop), so there is no double-free.
    private unsafe void RunPipelinedEncode(
        AVFormatContext* inputFmt,
        int videoIn,
        AVPacket* readPkt,
        AVCodecContext* decCtx,
        AVCodecContext* encCtx,
        ref SwsContext* sws,
        ref SwsContext* swsToBgra,
        AVFrame* decFrame,
        ref AVFrame* bgraFrame,
        Action<SKBitmap>? composite,
        AVFormatContext* outputFmt,
        AVStream* outStream,
        ref long frameCounter,
        ref long packetCounter,
        TaskCompletionSource<bool> firstFrameTcs,
        CancellationToken ct)
    {
        using var queue = new BlockingCollection<IntPtr>(boundedCapacity: 8);
        Exception? consumerEx = null;
        long localPacketCounter = 0;

        // Pointers can't be captured by a lambda — pass them through as IntPtr and cast back inside.
        IntPtr encCtxPtr = (IntPtr)encCtx;
        IntPtr outFmtPtr = (IntPtr)outputFmt;
        IntPtr outStreamPtr = (IntPtr)outStream;

        var consumer = new Thread(() =>
        {
            AVCodecContext* enc = (AVCodecContext*)encCtxPtr;
            AVFormatContext* ofmt = (AVFormatContext*)outFmtPtr;
            AVStream* ost = (AVStream*)outStreamPtr;
            AVPacket* outPkt = ffmpeg.av_packet_alloc();
            try
            {
                foreach (IntPtr p in queue.GetConsumingEnumerable())
                {
                    AVFrame* f = (AVFrame*)p;
                    int sr = ffmpeg.avcodec_send_frame(enc, f);
                    var ff = f;
                    ffmpeg.av_frame_free(&ff);
                    ThrowIfErr(sr, "send_frame(enc,pipe)");
                    LibavRecordingEncoder.WriteEncodedPackets(enc, ofmt, ost, outPkt, ref localPacketCounter);
                }

                // Flush the encoder on the thread that owns it.
                ThrowIfErr(ffmpeg.avcodec_send_frame(enc, null), "flush_encoder(pipe)");
                LibavRecordingEncoder.WriteEncodedPackets(enc, ofmt, ost, outPkt, ref localPacketCounter);
            }
            catch (Exception ex)
            {
                consumerEx = ex;
            }
            finally
            {
                if (outPkt != null) { var pp = outPkt; ffmpeg.av_packet_free(&pp); }
            }
        })
        { IsBackground = true, Name = "RecordEncode" };
        consumer.Start();

        try
        {
            while (!ct.IsCancellationRequested && consumerEx == null)
            {
                int rr = ffmpeg.av_read_frame(inputFmt, readPkt);
                if (rr == ffmpeg.AVERROR_EOF)
                {
                    break;
                }
                if (rr == ffmpeg.AVERROR(11))
                {
                    Thread.Sleep(1);
                    continue;
                }
                ThrowIfErr(rr, "av_read_frame(pipe)");

                if (readPkt->stream_index != videoIn)
                {
                    ffmpeg.av_packet_unref(readPkt);
                    continue;
                }

                ThrowIfErr(ffmpeg.avcodec_send_packet(decCtx, readPkt), "send_packet(dec,pipe)");
                ffmpeg.av_packet_unref(readPkt);
                DecodeComposeEnqueue(decCtx, encCtx, ref sws, ref swsToBgra, decFrame, ref bgraFrame, composite, queue, ref frameCounter, firstFrameTcs);
            }

            ffmpeg.avcodec_send_packet(decCtx, null);
            DecodeComposeEnqueue(decCtx, encCtx, ref sws, ref swsToBgra, decFrame, ref bgraFrame, composite, queue, ref frameCounter, firstFrameTcs);
        }
        finally
        {
            queue.CompleteAdding();
            consumer.Join();

            // Free any frames the consumer didn't drain (e.g. it faulted early).
            while (queue.TryTake(out IntPtr leftover))
            {
                var lf = (AVFrame*)leftover;
                ffmpeg.av_frame_free(&lf);
            }
        }

        packetCounter += localPacketCounter;
        if (consumerEx != null)
        {
            throw consumerEx;
        }
    }

    // Producer half of the pipeline: drain the decoder, composite, scale into a fresh encoder-format frame,
    // and enqueue it (dropping it if the queue is full). Mirrors DecodeEncodeLoop's lazy sws/composite setup.
    private static unsafe void DecodeComposeEnqueue(
        AVCodecContext* decCtx,
        AVCodecContext* encCtx,
        ref SwsContext* sws,
        ref SwsContext* swsToBgra,
        AVFrame* decFrame,
        ref AVFrame* bgraFrame,
        Action<SKBitmap>? composite,
        BlockingCollection<IntPtr> queue,
        ref long frameCounter,
        TaskCompletionSource<bool> firstFrameTcs)
    {
        while (true)
        {
            ffmpeg.av_frame_unref(decFrame);
            int gr = ffmpeg.avcodec_receive_frame(decCtx, decFrame);
            if (gr == ffmpeg.AVERROR_EOF || gr == ffmpeg.AVERROR(11))
            {
                break;
            }
            ThrowIfErr(gr, "receive_frame(dec,pipe)");

            var srcFmt = (AVPixelFormat)decFrame->format;
            int srcW = decFrame->width > 0 ? decFrame->width : decCtx->width;
            int srcH = decFrame->height > 0 ? decFrame->height : decCtx->height;

            if (sws == null)
            {
                if (composite != null)
                {
                    swsToBgra = ffmpeg.sws_getContext(
                        srcW, srcH, srcFmt,
                        srcW, srcH, AVPixelFormat.AV_PIX_FMT_BGRA,
                        (int)SwsFlags.SWS_FAST_BILINEAR, null, null, null);
                    sws = ffmpeg.sws_getContext(
                        srcW, srcH, AVPixelFormat.AV_PIX_FMT_BGRA,
                        encCtx->width, encCtx->height, encCtx->pix_fmt,
                        (int)SwsFlags.SWS_FAST_BILINEAR, null, null, null);
                    if (swsToBgra == null || sws == null)
                    {
                        throw new InvalidOperationException("sws_getContext failed for BGRA composite path (pipe).");
                    }

                    bgraFrame = ffmpeg.av_frame_alloc();
                    bgraFrame->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
                    bgraFrame->width = srcW;
                    bgraFrame->height = srcH;
                    ThrowIfErr(ffmpeg.av_frame_get_buffer(bgraFrame, 32), "frame_get_buffer(bgra,pipe)");
                }
                else
                {
                    sws = ffmpeg.sws_getContext(
                        srcW, srcH, srcFmt,
                        encCtx->width, encCtx->height, encCtx->pix_fmt,
                        (int)SwsFlags.SWS_FAST_BILINEAR, null, null, null);
                    if (sws == null)
                    {
                        throw new InvalidOperationException("sws_getContext failed (pipe).");
                    }
                }
            }

            AVFrame* encFrame = ffmpeg.av_frame_alloc();
            encFrame->format = (int)encCtx->pix_fmt;
            encFrame->width = encCtx->width;
            encFrame->height = encCtx->height;
            ThrowIfErr(ffmpeg.av_frame_get_buffer(encFrame, 32), "frame_get_buffer(enc,pipe)");

            if (composite != null && bgraFrame != null && swsToBgra != null)
            {
                ffmpeg.sws_scale(swsToBgra, decFrame->data, decFrame->linesize, 0, decFrame->height, bgraFrame->data, bgraFrame->linesize);
                var info = new SKImageInfo(bgraFrame->width, bgraFrame->height, SKColorType.Bgra8888, SKAlphaType.Premul);
                using (var sk = new SKBitmap())
                {
                    if (sk.InstallPixels(info, (IntPtr)bgraFrame->data[0], bgraFrame->linesize[0]))
                    {
                        composite(sk);
                    }
                }
                ffmpeg.sws_scale(sws, bgraFrame->data, bgraFrame->linesize, 0, bgraFrame->height, encFrame->data, encFrame->linesize);
            }
            else
            {
                ffmpeg.sws_scale(sws, decFrame->data, decFrame->linesize, 0, decFrame->height, encFrame->data, encFrame->linesize);
            }

            encFrame->pts = frameCounter++;
            if (frameCounter == 1)
            {
                firstFrameTcs.TrySetResult(true);
            }

            if (!queue.TryAdd((IntPtr)encFrame))
            {
                // Queue full — drop this frame rather than stall capture.
                var f = encFrame;
                ffmpeg.av_frame_free(&f);
            }
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
