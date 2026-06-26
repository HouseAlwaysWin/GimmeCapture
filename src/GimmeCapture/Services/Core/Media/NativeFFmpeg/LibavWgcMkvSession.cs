using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using FFmpeg.AutoGen;
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
    private CancellationTokenSource? _cts;
    private Task<bool>? _worker;

    public Task<bool>? Worker => _worker;
    public string? LastErrorMessage { get; private set; }
    public string? LastWarningMessage { get; private set; }
    public string? SelectedEncoderName { get; private set; }

    /// <summary>
    /// When true (default) GPU / Media-Foundation encoders (NVENC/QSV/AMF/MF) are tried before the
    /// software libx264/265 fallback.
    /// </summary>
    public bool PreferHardwareEncoder { get; set; } = true;

    public Task<bool> StartAsync(string outputPath, IntPtr hwnd, int fps, bool drawMouse, bool useH265)
    {
        FFmpegRuntime.EnsureInitialized();
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
        var timeout = Task.Delay(5000);
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

        using var source = new WgcWindowCaptureSource(hwnd, drawMouse);
        if (!source.Start())
        {
            throw new InvalidOperationException("Windows Graphics Capture could not start for the selected window.");
        }

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

            // Prime: wait for the first captured frame so the encoder sees real content from pts 0.
            while (!ct.IsCancellationRequested)
            {
                if (source.TryCopyLatest(ref buf, out _, out _) && buf != null)
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
