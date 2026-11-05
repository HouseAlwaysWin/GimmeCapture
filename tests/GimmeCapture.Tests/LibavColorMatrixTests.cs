using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FFmpeg.AutoGen;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using SkiaSharp;

namespace GimmeCapture.Tests;

/// <summary>
/// Pictures are read with the matrix their stream says, and recordings written with the one their encoder says. Every
/// picture was read as limited-range BT.601: most HD video says BT.709, and read as BT.601 its reds came out darker
/// (255,0,0 as 231,0,1) and its greens yellower, in a pin, the editor, a GIF and a thumbnail. A picture that says nothing
/// is still read as BT.601, as the app's own recordings were written. Real libav on clips written here; needs only the
/// bundled FFmpeg.
/// </summary>
public sealed class LibavColorMatrixTests : IDisposable
{
    private const int Width = 192; // six bands of 32
    private const int Height = 32;

    // Bands as (R, G, B), each 32 pixels wide so their chroma is whole.
    private static readonly (byte R, byte G, byte B)[] Bands =
    [
        (255, 0, 0), (0, 255, 0), (0, 0, 255), (200, 150, 120), (0, 128, 160), (128, 128, 128),
    ];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));

    public LibavColorMatrixTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A decoder a failed test left open may hold a clip.
        }
    }

    public static IEnumerable<object[]> Tags =>
    [
        [AVColorSpace.AVCOL_SPC_BT709, true],          // a phone's, another recorder's, AMD's encoder's
        [AVColorSpace.AVCOL_SPC_BT470BG, false],       // the app's own recordings now
        [AVColorSpace.AVCOL_SPC_UNSPECIFIED, false],   // the app's own recordings before
    ];

    [Theory]
    [MemberData(nameof(Tags))]
    public async Task AStillFrame_ShowsTheColoursItsStreamHolds(AVColorSpace tag, bool bt709)
    {
        string clip = WritePatches(Path.Combine(_dir, "clip.mkv"), tag, bt709);

        byte[]? bgra = await LibavVideoFramePlayer.DecodeFrameAtAsync(clip, 0, Width, Height);

        Assert.NotNull(bgra);
        AssertBands(bgra, Width * 4);
    }

    [Theory]
    [MemberData(nameof(Tags))]
    public async Task PlayingIt_ShowsTheColoursItsStreamHolds(AVColorSpace tag, bool bt709)
    {
        string clip = WritePatches(Path.Combine(_dir, "clip.mkv"), tag, bt709);
        using var player = new LibavVideoFramePlayer();
        using var stop = new CancellationTokenSource();
        byte[]? first = null;

        try
        {
            await player.PlayFramesAsync(clip, Width, Height, 0, 1.0, false, (bgra, _) =>
            {
                first ??= bgra.ToArray();
                stop.Cancel();
            }, stop.Token);
        }
        catch (OperationCanceledException)
        {
        }

        Assert.NotNull(first);
        AssertBands(first, Width * 4);
    }

    [Theory]
    [MemberData(nameof(Tags))]
    public void AGifOfIt_HasTheColoursItsStreamHolds(AVColorSpace tag, bool bt709)
    {
        string clip = WritePatches(Path.Combine(_dir, "clip.mkv"), tag, bt709);
        string gif = Path.Combine(_dir, "clip.gif");

        LibavGifTranscoder.TranscodeToGif(clip, gif, 10, maxWidth: 0, paletteuseArgs: "dither=none");

        using SKCodec? codec = SKCodec.Create(gif);
        Assert.NotNull(codec);
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        Assert.Equal(SKCodecResult.Success, codec.GetPixels(info, bitmap.GetPixels()));
        AssertBands(bitmap.GetPixelSpan().ToArray(), bitmap.RowBytes);
    }

    [Fact]
    public void AnEncodersBt709_IsWhatTheConverterWrites()
    {
        // The recording converters write what the encoder declares: AMD's encoder declares BT.709 whatever it is told.
        FFmpegRuntime.EnsureInitialized();
        (int y, int u, int v) bt601 = ConvertRed(AVColorSpace.AVCOL_SPC_BT470BG);
        (int y, int u, int v) bt709 = ConvertRed(AVColorSpace.AVCOL_SPC_BT709);

        Assert.Equal(Yuv(255, 0, 0, bt709: false), bt601, new Near(1));
        Assert.Equal(Yuv(255, 0, 0, bt709: true), bt709, new Near(1));
    }

    // One red frame turned into YUV 4:2:0 by a scaler set to write the given matrix; the Y, U and V it wrote.
    private static unsafe (int Y, int U, int V) ConvertRed(AVColorSpace space)
    {
        SwsContext* sws = ffmpeg.sws_getContext(
            16, 16, AVPixelFormat.AV_PIX_FMT_BGRA, 16, 16, AVPixelFormat.AV_PIX_FMT_YUV420P,
            (int)SwsFlags.SWS_FAST_BILINEAR, null, null, null);
        AVFrame* bgra = ffmpeg.av_frame_alloc();
        AVFrame* yuv = ffmpeg.av_frame_alloc();
        try
        {
            LibavColorMatrix.WriteAs(sws, space, AVColorRange.AVCOL_RANGE_MPEG);
            bgra->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
            bgra->width = 16;
            bgra->height = 16;
            yuv->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;
            yuv->width = 16;
            yuv->height = 16;
            Assert.True(ffmpeg.av_frame_get_buffer(bgra, 32) >= 0);
            Assert.True(ffmpeg.av_frame_get_buffer(yuv, 32) >= 0);
            for (int y = 0; y < 16; y++)
            {
                byte* row = bgra->data[0] + (y * bgra->linesize[0]);
                for (int x = 0; x < 16; x++)
                {
                    row[(x * 4) + 0] = 0;
                    row[(x * 4) + 1] = 0;
                    row[(x * 4) + 2] = 255;
                    row[(x * 4) + 3] = 255;
                }
            }

            ffmpeg.sws_scale(sws, bgra->data, bgra->linesize, 0, 16, yuv->data, yuv->linesize);
            return (yuv->data[0][0], yuv->data[1][0], yuv->data[2][0]);
        }
        finally
        {
            ffmpeg.av_frame_free(&bgra);
            ffmpeg.av_frame_free(&yuv);
            ffmpeg.sws_freeContext(sws);
        }
    }

    // Limited-range YUV for an RGB colour by BT.709 or BT.601.
    private static (int Y, int U, int V) Yuv(byte r, byte g, byte b, bool bt709)
    {
        double kr = bt709 ? 0.2126 : 0.299;
        double kb = bt709 ? 0.0722 : 0.114;
        double kg = 1 - kr - kb;
        double luma = ((kr * r) + (kg * g) + (kb * b)) / 255.0;
        double cb = ((b / 255.0) - luma) / (2 * (1 - kb));
        double cr = ((r / 255.0) - luma) / (2 * (1 - kr));
        return ((int)Math.Round(16 + (219 * luma)), (int)Math.Round(128 + (224 * cb)), (int)Math.Round(128 + (224 * cr)));
    }

    // Each band's middle, as BGRA rows of the given stride, within four levels of the colour written.
    private static void AssertBands(byte[] bgra, int stride)
    {
        for (int band = 0; band < Bands.Length; band++)
        {
            int offset = ((Height / 2) * stride) + (((band * 32) + 16) * 4);
            var (r, g, b) = Bands[band];
            string shown = $"{bgra[offset + 2]},{bgra[offset + 1]},{bgra[offset]}";
            Assert.True(
                Math.Abs(bgra[offset + 2] - r) <= 4 && Math.Abs(bgra[offset + 1] - g) <= 4 && Math.Abs(bgra[offset] - b) <= 4,
                $"band {band}: {r},{g},{b} shown as {shown}");
        }
    }

    // Ten frames of the bands, as YUV by the given matrix, nearly lossless H.264 saying the given colour space.
    private static unsafe string WritePatches(string path, AVColorSpace tag, bool bt709)
    {
        FFmpegRuntime.EnsureInitialized();
        AVFormatContext* output = null;
        AVCodecContext* encoder = null;
        AVFrame* frame = null;
        AVPacket* packet = null;
        AVDictionary* options = null;
        try
        {
            Assert.True(ffmpeg.avformat_alloc_output_context2(&output, null, null, path) >= 0);
            AVCodec* codec = ffmpeg.avcodec_find_encoder_by_name("libx264");
            Assert.True(codec != null, "libx264 is not in the bundled FFmpeg");
            encoder = ffmpeg.avcodec_alloc_context3(codec);
            encoder->width = Width;
            encoder->height = Height;
            encoder->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
            encoder->time_base = new AVRational { num = 1, den = 10 };
            encoder->framerate = new AVRational { num = 10, den = 1 };
            encoder->max_b_frames = 0;
            encoder->colorspace = tag;
            encoder->color_range = tag == AVColorSpace.AVCOL_SPC_UNSPECIFIED
                ? AVColorRange.AVCOL_RANGE_UNSPECIFIED
                : AVColorRange.AVCOL_RANGE_MPEG;
            if ((output->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
            {
                encoder->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
            }

            ffmpeg.av_dict_set(&options, "preset", "ultrafast", 0);
            ffmpeg.av_dict_set(&options, "qp", "1", 0);
            Assert.True(ffmpeg.avcodec_open2(encoder, codec, &options) >= 0);
            AVStream* stream = ffmpeg.avformat_new_stream(output, null);
            Assert.True(ffmpeg.avcodec_parameters_from_context(stream->codecpar, encoder) >= 0);
            stream->time_base = encoder->time_base;
            Assert.True(ffmpeg.avio_open(&output->pb, path, ffmpeg.AVIO_FLAG_WRITE) >= 0);
            Assert.True(ffmpeg.avformat_write_header(output, null) >= 0);

            frame = ffmpeg.av_frame_alloc();
            packet = ffmpeg.av_packet_alloc();
            frame->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;
            frame->width = Width;
            frame->height = Height;
            Assert.True(ffmpeg.av_frame_get_buffer(frame, 32) >= 0);
            for (int i = 0; i < 10; i++)
            {
                Assert.True(ffmpeg.av_frame_make_writable(frame) >= 0);
                for (int band = 0; band < Bands.Length; band++)
                {
                    var (r, g, b) = Bands[band];
                    var (y, u, v) = Yuv(r, g, b, bt709);
                    for (int row = 0; row < Height; row++)
                    {
                        new Span<byte>(frame->data[0] + (row * frame->linesize[0]) + (band * 32), 32).Fill((byte)y);
                    }

                    for (int row = 0; row < Height / 2; row++)
                    {
                        new Span<byte>(frame->data[1] + (row * frame->linesize[1]) + (band * 16), 16).Fill((byte)u);
                        new Span<byte>(frame->data[2] + (row * frame->linesize[2]) + (band * 16), 16).Fill((byte)v);
                    }
                }

                frame->pts = i;
                frame->duration = 1;
                Assert.True(ffmpeg.avcodec_send_frame(encoder, frame) >= 0);
                Drain(encoder, output, stream, packet);
            }

            Assert.True(ffmpeg.avcodec_send_frame(encoder, null) >= 0);
            Drain(encoder, output, stream, packet);
            Assert.True(ffmpeg.av_write_trailer(output) >= 0);
            return path;
        }
        finally
        {
            if (options != null) ffmpeg.av_dict_free(&options);
            if (packet != null) ffmpeg.av_packet_free(&packet);
            if (frame != null) ffmpeg.av_frame_free(&frame);
            if (encoder != null) ffmpeg.avcodec_free_context(&encoder);
            if (output != null)
            {
                if (output->pb != null) ffmpeg.avio_closep(&output->pb);
                ffmpeg.avformat_free_context(output);
            }
        }
    }

    private static unsafe void Drain(AVCodecContext* encoder, AVFormatContext* output, AVStream* stream, AVPacket* packet)
    {
        while (ffmpeg.avcodec_receive_packet(encoder, packet) >= 0)
        {
            ffmpeg.av_packet_rescale_ts(packet, encoder->time_base, stream->time_base);
            packet->stream_index = stream->index;
            Assert.True(ffmpeg.av_interleaved_write_frame(output, packet) >= 0);
        }
    }

    private sealed class Near(int tolerance) : IEqualityComparer<(int, int, int)>
    {
        public bool Equals((int, int, int) a, (int, int, int) b) =>
            Math.Abs(a.Item1 - b.Item1) <= tolerance && Math.Abs(a.Item2 - b.Item2) <= tolerance
            && Math.Abs(a.Item3 - b.Item3) <= tolerance;

        public int GetHashCode((int, int, int) value) => 0;
    }
}
