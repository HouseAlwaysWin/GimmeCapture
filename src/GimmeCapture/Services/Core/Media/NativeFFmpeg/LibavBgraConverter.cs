using System;
using FFmpeg.AutoGen;

namespace GimmeCapture.Services.Core.Media.NativeFFmpeg;

/// <summary>
/// Converts the player's decoded frames to BGRA of the size they are shown at, keeping its scalers until the source
/// changes. A frame downloaded from the graphics card comes as NV12, from which swscale has no fast way to BGRA: on a
/// 1450x900 recording its general route took 5.68 M cycles a frame, which at 30 fps was most of a playing pin's decode
/// thread. Unpacking NV12 to YUV420P and taking swscale's fast YUV420P route from there took 2.36 for both steps. The
/// fast route repeats each chroma sample over its 2x2 pixels rather than blending neighbours, which differs only at
/// coloured edges (PSNR 45.7 dB against the general route on that recording, not to be told apart at full size). So an
/// NV12 frame shown at its own size goes that way, and any other frame, or one shown at another size, goes straight.
/// </summary>
internal sealed unsafe class LibavBgraConverter : IDisposable
{
    private SwsContext* _toPlanar;
    private SwsContext* _toBgra;
    private AVFrame* _planar;
    private int _sourceWidth;
    private int _sourceHeight;
    private AVPixelFormat _sourceFormat = AVPixelFormat.AV_PIX_FMT_NONE;
    private AVColorSpace _space;
    private AVColorRange _range;
    private int _outputWidth;
    private int _outputHeight;

    /// <summary>Whether the last frame went through YUV420P on its way to BGRA.</summary>
    public bool ThroughPlanar { get; private set; }

    /// <summary>
    /// Converts <paramref name="source"/>, a decoded frame in memory, into <paramref name="bgra"/>, a BGRA frame of the
    /// size it is shown at. Its colours are <paramref name="space"/> and <paramref name="range"/>: the decoded frame's,
    /// as a download from the graphics card carries none.
    /// </summary>
    public void Convert(AVFrame* source, AVColorSpace space, AVColorRange range, AVFrame* bgra)
    {
        var format = (AVPixelFormat)source->format;
        if (_toBgra == null || format != _sourceFormat || source->width != _sourceWidth || source->height != _sourceHeight
            || space != _space || range != _range || bgra->width != _outputWidth || bgra->height != _outputHeight)
        {
            SetUp(format, source->width, source->height, space, range, bgra->width, bgra->height);
        }

        if (_toPlanar != null)
        {
            ffmpeg.sws_scale(_toPlanar, source->data, source->linesize, 0, source->height, _planar->data, _planar->linesize);
            ffmpeg.sws_scale(_toBgra, _planar->data, _planar->linesize, 0, _planar->height, bgra->data, bgra->linesize);
        }
        else
        {
            ffmpeg.sws_scale(_toBgra, source->data, source->linesize, 0, source->height, bgra->data, bgra->linesize);
        }
    }

    /// <summary>Whether a frame of <paramref name="format"/> shown at its own size goes through YUV420P. Pure.</summary>
    internal static bool GoesThroughPlanar(AVPixelFormat format, int width, int height, int outputWidth, int outputHeight) =>
        format == AVPixelFormat.AV_PIX_FMT_NV12 && width == outputWidth && height == outputHeight
        && width % 2 == 0 && height % 2 == 0;

    private void SetUp(
        AVPixelFormat format, int width, int height, AVColorSpace space, AVColorRange range, int outputWidth, int outputHeight)
    {
        Release();
        _sourceFormat = format;
        _sourceWidth = width;
        _sourceHeight = height;
        _space = space;
        _range = range;
        _outputWidth = outputWidth;
        _outputHeight = outputHeight;
        ThroughPlanar = GoesThroughPlanar(format, width, height, outputWidth, outputHeight);

        AVPixelFormat toBgraFrom = format;
        if (ThroughPlanar)
        {
            // Only unpacks the chroma: no colour conversion and no scaling, so the range and matrix stay the frame's.
            _toPlanar = ffmpeg.sws_getContext(
                width, height, format, width, height, AVPixelFormat.AV_PIX_FMT_YUV420P,
                (int)SwsFlags.SWS_BILINEAR, null, null, null);
            if (_toPlanar == null)
            {
                throw new InvalidOperationException("sws_getContext failed (NV12 to YUV420P).");
            }

            _planar = ffmpeg.av_frame_alloc();
            _planar->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;
            _planar->width = width;
            _planar->height = height;
            int allocated = ffmpeg.av_frame_get_buffer(_planar, 32);
            if (allocated < 0)
            {
                throw FFmpegErrors.ToException(allocated, "planar_frame_buffer");
            }

            toBgraFrom = AVPixelFormat.AV_PIX_FMT_YUV420P;
        }

        _toBgra = ffmpeg.sws_getContext(
            width, height, toBgraFrom, outputWidth, outputHeight, AVPixelFormat.AV_PIX_FMT_BGRA,
            (int)SwsFlags.SWS_BILINEAR, null, null, null);
        if (_toBgra == null)
        {
            throw new InvalidOperationException("sws_getContext failed.");
        }

        LibavColorMatrix.ReadAs(_toBgra, space, range);
    }

    public void Dispose() => Release();

    private void Release()
    {
        if (_toPlanar != null)
        {
            ffmpeg.sws_freeContext(_toPlanar);
            _toPlanar = null;
        }

        if (_toBgra != null)
        {
            ffmpeg.sws_freeContext(_toBgra);
            _toBgra = null;
        }

        if (_planar != null)
        {
            AVFrame* planar = _planar;
            ffmpeg.av_frame_free(&planar);
            _planar = null;
        }
    }
}
