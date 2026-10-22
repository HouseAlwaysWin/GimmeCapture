using System;
using GimmeCapture.Models;
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
internal sealed class LibavGdigrabMkvSession : LibavRecordingSession
{
    /// <summary>Composite a webcam picture-in-picture into each frame.</summary>
    public bool EnableWebcam { get; set; }

    /// <summary>dshow device name of the webcam (as listed by ffmpeg -list_devices).</summary>
    public string WebcamDeviceName { get; set; } = string.Empty;

    /// <summary>PiP corner: 0 = top-left, 1 = top-right, 2 = bottom-left, 3 = bottom-right.</summary>
    public int WebcamCorner { get; set; } = 3;

    /// <summary>PiP width as a fraction of the frame width (e.g. 0.18 / 0.25 / 0.33).</summary>
    public float WebcamWidthFraction { get; set; } = 0.25f;

    /// <summary>Clip the webcam PiP to a circle with a ring border instead of a rectangle.</summary>
    public bool WebcamCircular { get; set; }

    /// <summary>Burn a cursor spotlight ring into each frame.</summary>
    public bool HighlightCursor { get; set; }

    /// <summary>Burn a click ripple into each frame.</summary>
    public bool HighlightClicks { get; set; }

    /// <summary>Burn recently pressed keys (as chord pills) into each frame.</summary>
    public bool ShowKeystrokes { get; set; }

    /// <summary>
    /// Experimental: run capture/composite and encoding on separate threads (producer/consumer) so they
    /// overlap, reducing dropped frames at high resolution/fps with overlays. Default false keeps the
    /// proven single-threaded loop. Drops frames under sustained back-pressure rather than stalling capture.
    /// </summary>
    public bool PipelinedEncoding { get; set; }

    /// <summary>
    /// Timestamp output frames by wall-clock arrival time instead of a frame counter. Whenever gdigrab can't keep
    /// up with the target fps, a frame counter at 1/fps spacing yields a video SHORTER than real time (5 s
    /// captured → ~1 s playback). The recorded audio is real-time either way, so that same gap is exactly what
    /// desynchronises audio from video, growing as the recording runs. Wall-clock PTS keeps the output duration
    /// equal to real time (lower effective fps, but correct speed and in sync).
    /// </summary>
    /// <remarks>
    /// Works with <see cref="PipelinedEncoding"/>: PTS is stamped on the producer thread at frame arrival and
    /// travels with the frame through the queue, so pipelining does not affect the timeline.
    /// </remarks>
    public bool UseWallClockPts { get; set; }

    protected override TimeSpan StartupGateTimeout => TimeSpan.FromSeconds(4);

    protected override TimeSpan DisposeWait => TimeSpan.FromSeconds(8);

    protected override bool OnStartupGateTimedOut()
    {
        // Live capture may need longer than startup window before first frame is encoded.
        // Keep session running and report started; runtime errors are still surfaced by worker completion.
        LogNative("StartupGate: timeout, return started=true.");
        return true;
    }

    public Task<bool> StartAsync(string outputPath, int offsetX, int offsetY, int width, int height, int fps, bool drawMouse, VideoCodec codec)
    {
        FFmpegRuntime.EnsureInitialized();
        LogNative($"StartAsync requested: out={outputPath}, x={offsetX}, y={offsetY}, w={width}, h={height}, fps={fps}, drawMouse={drawMouse}, codec={codec}");
        return StartWorker((ct, firstFrameTcs) =>
            RunTranscode(outputPath, offsetX, offsetY, width, height, fps, drawMouse, codec, ct, firstFrameTcs));
    }

