using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using FFmpeg.AutoGen;
using SkiaSharp;

namespace GimmeCapture.Services.Core.Media.NativeFFmpeg;

/// <summary>
/// Owns a <see cref="WebcamCaptureSource"/> and draws its latest frame as a picture-in-picture overlay
/// into a corner of each recorded frame. Created once per recording session; <see cref="Draw"/> is called
/// per frame from the encode loop.
/// </summary>
internal sealed class WebcamPipCompositor : IDisposable
{
    // corner: 0 = top-left, 1 = top-right, 2 = bottom-left, 3 = bottom-right
    private readonly WebcamCaptureSource _source;
    private readonly int _corner;
    private readonly float _widthFraction;
    private readonly bool _circular;
    private byte[]? _buffer;

    public WebcamPipCompositor(string deviceName, int corner, float widthFraction = 0.25f, bool circular = false)
    {
        _source = new WebcamCaptureSource(deviceName);
        _corner = corner;
        _widthFraction = widthFraction > 0 ? widthFraction : 0.25f;
        _circular = circular;
    }

    public void Start() => _source.Start();

    public void Draw(SKBitmap target)
    {
        if (!_source.TryCopyLatest(ref _buffer, out int w, out int h) || _buffer == null || w <= 0 || h <= 0)
        {
            return;
        }

        var handle = GCHandle.Alloc(_buffer, GCHandleType.Pinned);
        try
        {
            var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            using var cam = new SKBitmap();
            if (!cam.InstallPixels(info, handle.AddrOfPinnedObject(), w * 4))
            {
                return;
            }

            float pipW = target.Width * _widthFraction;
            float pipH = pipW * h / w;
            float margin = target.Width * 0.02f;
            float x = (_corner == 1 || _corner == 3) ? target.Width - pipW - margin : margin;
            float y = (_corner >= 2) ? target.Height - pipH - margin : margin;
            var dest = new SKRect(x, y, x + pipW, y + pipH);

            using var canvas = new SKCanvas(target);
            using var border = new SKPaint
            {
                Color = new SKColor(255, 255, 255, 200),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 2,
                IsAntialias = true,
            };

            if (_circular)
            {
                // Clip to a centered circle (diameter = the shorter PiP side) so the webcam shows as a
                // round bubble, then stroke a matching ring. Save/restore so the clip doesn't leak.
                float diameter = Math.Min(pipW, pipH);
                float cx = x + pipW / 2f;
                float cy = y + pipH / 2f;
                float radius = diameter / 2f;

                int save = canvas.Save();
                using (var clip = new SKPath())
                {
                    clip.AddCircle(cx, cy, radius);
                    canvas.ClipPath(clip, SKClipOperation.Intersect, antialias: true);
                    // Draw the camera so its shorter side fills the circle (cover, centered).
                    float coverW = diameter * (w >= h ? (float)w / h : 1f);
                    float coverH = diameter * (h > w ? (float)h / w : 1f);
                    canvas.DrawBitmap(cam, new SKRect(cx - coverW / 2f, cy - coverH / 2f, cx + coverW / 2f, cy + coverH / 2f));
                }
                canvas.RestoreToCount(save);
                canvas.DrawCircle(cx, cy, radius, border);
            }
            else
            {
                canvas.DrawBitmap(cam, dest);
                canvas.DrawRect(dest, border);
            }
        }
        finally
        {
            handle.Free();
        }
    }

    public void Dispose() => _source.Dispose();
}

/// <summary>
/// Opens a webcam via libav <c>dshow</c> on a background thread and continuously decodes it to BGRA,
/// keeping only the latest frame (lock-protected). The recording encode loop polls
/// <see cref="TryCopyLatest"/> each desktop frame to composite a picture-in-picture overlay. All failures
/// are swallowed — a missing/busy webcam just means no PiP, never a broken recording.
/// </summary>
internal sealed class WebcamCaptureSource : IDisposable
{
    private readonly string _deviceName;
    private Thread? _thread;
    private volatile bool _running;

    private readonly object _lock = new();
    private byte[]? _latest;     // BGRA, tightly packed (stride == width*4)
    private byte[]? _spare;      // recycled back-buffer, swapped with _latest to avoid a per-frame alloc
    private int _width;
    private int _height;

    public WebcamCaptureSource(string deviceName)
    {
        _deviceName = deviceName;
    }

    public void Start()
    {
        _running = true;
        _thread = new Thread(DecodeLoop) { IsBackground = true, Name = "WebcamCapture" };
        _thread.Start();
    }

    /// <summary>Copies the latest webcam frame (BGRA, tightly packed) into a caller buffer. False if none yet.</summary>
    public bool TryCopyLatest(ref byte[]? buffer, out int width, out int height)
    {
        lock (_lock)
        {
            if (_latest == null)
            {
                width = 0;
                height = 0;
                return false;
            }

            if (buffer == null || buffer.Length < _latest.Length)
            {
                buffer = new byte[_latest.Length];
            }

            Array.Copy(_latest, buffer, _latest.Length);
            width = _width;
            height = _height;
            return true;
        }
    }

