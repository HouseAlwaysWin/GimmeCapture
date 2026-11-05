using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FFmpeg.AutoGen;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Services.Core.Media.NativeFFmpeg;

/// <summary>
/// The frames a paused preview shows, decoded from one video that stays open: the file, its streams, the decoder and its
/// GPU device are set up for the first frame and kept, where the compress editor and its compare set them all up again
/// for every frame they showed. The frame is the first at or after the time asked for. A step forward decodes on from
/// where the decoder stands instead of seeking back to a keyframe, and asking again for the frame last given, to draw it
/// rotated or cropped, gives it back without decoding. The file closes once no frame has been asked for in a while.
/// </summary>
internal sealed class LibavStillFrameDecoder : IDisposable
{
    /// <summary>
    /// How far ahead of the decoder a frame can be and still be reached by decoding on, for a file with no index of its
    /// keyframes. With an index, decoding on is chosen whenever no keyframe lies between: a seek would start from one.
    /// </summary>
    internal const double DecodeOnSeconds = 1.0;

    /// <summary>
    /// How long the file stays open with no frame asked for. Open, the decoder holds 70-90 MB of the process (a 1450x900
    /// recording and a 1920x1080 clip, decoded on the CPU or the GPU); the next frame after it closes opens it again.
    /// </summary>
    internal static readonly TimeSpan IdleCloseDelay = TimeSpan.FromSeconds(30);

    // As in the player: a frame up to half a millisecond before the time asked for counts as at it.
    private const double FrameTimeTolerance = 0.0005;

    private readonly int _width;
    private readonly int _height;

    // One request at a time, and the idle close only between them. Dispose closes the file at once when nothing is
    // using it, and otherwise leaves that to whatever is.
    private readonly SemaphoreSlim _requests = new(1, 1);
    private readonly object _stateLock = new();
    private readonly IdleReleaseScheduler _idleClose;
    private bool _disposed;
    private bool _busy;

    // Touched only while _busy.
    private unsafe AVFormatContext* _input;
    private unsafe AVCodecContext* _decoder;
    private unsafe AVBufferRef* _hwDevice;
    private unsafe SwsContext* _scaler;
    private unsafe AVPacket* _packet;
    private unsafe AVFrame* _decoded;
    private unsafe AVFrame* _downloaded;
    private unsafe AVFrame* _scaled;
    private int _videoStream = -1;
    private AVRational _timeBase;
    private int _scalerWidth;
    private int _scalerHeight;
    private AVPixelFormat _scalerFormat = AVPixelFormat.AV_PIX_FMT_NONE;
    private AVColorSpace _scalerSpace;
    private AVColorRange _scalerRange;
    private bool _draining;

    // The time of the last frame out of the decoder. Negative infinity just after the file opens, when the decoder stands
    // at its start; NaN when the next frame needs a seek (none has come out since the last one, or the end was reached).
    private double _decodedSeconds = double.NaN;

    // The frame handed out last, the time asked for that gave it, and the frame's own time.
    private byte[]? _lastFrame;
    private double _lastAskedSeconds;
    private double _lastFrameSeconds;

    public LibavStillFrameDecoder(string videoPath, int width, int height, TimeSpan? idleCloseDelay = null)
    {
        VideoPath = videoPath;
        _width = width;
        _height = height;
        _idleClose = new IdleReleaseScheduler(idleCloseDelay ?? IdleCloseDelay, CloseWhenIdle);
    }

    public string VideoPath { get; }

    /// <summary>How many times the file was opened: once, unless it closed for being idle or for a failure.</summary>
    internal int Opens { get; private set; }

    /// <summary>How many times the decoder sought back to a keyframe.</summary>
    internal int Seeks { get; private set; }

    /// <summary>Whether the file is open now.</summary>
    internal unsafe bool IsOpen => _input != null;

    /// <summary>The time of the frame given last: the first at or after the time asked for. Read between requests.</summary>
    internal double LastFrameSeconds => _lastFrameSeconds;

