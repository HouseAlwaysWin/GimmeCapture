using System;
using System.Globalization;
using System.Threading;
using FFmpeg.AutoGen;

namespace GimmeCapture.Services.Core.Media.NativeFFmpeg;

/// <summary>
/// Grabs a screen region through libavdevice: gdigrab on Windows, x11grab on Linux. Each frame arrives as a packet, a
/// bitmap or raw pixels, that a decoder turns into a picture. Both decoders give a packet's picture at once and hold
/// nothing back, so a recording that stops reading loses no frame by not draining them.
/// </summary>
internal sealed unsafe class LibavScreenGrabSource : ICapturedFrameSource
{
    private AVFormatContext* _input;
    private AVCodecContext* _decoder;
    private AVPacket* _packet;
    private readonly int _videoIn;

    private LibavScreenGrabSource(string name, AVFormatContext* input, AVCodecContext* decoder, int videoIn)
    {
        Name = name;
        _input = input;
        _decoder = decoder;
        _videoIn = videoIn;
        _packet = ffmpeg.av_packet_alloc();
    }

    public string Name { get; }

    /// <summary>Opens the grabber on the region, in physical pixels of the virtual screen. A failure throws.</summary>
    public static LibavScreenGrabSource Open(int offsetX, int offsetY, int width, int height, int fps, bool drawMouse)
    {
        AVFormatContext* input = null;
        AVCodecContext* decoder = null;
        AVDictionary* demuxOpts = null;
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

            ffmpeg.av_dict_set(&demuxOpts, "framerate", fps.ToString(CultureInfo.InvariantCulture), 0);
            ffmpeg.av_dict_set(&demuxOpts, "video_size", string.Create(CultureInfo.InvariantCulture, $"{width}x{height}"), 0);
            ffmpeg.av_dict_set(&demuxOpts, "draw_mouse", drawMouse ? "1" : "0", 0);
            ffmpeg.av_dict_set(&demuxOpts, "probesize", "32768", 0);
            ffmpeg.av_dict_set(&demuxOpts, "analyzeduration", "0", 0);

            string inputUrl;
            if (isWindows)
            {
                // gdigrab: the capture-region offset is passed as options; the URL is "desktop".
                ffmpeg.av_dict_set(&demuxOpts, "offset_x", offsetX.ToString(CultureInfo.InvariantCulture), 0);
                ffmpeg.av_dict_set(&demuxOpts, "offset_y", offsetY.ToString(CultureInfo.InvariantCulture), 0);
                inputUrl = "desktop";
            }
            else
            {
                // x11grab: the offset lives in the URL as :<display>+x,y (offset_x/y options are ignored).
                string display = Environment.GetEnvironmentVariable("DISPLAY") ?? ":0";
                inputUrl = string.Create(CultureInfo.InvariantCulture, $"{display}+{offsetX},{offsetY}");
            }

            ThrowIfErr(ffmpeg.avformat_open_input(&input, inputUrl, ifmt, &demuxOpts), "avformat_open_input");

            // Skip avformat_find_stream_info for gdigrab live input.
            // On some Windows systems it repeatedly logs probe warnings and delays startup.

            int videoIn = ffmpeg.av_find_best_stream(input, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            if (videoIn < 0)
            {
                throw new InvalidOperationException($"No {grabber} video stream.");
            }

            AVCodecParameters* inPar = input->streams[videoIn]->codecpar;
            AVCodec* dec = ffmpeg.avcodec_find_decoder(inPar->codec_id);
            if (dec == null)
            {
                throw new InvalidOperationException($"No decoder for codec_id={inPar->codec_id}");
            }

            decoder = ffmpeg.avcodec_alloc_context3(dec);
            ThrowIfErr(ffmpeg.avcodec_parameters_to_context(decoder, inPar), "parameters_to_context(dec)");
            ThrowIfErr(ffmpeg.avcodec_open2(decoder, dec, null), "avcodec_open2(dec)");

            var source = new LibavScreenGrabSource(grabber, input, decoder, videoIn);
            input = null;
            decoder = null;
            return source;
        }
        finally
        {
            if (decoder != null)
            {
                ffmpeg.avcodec_free_context(&decoder);
            }

            if (input != null)
            {
                ffmpeg.avformat_close_input(&input);
            }

            if (demuxOpts != null)
            {
                ffmpeg.av_dict_free(&demuxOpts);
            }
        }
    }

    public bool TryRead(AVFrame* frame, CancellationToken ct)
    {
        while (true)
        {
            ffmpeg.av_frame_unref(frame);
            int got = ffmpeg.avcodec_receive_frame(_decoder, frame);
            if (got >= 0)
            {
                return true;
            }

            if (got == ffmpeg.AVERROR_EOF)
            {
                return false;
            }

            if (got != ffmpeg.AVERROR(11))
            {
                ThrowIfErr(got, "receive_frame(dec)");
            }

            int rr = ffmpeg.av_read_frame(_input, _packet);
            if (rr == ffmpeg.AVERROR_EOF)
            {
                // The input has ended: the decoder gives what it holds and then its own end.
                ThrowIfErr(ffmpeg.avcodec_send_packet(_decoder, null), "flush_decoder");
                continue;
            }

            if (rr == ffmpeg.AVERROR(11))
            {
                // Live input can temporarily have no packet ready yet.
                if (ct.IsCancellationRequested)
                {
                    return false;
                }

                Thread.Sleep(1);
                continue;
            }

            ThrowIfErr(rr, "av_read_frame");
            if (_packet->stream_index != _videoIn)
            {
                ffmpeg.av_packet_unref(_packet);
                continue;
            }

            ThrowIfErr(ffmpeg.avcodec_send_packet(_decoder, _packet), "send_packet(dec)");
            ffmpeg.av_packet_unref(_packet);
        }
    }

    public void Dispose()
    {
        if (_packet != null)
        {
            AVPacket* packet = _packet;
            ffmpeg.av_packet_free(&packet);
            _packet = null;
        }

        if (_decoder != null)
        {
            AVCodecContext* decoder = _decoder;
            ffmpeg.avcodec_free_context(&decoder);
            _decoder = null;
        }

        if (_input != null)
        {
            AVFormatContext* input = _input;
            ffmpeg.avformat_close_input(&input);
            _input = null;
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
