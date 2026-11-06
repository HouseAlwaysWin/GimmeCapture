using FFmpeg.AutoGen;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;

namespace GimmeCapture.Tests;

/// <summary>
/// The player's conversion of decoded frames to BGRA. A frame downloaded from the graphics card is NV12, which goes
/// through YUV420P to reach swscale's fast route when it is shown at its own size; the picture must come out as the
/// direct route gives it, its colour tags honoured. Uses the bundled FFmpeg's swscale only; runs everywhere.
/// </summary>
public sealed unsafe class LibavBgraConverterTests
{
    [Theory]
    [InlineData(AVPixelFormat.AV_PIX_FMT_NV12, 1450, 900, 1450, 900, true)]  // a pin of a recording, pixel for pixel
    [InlineData(AVPixelFormat.AV_PIX_FMT_NV12, 1450, 900, 720, 446, false)]  // the editor's smaller frames: scaled
    [InlineData(AVPixelFormat.AV_PIX_FMT_YUV420P, 1450, 900, 1450, 900, false)] // decoded on the CPU: already fast
    [InlineData(AVPixelFormat.AV_PIX_FMT_NV12, 1451, 900, 1451, 900, false)] // an odd width has no 2x2 chroma blocks
    public void OnlyNv12ShownAtItsOwnSize_GoesThroughPlanar(AVPixelFormat format, int width, int height, int outputWidth, int outputHeight, bool expected)
    {
        Assert.Equal(expected, LibavBgraConverter.GoesThroughPlanar(format, width, height, outputWidth, outputHeight));
    }

    // Limited-range BT.709 red (Y 63, Cb 102, Cr 240): read as BT.709 it is red; read as BT.601, as an unsaid matrix
    // is, it is not. The matrix has to reach the second step.
    [Theory]
    [InlineData(AVColorSpace.AVCOL_SPC_BT709, 255, 0, 0)]
    [InlineData(AVColorSpace.AVCOL_SPC_BT470BG, 232, 0, 1)]
    public void AFlatNv12Frame_ComesOutInTheColourItsTagsSay(AVColorSpace space, int r, int g, int b)
    {
        FFmpegRuntime.EnsureInitialized();
        AVFrame* nv12 = Nv12(64, 32, (x, y) => (63, 102, 240));
        AVFrame* bgra = Bgra(64, 32);
        using var converter = new LibavBgraConverter();
        try
        {
            converter.Convert(nv12, space, AVColorRange.AVCOL_RANGE_MPEG, bgra);

            Assert.True(converter.ThroughPlanar);
            byte* centre = bgra->data[0] + (16 * bgra->linesize[0]) + (32 * 4);
            Assert.InRange(centre[2], r - 3, r + 3);
            Assert.InRange(centre[1], g - 3, g + 3);
            Assert.InRange(centre[0], b - 3, b + 3);
        }
        finally
        {
            ffmpeg.av_frame_free(&nv12);
            ffmpeg.av_frame_free(&bgra);
        }
    }

    // Stripes of colour and a fine pattern, through both routes: they part only where chroma changes between
    // neighbouring 2x2 blocks, which the fast route repeats rather than blends.
    [Fact]
    public void ThroughPlanar_GivesThePictureTheDirectRouteGives()
    {
        FFmpegRuntime.EnsureInitialized();
        const int width = 320, height = 180;
        AVFrame* nv12 = Nv12(width, height, (x, y) =>
        {
            byte luma = (byte)(16 + ((x * 7 + y * 3) % 220));
            return ((x / 40) % 4) switch
            {
                0 => (luma, (byte)90, (byte)240),
                1 => (luma, (byte)54, (byte)34),
                2 => (luma, (byte)240, (byte)110),
                _ => (luma, (byte)128, (byte)128),
            };
        });
        AVFrame* viaPlanar = Bgra(width, height);
        AVFrame* direct = Bgra(width, height);
        using var converter = new LibavBgraConverter();
        SwsContext* reference = ffmpeg.sws_getContext(
            width, height, AVPixelFormat.AV_PIX_FMT_NV12, width, height, AVPixelFormat.AV_PIX_FMT_BGRA,
            (int)SwsFlags.SWS_BILINEAR, null, null, null);
        try
        {
            converter.Convert(nv12, AVColorSpace.AVCOL_SPC_BT470BG, AVColorRange.AVCOL_RANGE_MPEG, viaPlanar);
            LibavColorMatrix.ReadAs(reference, AVColorSpace.AVCOL_SPC_BT470BG, AVColorRange.AVCOL_RANGE_MPEG);
            ffmpeg.sws_scale(reference, nv12->data, nv12->linesize, 0, height, direct->data, direct->linesize);

            Assert.True(converter.ThroughPlanar);
            double squares = 0;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width * 4; x++)
                {
                    if (x % 4 == 3)
                    {
                        continue;
                    }

                    int e = viaPlanar->data[0][(y * viaPlanar->linesize[0]) + x] - direct->data[0][(y * direct->linesize[0]) + x];
                    squares += e * e;
                }
            }