    /// <summary>
    /// The frame at <paramref name="seconds"/> as width × height BGRA pixels with the rows packed, decoded on the pool;
    /// null when no frame is at or after that time, or when cancelled or disposed first. The same frame asked for again
    /// comes back as the same array, so it is only to be read.
    /// </summary>
    public async Task<byte[]?> DecodeFrameAtAsync(double seconds, CancellationToken ct = default)
    {
        try
        {
            await _requests.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        try
        {
            if (ct.IsCancellationRequested || !TryBeginUse())
            {
                return null;
            }

            try
            {
                double target = Math.Max(0, seconds);
                if (_lastFrame != null && target >= _lastAskedSeconds && target <= _lastFrameSeconds + FrameTimeTolerance)
                {
                    return _lastFrame;
                }

                return await Task.Run(() => DecodeAt(target, ct), CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                if (EndUse())
                {
                    _idleClose.NotifyUse();
                }
            }
        }
        finally
        {
            _requests.Release();
        }
    }

    // Marks the decoder in use, unless it has been disposed.
    private bool TryBeginUse()
    {
        lock (_stateLock)
        {
            if (_disposed)
            {
                return false;
            }

            _busy = true;
            return true;
        }
    }

    // Ends a use, closing the file if Dispose came meanwhile and left that to it. False once disposed.
    private bool EndUse()
    {
        bool disposed;
        lock (_stateLock)
        {
            _busy = false;
            disposed = _disposed;
        }

        if (disposed)
        {
            Close(dropLastFrame: true);
        }

        return !disposed;
    }

    // The idle close, on the pool: between requests only, keeping the frame last given for a redraw.
    private void CloseWhenIdle()
    {
        if (!_requests.Wait(0))
        {
            return; // a request is under way, and starts the countdown again when it ends
        }

        try
        {
            if (TryBeginUse())
            {
                Close(dropLastFrame: false);
                EndUse();
            }
        }
        finally
        {
            _requests.Release();
        }
    }

    // On the pool, for one request at a time. A failure closes the file, so the next request opens it afresh.
    private unsafe byte[]? DecodeAt(double target, CancellationToken ct)
    {
        try
        {
            if (_input == null)
            {
                Open();
            }

            if (!CanDecodeOnTo(target))
            {
                Seek(target);
            }

            int transferFailures = 0;
            while (!ct.IsCancellationRequested)
            {
                int received = ffmpeg.avcodec_receive_frame(_decoder, _decoded);
                if (received == 0)
                {
                    long ts = _decoded->best_effort_timestamp;
                    double frameSeconds = ts >= 0 ? ts * ffmpeg.av_q2d(_timeBase) : 0;
                    _decodedSeconds = frameSeconds;
                    if (frameSeconds + FrameTimeTolerance < target)
                    {
                        continue;
                    }

                    byte[]? frame = ToBgra(ref transferFailures);
                    if (frame == null)
                    {
                        continue; // the GPU would not hand the frame over: the next one, as the player does
                    }

                    _lastFrame = frame;
                    _lastAskedSeconds = target;
                    _lastFrameSeconds = frameSeconds;
                    return frame;
                }

                if (received == ffmpeg.AVERROR_EOF || (received == ffmpeg.AVERROR(11) && _draining))
                {
                    _decodedSeconds = double.NaN; // past the last frame; the next request's seek resets the decoder
                    return null;
                }

                if (received != ffmpeg.AVERROR(11))
                {
                    ThrowIfErr(received, "still_receive_frame");
                }

                SendNextPacket();
            }

            return null;
        }
        catch
        {
            Close(dropLastFrame: true);
            throw;
        }
    }

    // Whether the frame at target is still ahead of the decoder, and near enough that decoding on to it costs no more
    // than a seek would: no keyframe between, or no more than DecodeOnSeconds when the file has no index.
    private bool CanDecodeOnTo(double target)
    {
        if (double.IsNaN(_decodedSeconds))
        {
            return false;
        }

        if (double.IsNegativeInfinity(_decodedSeconds))
        {
            return target <= DecodeOnSeconds;
        }

        if (target <= _decodedSeconds + FrameTimeTolerance)
        {
            return false; // that frame, or one after the time asked for, has already come out
        }

        double keyframe = KeyframeAtOrBefore(target);
        return double.IsNaN(keyframe)
            ? target - _decodedSeconds <= DecodeOnSeconds
            : keyframe <= _decodedSeconds + FrameTimeTolerance;
    }

    // When the keyframe a seek to `seconds` would start from shows, by the file's index; NaN when it has none there.
    private unsafe double KeyframeAtOrBefore(double seconds)
    {
        AVStream* stream = _input->streams[_videoStream];
        long timestamp = (long)(seconds / ffmpeg.av_q2d(_timeBase));
        int index = ffmpeg.av_index_search_timestamp(stream, timestamp, ffmpeg.AVSEEK_FLAG_BACKWARD);
        AVIndexEntry* entry = index >= 0 ? ffmpeg.avformat_index_get_entry(stream, index) : null;
        return entry != null ? entry->timestamp * ffmpeg.av_q2d(_timeBase) : double.NaN;
    }

    private unsafe void Seek(double target)
    {
        long timestamp = (long)(target / ffmpeg.av_q2d(_timeBase));
        ThrowIfErr(ffmpeg.av_seek_frame(_input, _videoStream, timestamp, ffmpeg.AVSEEK_FLAG_BACKWARD), "still_seek_frame");
        ffmpeg.avcodec_flush_buffers(_decoder);
        _draining = false;
        _decodedSeconds = double.NaN;
        Seeks++;
    }

    // Hands the decoder the file's next video packet, or tells it the file has ended.
    private unsafe void SendNextPacket()
    {
        while (true)
        {
            int read = ffmpeg.av_read_frame(_input, _packet);
            if (read == ffmpeg.AVERROR_EOF)
            {
                ThrowIfErr(ffmpeg.avcodec_send_packet(_decoder, null), "still_send_eof");
                _draining = true;
                return;
            }

            if (read == ffmpeg.AVERROR(11))
            {
                continue;
            }

            ThrowIfErr(read, "still_read_frame");
            if (_packet->stream_index != _videoStream)
            {
                ffmpeg.av_packet_unref(_packet);
                continue;
            }

            int sent = ffmpeg.avcodec_send_packet(_decoder, _packet);
            ffmpeg.av_packet_unref(_packet);
            ThrowIfErr(sent, "still_send_packet");
            return;
        }
    }

    // The decoded frame as BGRA at the preview's size, in a new array; null when the GPU would not hand it over, until
    // that has failed MaxHwTransferFailures times in a row.
    private unsafe byte[]? ToBgra(ref int transferFailures)
    {
        AVFrame* source = _decoded;
        if (_decoded->format == (int)AVPixelFormat.AV_PIX_FMT_D3D11)
        {
            ffmpeg.av_frame_unref(_downloaded);
            int transferred = ffmpeg.av_hwframe_transfer_data(_downloaded, _decoded, 0);
            if (transferred < 0)
            {
                if (++transferFailures >= LibavVideoFramePlayer.MaxHwTransferFailures)
                {
                    throw FFmpegErrors.ToException(transferred, "still_hwframe_transfer_data");
                }

                return null;
            }

            source = _downloaded;
        }

        // The colours are the decoded frame's: a download from the GPU carries none.
        if (_scaler == null || source->width != _scalerWidth || source->height != _scalerHeight ||
            (AVPixelFormat)source->format != _scalerFormat ||
            _decoded->colorspace != _scalerSpace || _decoded->color_range != _scalerRange)
        {
            if (_scaler != null)
            {
                ffmpeg.sws_freeContext(_scaler);
            }

            _scalerWidth = source->width;
            _scalerHeight = source->height;
            _scalerFormat = (AVPixelFormat)source->format;
            _scalerSpace = _decoded->colorspace;
            _scalerRange = _decoded->color_range;
            _scaler = ffmpeg.sws_getContext(
                _scalerWidth, _scalerHeight, _scalerFormat,
                _width, _height, AVPixelFormat.AV_PIX_FMT_BGRA,
                (int)SwsFlags.SWS_BILINEAR, null, null, null);
            if (_scaler == null)
            {
                throw new InvalidOperationException("sws_getContext failed.");
            }

            LibavColorMatrix.ReadAs(_scaler, _scalerSpace, _scalerRange);
        }

        ThrowIfErr(ffmpeg.av_frame_make_writable(_scaled), "still_frame_writable");
        ffmpeg.sws_scale(_scaler, source->data, source->linesize, 0, source->height, _scaled->data, _scaled->linesize);

        var frame = new byte[_width * _height * 4];
        LibavVideoFramePlayer.CopyBgraFrame(_scaled, _width, _height, frame);
        return frame;
    }

    // Opens the file and builds the decoder as the player does: the same bounded probe, D3D11VA when it can.
    private unsafe void Open()
    {
        FFmpegRuntime.EnsureInitialized();
        var watch = Stopwatch.StartNew();

        AVFormatContext* input = null;
        AVDictionary* options = null;
        ffmpeg.av_dict_set(&options, "analyzeduration", "5000000", 0);
        ffmpeg.av_dict_set(&options, "probesize", "15000000", 0);
        int opened = ffmpeg.avformat_open_input(&input, VideoPath, null, &options);
        ffmpeg.av_dict_free(&options);
        ThrowIfErr(opened, "still_open_input");
        _input = input;
        ThrowIfErr(ffmpeg.avformat_find_stream_info(_input, null), "still_find_stream_info");

        _videoStream = ffmpeg.av_find_best_stream(_input, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
        if (_videoStream < 0)
        {
            throw new InvalidOperationException("No video stream.");
        }

        AVStream* stream = _input->streams[_videoStream];
        _timeBase = stream->time_base;
        AVCodec* codec = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
        if (codec == null)
        {
            throw new InvalidOperationException($"Decoder not found for {stream->codecpar->codec_id}");
        }

        _decoder = ffmpeg.avcodec_alloc_context3(codec);
        if (_decoder == null)
        {
            throw new OutOfMemoryException("still_alloc_decoder");
        }

        ThrowIfErr(ffmpeg.avcodec_parameters_to_context(_decoder, stream->codecpar), "still_par_to_ctx");
        _hwDevice = LibavVideoFramePlayer.TryInitHardwareDecode(codec, _decoder, out string? hwReason);
        _decoder->thread_count = LibavVideoFramePlayer.DecodeThreads(_hwDevice != null);
        _decoder->thread_type = ffmpeg.FF_THREAD_FRAME | ffmpeg.FF_THREAD_SLICE;
        ThrowIfErr(ffmpeg.avcodec_open2(_decoder, codec, null), "still_open_decoder");

        _packet = ffmpeg.av_packet_alloc();
        _decoded = ffmpeg.av_frame_alloc();
        _downloaded = ffmpeg.av_frame_alloc();
        _scaled = ffmpeg.av_frame_alloc();
        if (_packet == null || _decoded == null || _downloaded == null || _scaled == null)
        {
            throw new OutOfMemoryException("still_alloc_packet_frame");
        }

        _scaled->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
        _scaled->width = _width;
        _scaled->height = _height;
        ThrowIfErr(ffmpeg.av_frame_get_buffer(_scaled, 1), "still_frame_buffer");

        _decodedSeconds = double.NegativeInfinity;
        _draining = false;
        Opens++;
        AppLog.Debug(
            "LibavStillFrameDecoder.Open",
            $"{Path.GetFileName(VideoPath)} {(_hwDevice != null ? "D3D11VA" : "software, " + hwReason)} in {watch.ElapsedMilliseconds} ms");
    }

    private unsafe void Close(bool dropLastFrame)
    {
        AVPacket* packet = _packet;
        if (packet != null) ffmpeg.av_packet_free(&packet);
        _packet = null;
        AVFrame* frame = _decoded;
        if (frame != null) ffmpeg.av_frame_free(&frame);
        _decoded = null;
        frame = _downloaded;
        if (frame != null) ffmpeg.av_frame_free(&frame);
        _downloaded = null;
        frame = _scaled;
        if (frame != null) ffmpeg.av_frame_free(&frame);
        _scaled = null;
        if (_scaler != null) ffmpeg.sws_freeContext(_scaler);
        _scaler = null;
        AVCodecContext* decoder = _decoder;
        if (decoder != null) ffmpeg.avcodec_free_context(&decoder);
        _decoder = null;
        AVBufferRef* hwDevice = _hwDevice;
        if (hwDevice != null) ffmpeg.av_buffer_unref(&hwDevice);
        _hwDevice = null;
        AVFormatContext* input = _input;
        if (input != null) ffmpeg.avformat_close_input(&input);
        _input = null;

        _videoStream = -1;
        _scalerFormat = AVPixelFormat.AV_PIX_FMT_NONE;
        _decodedSeconds = double.NaN;
        _draining = false;
        if (dropLastFrame)
        {
            _lastFrame = null;
        }
    }

    private static void ThrowIfErr(int err, string ctx)
    {
        if (err < 0) throw FFmpegErrors.ToException(err, ctx);
    }

    public void Dispose()
    {
        bool close;
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            close = !_busy;
        }

        _idleClose.Cancel();
        if (close)
        {
            Close(dropLastFrame: true);
        }
    }
}
