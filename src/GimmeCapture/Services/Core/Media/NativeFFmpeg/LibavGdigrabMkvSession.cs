using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FFmpeg.AutoGen;

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
        AVPacket* pkt = null;
        AVFrame* decFrame = null;
        AVFrame* encFrame = null;
        AVDictionary* demuxOpts = null;
        AVDictionary* encOpts = null;

        int videoIn = -1;
        AVStream* outStream = null;
        long frameCounter = 0;
        long packetCounter = 0;
        bool encFrameInitialized = false;

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

            encCtx = OpenRecordingEncoderContext(
                useH265,
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

                DecodeEncodeLoop(decCtx, encCtx, ref sws, decFrame, encFrame, ref encFrameInitialized, outputFmt, outStream, pkt, ref frameCounter, ref packetCounter, firstFrameTcs);
            }

            ThrowIfErr(ffmpeg.avcodec_send_packet(decCtx, null), "flush_decoder");
            DecodeEncodeLoop(decCtx, encCtx, ref sws, decFrame, encFrame, ref encFrameInitialized, outputFmt, outStream, pkt, ref frameCounter, ref packetCounter, firstFrameTcs);

            ThrowIfErr(ffmpeg.avcodec_send_frame(encCtx, null), "flush_encoder");
            WriteEncodedPackets(encCtx, outputFmt, outStream, pkt, ref packetCounter);

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
            SafeFreePkt(&pkt);
            SafeFreeFrame(&decFrame);
            SafeFreeFrame(&encFrame);

            if (sws != null)
            {
                ffmpeg.sws_freeContext(sws);
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
        AVFrame* decFrame,
        AVFrame* encFrame,
        ref bool encFrameInitialized,
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

            if (sws == null)
            {
                var srcFmt = (AVPixelFormat)decFrame->format;
                int srcW = decFrame->width > 0 ? decFrame->width : decCtx->width;
                int srcH = decFrame->height > 0 ? decFrame->height : decCtx->height;
                sws = ffmpeg.sws_getContext(
                    srcW,
                    srcH,
                    srcFmt,
                    encCtx->width,
                    encCtx->height,
                    encCtx->pix_fmt,
                    (int)SwsFlags.SWS_FAST_BILINEAR,
                    null,
                    null,
                    null);
                if (sws == null)
                {
                    throw new InvalidOperationException($"sws_getContext failed for src={srcFmt} {srcW}x{srcH} -> dst={encCtx->pix_fmt} {encCtx->width}x{encCtx->height}");
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
            ffmpeg.sws_scale(sws, decFrame->data, decFrame->linesize, 0, decFrame->height, encFrame->data, encFrame->linesize);
            encFrame->pts = frameCounter++;
            if (frameCounter == 1)
            {
                firstFrameTcs.TrySetResult(true);
            }

            ThrowIfErr(ffmpeg.avcodec_send_frame(encCtx, encFrame), "send_frame(enc)");
            WriteEncodedPackets(encCtx, outputFmt, outStream, pkt, ref packetCounter);
        }
    }

    private static unsafe void WriteEncodedPackets(
        AVCodecContext* encCtx,
        AVFormatContext* outputFmt,
        AVStream* outStream,
        AVPacket* pkt,
        ref long packetCounter)
    {
        while (true)
        {
            int gp = ffmpeg.avcodec_receive_packet(encCtx, pkt);
            if (gp == ffmpeg.AVERROR_EOF || gp == ffmpeg.AVERROR(11))
            {
                break;
            }
            ThrowIfErr(gp, "receive_packet(enc)");

            ffmpeg.av_packet_rescale_ts(pkt, encCtx->time_base, outStream->time_base);
            pkt->stream_index = outStream->index;
            ThrowIfErr(ffmpeg.av_interleaved_write_frame(outputFmt, pkt), "write_packet(enc)");
            packetCounter++;
            ffmpeg.av_packet_unref(pkt);
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

    private static unsafe AVCodecContext* OpenRecordingEncoderContext(
        bool preferH265,
        int width,
        int height,
        int fps,
        AVDictionary** encOpts,
        out string selectedEncoderName,
        out string? warningMessage)
    {
        string[] preferredNames = preferH265
            ? ["libx265", "hevc_mf", "libx264", "libopenh264", "mpeg4", "h264_mf"]
            : ["libx264", "libopenh264", "mpeg4", "h264_mf"];

        selectedEncoderName = preferH265 ? "libx265" : "libx264";
        warningMessage = null;
        string requestedEncoderName = selectedEncoderName;
        string? lastOpenError = null;
        bool foundRequestedEncoder = false;

        foreach (string candidateName in preferredNames)
        {
            AVCodec* candidate = ffmpeg.avcodec_find_encoder_by_name(candidateName);
            if (candidate == null)
            {
                LogNative($"Encoder candidate unavailable: {candidateName}");
                continue;
            }

            if (candidateName == requestedEncoderName)
            {
                foundRequestedEncoder = true;
            }

            AVCodecContext* candidateCtx = ffmpeg.avcodec_alloc_context3(candidate);
            if (candidateCtx == null)
            {
                LogNative($"Encoder candidate alloc failed: {candidateName}");
                continue;
            }

            AVDictionary* candidateOpts = null;
            try
            {
                ConfigureEncoderContext(candidateCtx, candidate, candidateName, width, height, fps, &candidateOpts);
                int openResult = ffmpeg.avcodec_open2(candidateCtx, candidate, &candidateOpts);
                if (openResult >= 0)
                {
                    AVCodecContext* openedCtx = candidateCtx;
                    if (encOpts != null)
                    {
                        *encOpts = candidateOpts;
                    }
                    candidateCtx = null;
                    candidateOpts = null;

                    selectedEncoderName = candidateName;
                    if (preferH265 && candidateName != requestedEncoderName)
                    {
                        warningMessage = $"Encoder '{requestedEncoderName}' unavailable. Falling back to '{candidateName}'.";
                    }

                    return openedCtx;
                }

                lastOpenError = $"{candidateName}: {FFmpegErrors.Describe(openResult)} ({openResult})";
                LogNative($"Encoder candidate open failed: {lastOpenError}");
            }
            finally
            {
                if (candidateOpts != null)
                {
                    ffmpeg.av_dict_free(&candidateOpts);
                }

                if (candidateCtx != null)
                {
                    ffmpeg.avcodec_free_context(&candidateCtx);
                }
            }
        }

        if (!foundRequestedEncoder)
        {
            throw new InvalidOperationException($"Encoder '{requestedEncoderName}' unavailable.");
        }

        throw new InvalidOperationException($"No compatible video encoder available. Last error: {lastOpenError ?? "unknown"}");
    }

    private static unsafe string GetCodecName(AVCodec* codec)
    {
        if (codec == null || codec->name == null)
        {
            return string.Empty;
        }

        return Marshal.PtrToStringAnsi((nint)codec->name) ?? string.Empty;
    }

    private static unsafe void ConfigureEncoderContext(
        AVCodecContext* encCtx,
        AVCodec* enc,
        string encoderName,
        int width,
        int height,
        int fps,
        AVDictionary** encOpts)
    {
        encCtx->codec_id = enc->id;
        encCtx->width = width;
        encCtx->height = height;
        encCtx->pix_fmt = ChooseEncoderPixelFormat(enc, encoderName);
        encCtx->time_base = new AVRational { num = 1, den = fps };
        encCtx->framerate = new AVRational { num = fps, den = 1 };
        encCtx->gop_size = fps * 2;
        encCtx->max_b_frames = 0;
        encCtx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

        if (string.Equals(encoderName, "libx264", StringComparison.OrdinalIgnoreCase)
            || string.Equals(encoderName, "libx265", StringComparison.OrdinalIgnoreCase))
        {
            ffmpeg.av_dict_set(encOpts, "preset", "ultrafast", 0);
            ffmpeg.av_dict_set(encOpts, "tune", "zerolatency", 0);
            ffmpeg.av_dict_set(encOpts, "crf", "23", 0);
            return;
        }

        long targetBitrate = Math.Clamp((long)width * height * Math.Max(fps, 1) / 8, 800_000L, 16_000_000L);
        encCtx->bit_rate = targetBitrate;
    }

    private static unsafe AVPixelFormat ChooseEncoderPixelFormat(AVCodec* enc, string encoderName)
    {
        AVPixelFormat[] preferredFormats = IsHardwareOrMediaFoundationEncoder(encoderName)
            ? [AVPixelFormat.AV_PIX_FMT_NV12, AVPixelFormat.AV_PIX_FMT_YUV420P, AVPixelFormat.AV_PIX_FMT_P010LE]
            : [AVPixelFormat.AV_PIX_FMT_YUV420P, AVPixelFormat.AV_PIX_FMT_NV12];

        if (enc->pix_fmts == null)
        {
            return preferredFormats[0];
        }

        for (int i = 0; enc->pix_fmts[i] != AVPixelFormat.AV_PIX_FMT_NONE; i++)
        {
            foreach (var preferredFormat in preferredFormats)
            {
                if (enc->pix_fmts[i] == preferredFormat)
                {
                    return preferredFormat;
                }
            }
        }

        return enc->pix_fmts[0];
    }

    private static bool IsHardwareOrMediaFoundationEncoder(string encoderName)
    {
        return encoderName.Contains("_mf", StringComparison.OrdinalIgnoreCase)
            || encoderName.Contains("_nvenc", StringComparison.OrdinalIgnoreCase)
            || encoderName.Contains("_qsv", StringComparison.OrdinalIgnoreCase)
            || encoderName.Contains("_amf", StringComparison.OrdinalIgnoreCase)
            || encoderName.Contains("_vaapi", StringComparison.OrdinalIgnoreCase)
            || encoderName.Contains("_d3d12va", StringComparison.OrdinalIgnoreCase);
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