    private unsafe void RunTranscode(
        string outputPath,
        int offsetX,
        int offsetY,
        int width,
        int height,
        int fps,
        bool drawMouse,
        VideoCodec codec,
        CancellationToken ct,
        TaskCompletionSource<bool> firstFrameTcs)
    {
        AVFormatContext* inputFmt = null;
        AVCodecContext* decCtx = null;
        AVPacket* pkt = null;
        AVFrame* decFrame = null;
        AVDictionary* demuxOpts = null;
        RecordingSegmentWriter? writer = null;
        CapturedFrameConverter? converter = null;

        int videoIn = -1;

        // Optional webcam picture-in-picture, composited per frame via a BGRA round-trip in the encode loop.
        WebcamPipCompositor? webcam = null;
        if (EnableWebcam && !string.IsNullOrWhiteSpace(WebcamDeviceName))
        {
            try
            {
                webcam = new WebcamPipCompositor(WebcamDeviceName, WebcamCorner, WebcamWidthFraction, WebcamCircular);
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

        try
        {
            // Windows captures the screen via gdigrab; Linux/X11 via x11grab (same libavdevice API).
            bool isWindows = OperatingSystem.IsWindows();
            string grabber = isWindows ? "gdigrab" : "x11grab";
            var ifmt = ffmpeg.av_find_input_format(grabber);
            if (ifmt == null)
            {
                throw new InvalidOperationException($"{grabber} demuxer missing (need the avdevice native lib).");
            }

            ffmpeg.av_dict_set(&demuxOpts, "framerate", fps.ToString(), 0);
            ffmpeg.av_dict_set(&demuxOpts, "video_size", $"{width}x{height}", 0);
            ffmpeg.av_dict_set(&demuxOpts, "draw_mouse", drawMouse ? "1" : "0", 0);
            ffmpeg.av_dict_set(&demuxOpts, "probesize", "32768", 0);
            ffmpeg.av_dict_set(&demuxOpts, "analyzeduration", "0", 0);

            string inputUrl;
            if (isWindows)
            {
                // gdigrab: the capture-region offset is passed as options; the URL is "desktop".
                ffmpeg.av_dict_set(&demuxOpts, "offset_x", offsetX.ToString(), 0);
                ffmpeg.av_dict_set(&demuxOpts, "offset_y", offsetY.ToString(), 0);
                inputUrl = "desktop";
            }
            else
            {
                // x11grab: the offset lives in the URL as :<display>+x,y (offset_x/y options are ignored).
                string display = Environment.GetEnvironmentVariable("DISPLAY") ?? ":0";
                inputUrl = $"{display}+{offsetX},{offsetY}";
            }

            ThrowIfErr(
                ffmpeg.avformat_open_input(&inputFmt, inputUrl, ifmt, &demuxOpts),
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

            writer = RecordingSegmentWriter.Open(
                outputPath, codec, PreferHardwareEncoder, width, height, fps, VideoCrf, VideoBitrate);

            SelectedEncoderName = writer.EncoderName;
            LastWarningMessage = writer.EncoderWarning;
            if (!string.IsNullOrWhiteSpace(writer.EncoderWarning))
            {
                LogNative(writer.EncoderWarning);
            }
            LogNative($"Selected encoder: {writer.EncoderName}");

            converter = new CapturedFrameConverter(decCtx, writer.Encoder, composite);
            WallClockPts? wallClock = UseWallClockPts ? new WallClockPts(writer.Encoder->time_base) : null;

            if (PipelinedEncoding)
            {
                RunPipelinedEncode(inputFmt, videoIn, pkt, decCtx, decFrame, writer, converter, wallClock, firstFrameTcs, ct);
            }
            else
            {
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

                    DecodeEncodeLoop(decCtx, decFrame, writer, converter, wallClock, firstFrameTcs);
                }

                ThrowIfErr(ffmpeg.avcodec_send_packet(decCtx, null), "flush_decoder");
                DecodeEncodeLoop(decCtx, decFrame, writer, converter, wallClock, firstFrameTcs);

                writer.Flush();
            }

            writer.WriteTrailer();

            if (writer.FrameCount == 0)
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

            AppLog.Debug("LibavGdigrabMkvSession", $"[LibavGdigrab] segment done: frames={writer.FrameCount}, packets={writer.PacketCount}, output='{outputPath}', bytes={outputBytes}");
        }
        finally
        {
            webcam?.Dispose();
            overlay?.Dispose();
            keystrokes?.Dispose();

            writer?.Dispose();
            SafeFreePkt(&pkt);
            SafeFreeFrame(&decFrame);
            converter?.Dispose();

            if (decCtx != null)
            {
                ffmpeg.avcodec_free_context(&decCtx);
            }

            if (inputFmt != null)
            {
                ffmpeg.avformat_close_input(&inputFmt);
            }

            if (demuxOpts != null)
            {
                ffmpeg.av_dict_free(&demuxOpts);
            }
        }
    }

    // Single-threaded encode loop: drain the decoder, converting each frame straight into the writer's frame and
    // encoding it here.
    private static unsafe void DecodeEncodeLoop(
        AVCodecContext* decCtx,
        AVFrame* decFrame,
        RecordingSegmentWriter writer,
        CapturedFrameConverter converter,
        WallClockPts? wallClock,
        TaskCompletionSource<bool> firstFrameTcs)
    {
        while (TryReceiveFrame(decCtx, decFrame, "receive_frame(dec)"))
        {
            AVFrame* encFrame = writer.FrameToFill();
            converter.Convert(decFrame, encFrame);
            long framePts = wallClock?.Next() ?? writer.FrameCount;

            if (writer.FrameCount == 0)
            {
                firstFrameTcs.TrySetResult(true);
            }

            writer.EncodeFrame(framePts);
        }
    }

    // Producer/consumer variant of the encode loop. This (the worker) thread reads + decodes + composites
    // each frame into a fresh encoder-format frame and hands it to a bounded queue; a dedicated encoder
    // thread drains the queue and muxes. Capture/composite and the (CPU-heavy) software encode overlap.
    // The writer (encoder + muxer) is used only by the consumer thread, until the producer writes the trailer after
    // joining it; the producer only reads the encoder's frame size, format and time base, fixed once it is open.
    // Under sustained back-pressure full-queue frames are dropped rather than stalling capture. Each queued frame is
    // owned by exactly one side (consumer after encode, producer on drop), so there is no double-free.
    private static unsafe void RunPipelinedEncode(
        AVFormatContext* inputFmt,
        int videoIn,
        AVPacket* readPkt,
        AVCodecContext* decCtx,
        AVFrame* decFrame,
        RecordingSegmentWriter writer,
        CapturedFrameConverter converter,
        WallClockPts? wallClock,
        TaskCompletionSource<bool> firstFrameTcs,
        CancellationToken ct)
    {
        using var queue = new BlockingCollection<IntPtr>(boundedCapacity: 8);
        Exception? consumerEx = null;
        AVCodecContext* encCtx = writer.Encoder;
        long producedFrames = 0; // dropped frames included; the writer counts the ones it encodes

        var consumer = new Thread(() =>
        {
            try
            {
                foreach (IntPtr p in queue.GetConsumingEnumerable())
                {
                    writer.EncodeAndFree((AVFrame*)p, "send_frame(enc,pipe)");
                }

                // Flush the encoder on the thread that owns it.
                writer.Flush("flush_encoder(pipe)");
            }
            catch (Exception ex)
            {
                consumerEx = ex;
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
                DecodeComposeEnqueue(decCtx, decFrame, encCtx, converter, wallClock, queue, ref producedFrames, firstFrameTcs);
            }

            ffmpeg.avcodec_send_packet(decCtx, null);
            DecodeComposeEnqueue(decCtx, decFrame, encCtx, converter, wallClock, queue, ref producedFrames, firstFrameTcs);
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

        if (consumerEx != null)
        {
            throw consumerEx;
        }
    }

    // Producer half of the pipeline: drain the decoder, convert each frame into a fresh encoder-format frame and
    // enqueue it, dropping it if the queue is full. Its pts is stamped here, at arrival, and travels with it through
    // the queue (the consumer only encodes), so wall-clock pacing is as correct pipelined as not.
    private static unsafe void DecodeComposeEnqueue(
        AVCodecContext* decCtx,
        AVFrame* decFrame,
        AVCodecContext* encCtx,
        CapturedFrameConverter converter,
        WallClockPts? wallClock,
        BlockingCollection<IntPtr> queue,
        ref long producedFrames,
        TaskCompletionSource<bool> firstFrameTcs)
    {
        while (TryReceiveFrame(decCtx, decFrame, "receive_frame(dec,pipe)"))
        {
            AVFrame* encFrame = ffmpeg.av_frame_alloc();
            try
            {
                encFrame->format = (int)encCtx->pix_fmt;
                encFrame->width = encCtx->width;
                encFrame->height = encCtx->height;
                ThrowIfErr(ffmpeg.av_frame_get_buffer(encFrame, 32), "frame_get_buffer(enc,pipe)");
                converter.Convert(decFrame, encFrame);
            }
            catch
            {
                ffmpeg.av_frame_free(&encFrame);
                throw;
            }

            encFrame->pts = wallClock?.Next() ?? producedFrames;
            producedFrames++;
            if (producedFrames == 1)
            {
                firstFrameTcs.TrySetResult(true);
            }

            if (!queue.TryAdd((IntPtr)encFrame))
            {
                // Queue full — drop this frame rather than stall capture.
                ffmpeg.av_frame_free(&encFrame);
            }
        }
    }

    // Takes the decoder's next frame into decFrame; false once it needs another packet or has been drained.
    private static unsafe bool TryReceiveFrame(AVCodecContext* decCtx, AVFrame* decFrame, string context)
    {
        ffmpeg.av_frame_unref(decFrame);
        int gr = ffmpeg.avcodec_receive_frame(decCtx, decFrame);
        if (gr == ffmpeg.AVERROR_EOF || gr == ffmpeg.AVERROR(11))
        {
            return false;
        }

        ThrowIfErr(gr, context);
        return true;
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

    /// <summary>
    /// Converts each captured frame into the encoder's pixel format and size. With overlays to burn in (cursor,
    /// clicks, webcam picture-in-picture, keystrokes) it goes through a BGRA frame they are drawn on; otherwise it
    /// scales directly. The scalers and the BGRA frame are set up from the first frame and kept until disposed.
    /// </summary>
    private sealed unsafe class CapturedFrameConverter : IDisposable
    {
        private readonly AVCodecContext* _decoder;
        private readonly AVPixelFormat _targetFormat;
        private readonly int _targetWidth;
        private readonly int _targetHeight;
        private readonly Action<SKBitmap>? _overlays;
        private SwsContext* _toTarget;
        private SwsContext* _toBgra;
        private AVFrame* _bgra;

        /// <param name="decoder">The capture decoder, whose size stands in for a frame that carries none.</param>
        /// <param name="encoder">The opened encoder, whose pixel format and size every frame is converted to.</param>
        /// <param name="overlays">Draws every overlay onto a BGRA frame, or null when there are none.</param>
        public CapturedFrameConverter(AVCodecContext* decoder, AVCodecContext* encoder, Action<SKBitmap>? overlays)
        {
            _decoder = decoder;
            _targetFormat = encoder->pix_fmt;
            _targetWidth = encoder->width;
            _targetHeight = encoder->height;
            _overlays = overlays;
        }

        /// <summary>Converts <paramref name="captured"/> into <paramref name="target"/>, a frame in the encoder's format.</summary>
        public void Convert(AVFrame* captured, AVFrame* target)
        {
            if (_toTarget == null)
            {
                SetUp(captured);
            }

            if (_overlays != null)
            {
                // captured → BGRA, the overlays drawn onto those pixels in place, then BGRA → encoder format.
                ffmpeg.sws_scale(_toBgra, captured->data, captured->linesize, 0, captured->height, _bgra->data, _bgra->linesize);

                var info = new SKImageInfo(_bgra->width, _bgra->height, SKColorType.Bgra8888, SKAlphaType.Premul);
                using (var sk = new SKBitmap())
                {
                    if (sk.InstallPixels(info, (IntPtr)_bgra->data[0], _bgra->linesize[0]))
                    {
                        _overlays(sk);
                    }
                }

                ffmpeg.sws_scale(_toTarget, _bgra->data, _bgra->linesize, 0, _bgra->height, target->data, target->linesize);
            }
            else
            {
                ffmpeg.sws_scale(_toTarget, captured->data, captured->linesize, 0, captured->height, target->data, target->linesize);
            }
        }

        private void SetUp(AVFrame* captured)
        {
            var srcFmt = (AVPixelFormat)captured->format;
            int srcW = captured->width > 0 ? captured->width : _decoder->width;
            int srcH = captured->height > 0 ? captured->height : _decoder->height;

            if (_overlays != null)
            {
                _toBgra = ffmpeg.sws_getContext(
                    srcW, srcH, srcFmt,
                    srcW, srcH, AVPixelFormat.AV_PIX_FMT_BGRA,
                    (int)SwsFlags.SWS_FAST_BILINEAR, null, null, null);
                _toTarget = ffmpeg.sws_getContext(
                    srcW, srcH, AVPixelFormat.AV_PIX_FMT_BGRA,
                    _targetWidth, _targetHeight, _targetFormat,
                    (int)SwsFlags.SWS_FAST_BILINEAR, null, null, null);
                if (_toBgra == null || _toTarget == null)
                {
                    throw new InvalidOperationException("sws_getContext failed for BGRA composite path.");
                }

                _bgra = ffmpeg.av_frame_alloc();
                _bgra->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
                _bgra->width = srcW;
                _bgra->height = srcH;
                ThrowIfErr(ffmpeg.av_frame_get_buffer(_bgra, 32), "frame_get_buffer(bgra)");
            }
            else
            {
                _toTarget = ffmpeg.sws_getContext(
                    srcW, srcH, srcFmt,
                    _targetWidth, _targetHeight, _targetFormat,
                    (int)SwsFlags.SWS_FAST_BILINEAR, null, null, null);
                if (_toTarget == null)
                {
                    throw new InvalidOperationException($"sws_getContext failed for src={srcFmt} {srcW}x{srcH} -> dst={_targetFormat} {_targetWidth}x{_targetHeight}");
                }
            }
        }

        public void Dispose()
        {
            if (_toTarget != null)
            {
                ffmpeg.sws_freeContext(_toTarget);
                _toTarget = null;
            }

            if (_toBgra != null)
            {
                ffmpeg.sws_freeContext(_toBgra);
                _toBgra = null;
            }

            if (_bgra != null)
            {
                AVFrame* bgra = _bgra;
                ffmpeg.av_frame_free(&bgra);
                _bgra = null;
            }
        }
    }

    /// <summary>
    /// Stamps frames by when they are captured rather than by count (see <see cref="UseWallClockPts"/>): the time since
    /// the first frame times fps, in the encoder's 1/fps time base. The clock starts at that first frame, so it is pts 0
    /// however long capture took to start, and a frame landing in the same tick as the one before is moved one tick on,
    /// so timestamps only increase. Both paths stamp on the capture thread, so it needs no locking.
    /// </summary>
    private sealed class WallClockPts
    {
        private readonly Stopwatch _clock = new();
        private readonly double _fps;
        private long _last = -1;

        public WallClockPts(AVRational timeBase)
        {
            _fps = timeBase.num != 0 ? timeBase.den / (double)timeBase.num : 30.0;
        }

        public long Next()
        {
            if (!_clock.IsRunning)
            {
                _clock.Start();
            }

            long pts = (long)(_clock.Elapsed.TotalSeconds * _fps);
            if (pts <= _last)
            {
                pts = _last + 1;
            }

            _last = pts;
            return pts;
        }
    }
}