    private unsafe void DecodeLoop()
    {
        AVFormatContext* fmt = null;
        AVCodecContext* dec = null;
        SwsContext* sws = null;
        AVPacket* pkt = null;
        AVFrame* frame = null;
        AVFrame* bgra = null;
        AVDictionary* opts = null;
        int lastW = 0, lastH = 0;

        try
        {
            FFmpegRuntime.EnsureInitialized();

            AVInputFormat* ifmt = ffmpeg.av_find_input_format("dshow");
            if (ifmt == null)
            {
                Debug.WriteLine("[Webcam] dshow demuxer unavailable.");
                return;
            }

            ffmpeg.av_dict_set(&opts, "rtbufsize", "64M", 0);
            string url = $"video={_deviceName}";
            if (ffmpeg.avformat_open_input(&fmt, url, ifmt, &opts) < 0)
            {
                Debug.WriteLine($"[Webcam] Failed to open '{url}'.");
                return;
            }

            if (ffmpeg.avformat_find_stream_info(fmt, null) < 0)
            {
                return;
            }

            int videoStream = ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            if (videoStream < 0)
            {
                return;
            }

            AVStream* stream = fmt->streams[videoStream];
            AVCodec* codec = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
            if (codec == null)
            {
                return;
            }

            dec = ffmpeg.avcodec_alloc_context3(codec);
            if (ffmpeg.avcodec_parameters_to_context(dec, stream->codecpar) < 0
                || ffmpeg.avcodec_open2(dec, codec, null) < 0)
            {
                return;
            }

            pkt = ffmpeg.av_packet_alloc();
            frame = ffmpeg.av_frame_alloc();

            while (_running)
            {
                int rr = ffmpeg.av_read_frame(fmt, pkt);
                if (rr < 0)
                {
                    Thread.Sleep(5);
                    continue;
                }

                if (pkt->stream_index != videoStream)
                {
                    ffmpeg.av_packet_unref(pkt);
                    continue;
                }

                if (ffmpeg.avcodec_send_packet(dec, pkt) >= 0)
                {
                    ffmpeg.av_packet_unref(pkt);
                    while (ffmpeg.avcodec_receive_frame(dec, frame) >= 0)
                    {
                        int w = frame->width;
                        int h = frame->height;
                        if (w <= 0 || h <= 0)
                        {
                            continue;
                        }

                        if (sws == null || w != lastW || h != lastH)
                        {
                            if (sws != null) { ffmpeg.sws_freeContext(sws); sws = null; }
                            if (bgra != null) { var b = bgra; ffmpeg.av_frame_free(&b); bgra = null; }

                            sws = ffmpeg.sws_getContext(
                                w, h, (AVPixelFormat)frame->format,
                                w, h, AVPixelFormat.AV_PIX_FMT_BGRA,
                                (int)SwsFlags.SWS_FAST_BILINEAR, null, null, null);
                            if (sws == null)
                            {
                                break;
                            }

                            bgra = ffmpeg.av_frame_alloc();
                            bgra->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
                            bgra->width = w;
                            bgra->height = h;
                            if (ffmpeg.av_frame_get_buffer(bgra, 32) < 0)
                            {
                                break;
                            }
                            lastW = w;
                            lastH = h;
                        }

                        ffmpeg.sws_scale(sws, frame->data, frame->linesize, 0, h, bgra->data, bgra->linesize);

                        // Copy row by row into a tightly-packed managed buffer (sws stride may be padded).
                        // Reuse a recycled back-buffer instead of allocating every frame: take the spare,
                        // fill it, then swap it in and recycle the previously published buffer. Only
                        // (re)allocate when no spare is free or the frame size changed (e.g. resolution switch),
                        // which keeps the invariant that _latest.Length == _width*_height*4 for TryCopyLatest.
                        int stride = bgra->linesize[0];
                        int rowBytes = w * 4;
                        int needed = rowBytes * h;

                        byte[]? managed;
                        lock (_lock)
                        {
                            managed = _spare;
                            _spare = null;
                        }
                        if (managed == null || managed.Length != needed)
                        {
                            managed = new byte[needed];
                        }

                        byte* srcBase = bgra->data[0];
                        for (int row = 0; row < h; row++)
                        {
                            Marshal_Copy((IntPtr)(srcBase + row * stride), managed, row * rowBytes, rowBytes);
                        }

                        lock (_lock)
                        {
                            _spare = _latest;   // recycle the previously published buffer for the next frame
                            _latest = managed;
                            _width = w;
                            _height = h;
                        }
                    }
                }
                else
                {
                    ffmpeg.av_packet_unref(pkt);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Webcam] Decode loop error: {ex.Message}");
        }
        finally
        {
            if (sws != null) ffmpeg.sws_freeContext(sws);
            if (bgra != null) { var b = bgra; ffmpeg.av_frame_free(&b); }
            if (frame != null) { var f = frame; ffmpeg.av_frame_free(&f); }
            if (pkt != null) { var p = pkt; ffmpeg.av_packet_free(&p); }
            if (dec != null) { var d = dec; ffmpeg.avcodec_free_context(&d); }
            if (fmt != null) { var ff = fmt; ffmpeg.avformat_close_input(&ff); }
        }
    }

    private static void Marshal_Copy(IntPtr src, byte[] dst, int startIndex, int count)
    {
        Marshal.Copy(src, dst, startIndex, count);
    }

    public void Dispose()
    {
        _running = false;
        try { _thread?.Join(700); } catch { /* best effort */ }
        _thread = null;
    }
}
