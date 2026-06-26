using System;
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

    /// <summary>
    /// When true (default) GPU / Media-Foundation encoders (NVENC/QSV/AMF/MF) are tried before the
    /// software libx264/265 fallback. Set false to force software encoding.
    /// </summary>
    public bool PreferHardwareEncoder { get; set; } = true;

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

        // One per-frame draw combining both: cursor/click overlay first, then the webcam PiP on top.
        Action<SKBitmap>? composite = (overlay != null || webcam != null)
            ? sk => { overlay?.Draw(sk); webcam?.Draw(sk); }
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

                DecodeEncodeLoop(decCtx, encCtx, ref sws, ref swsToBgra, decFrame, encFrame, ref bgraFrame, ref encFrameInitialized, composite, outputFmt, outStream, pkt, ref frameCounter, ref packetCounter, firstFrameTcs);
            }

            ThrowIfErr(ffmpeg.avcodec_send_packet(decCtx, null), "flush_decoder");
            DecodeEncodeLoop(decCtx, encCtx, ref sws, ref swsToBgra, decFrame, encFrame, ref bgraFrame, ref encFrameInitialized, composite, outputFmt, outStream, pkt, ref frameCounter, ref packetCounter, firstFrameTcs);

            ThrowIfErr(ffmpeg.avcodec_send_frame(encCtx, null), "flush_encoder");
            LibavRecordingEncoder.WriteEncodedPackets(encCtx, outputFmt, outStream, pkt, ref packetCounter);

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

            encFrame->pts = frameCounter++;
            if (frameCounter == 1)
            {
                firstFrameTcs.TrySetResult(true);
            }

            ThrowIfErr(ffmpeg.avcodec_send_frame(encCtx, encFrame), "send_frame(enc)");
            LibavRecordingEncoder.WriteEncodedPackets(encCtx, outputFmt, outStream, pkt, ref packetCounter);
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