            double psnr = 10 * Math.Log10(255.0 * 255.0 / Math.Max(squares / (width * height * 3), 1e-9));
            Assert.True(psnr > 40, $"PSNR {psnr:0.0} dB against the direct route");
        }
        finally
        {
            ffmpeg.sws_freeContext(reference);
            ffmpeg.av_frame_free(&nv12);
            ffmpeg.av_frame_free(&viaPlanar);
            ffmpeg.av_frame_free(&direct);
        }
    }

    [Fact]
    public void AFrameOfAnotherSize_SetsTheConverterUpAgain()
    {
        FFmpegRuntime.EnsureInitialized();
        AVFrame* small = Nv12(64, 32, (x, y) => (63, 102, 240));
        AVFrame* large = Nv12(128, 64, (x, y) => (63, 102, 240));
        AVFrame* smallOut = Bgra(64, 32);
        AVFrame* scaledOut = Bgra(64, 32);
        using var converter = new LibavBgraConverter();
        try
        {
            converter.Convert(small, AVColorSpace.AVCOL_SPC_BT709, AVColorRange.AVCOL_RANGE_MPEG, smallOut);
            Assert.True(converter.ThroughPlanar);

            converter.Convert(large, AVColorSpace.AVCOL_SPC_BT709, AVColorRange.AVCOL_RANGE_MPEG, scaledOut);
            Assert.False(converter.ThroughPlanar);
            byte* centre = scaledOut->data[0] + (16 * scaledOut->linesize[0]) + (32 * 4);
            Assert.InRange(centre[2], 252, 255);
        }
        finally
        {
            ffmpeg.av_frame_free(&small);
            ffmpeg.av_frame_free(&large);
            ffmpeg.av_frame_free(&smallOut);
            ffmpeg.av_frame_free(&scaledOut);
        }
    }

    // An NV12 frame whose every 2x2 block takes the chroma of its top-left pixel from `pixel`.
    private static AVFrame* Nv12(int width, int height, Func<int, int, (byte Y, byte Cb, byte Cr)> pixel)
    {
        AVFrame* frame = ffmpeg.av_frame_alloc();
        frame->format = (int)AVPixelFormat.AV_PIX_FMT_NV12;
        frame->width = width;
        frame->height = height;
        Assert.True(ffmpeg.av_frame_get_buffer(frame, 32) >= 0);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var (luma, cb, cr) = pixel(x, y);
                frame->data[0][(y * frame->linesize[0]) + x] = luma;
                if (x % 2 == 0 && y % 2 == 0)
                {
                    byte* chroma = frame->data[1] + ((y / 2) * frame->linesize[1]) + x;
                    chroma[0] = cb;
                    chroma[1] = cr;
                }
            }
        }

        return frame;
    }

    private static AVFrame* Bgra(int width, int height)
    {
        AVFrame* frame = ffmpeg.av_frame_alloc();
        frame->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
        frame->width = width;
        frame->height = height;
        Assert.True(ffmpeg.av_frame_get_buffer(frame, 1) >= 0);
        return frame;
    }
}
