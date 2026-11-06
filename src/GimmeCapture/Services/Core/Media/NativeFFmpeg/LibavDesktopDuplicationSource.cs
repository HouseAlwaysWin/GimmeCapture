using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using FFmpeg.AutoGen;

namespace GimmeCapture.Services.Core.Media.NativeFFmpeg;

/// <summary>
/// A display as DXGI lists it: the adapter it is attached to and its index there, which is how Desktop Duplication
/// names it, and its rectangle on the desktop in physical pixels. <paramref name="Rotation"/> is a DXGI_MODE_ROTATION.
/// </summary>
internal readonly record struct DuplicationOutput(
    int Adapter, int Output, int Left, int Top, int Right, int Bottom, bool AttachedToDesktop, int Rotation)
{
    /// <summary>
    /// The display that holds the whole region, in adapter order, or null when none does: a region across two displays,
    /// or on one that is turned. Desktop Duplication hands a turned display's picture over the way the panel is mounted,
    /// which FFmpeg does not turn back.
    /// </summary>
    public static DuplicationOutput? Holding(IEnumerable<DuplicationOutput> outputs, int x, int y, int width, int height)
    {
        foreach (var output in outputs)
        {
            bool upright = output.Rotation is 0 or 1; // DXGI_MODE_ROTATION_UNSPECIFIED, DXGI_MODE_ROTATION_IDENTITY
            if (output.AttachedToDesktop && upright
                && x >= output.Left && y >= output.Top
                && (long)x + width <= output.Right && (long)y + height <= output.Bottom)
            {
                return output;
            }
        }

        return null;
    }
}

/// <summary>
/// The Direct3D 11 device Desktop Duplication works through, made on the adapter the display hangs off (a laptop's
/// other graphics card cannot duplicate it), as FFmpeg holds devices. Making one took about 55 ms on an AMD laptop,
/// every time, so a recording makes it while its encoder opens, and keeps it to duplicate the display again on.
/// </summary>
internal sealed unsafe class DuplicationDevice : IDisposable
{
    private AVBufferRef* _device;

    private DuplicationDevice(DuplicationOutput output, AVBufferRef* device)
    {
        Output = output;
        _device = device;
    }

    /// <summary>The display it was made for.</summary>
    public DuplicationOutput Output { get; }

    internal AVBufferRef* Device => _device;

    /// <summary>
    /// Hands FFmpeg <paramref name="d3d11Device"/>, an ID3D11Device made with video support for <paramref name="output"/>,
    /// and takes over the reference passed in; null, with the device let go of, where FFmpeg does not take it.
    /// </summary>
    public static DuplicationDevice? Adopt(DuplicationOutput output, IntPtr d3d11Device)
    {
        AVBufferRef* device = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
        if (device == null)
        {
            Marshal.Release(d3d11Device);
            return null;
        }

        // From here FFmpeg lets go of the device with the context, whether that sets up or not.
        var context = (AVD3D11VADeviceContext*)((AVHWDeviceContext*)device->data)->hwctx;
        context->device = (ID3D11Device*)d3d11Device;
        int initialised = ffmpeg.av_hwdevice_ctx_init(device);
        if (initialised < 0)
        {
            AppLog.Information(
                "RecordingNative", $"Desktop Duplication: FFmpeg did not take the device: {FFmpegErrors.Describe(initialised)} ({initialised})");
            ffmpeg.av_buffer_unref(&device);
            return null;
        }

        return new DuplicationDevice(output, device);
    }

    public void Dispose()
    {
        if (_device != null)
        {
            AVBufferRef* device = _device;
            ffmpeg.av_buffer_unref(&device);
            _device = null;
        }
    }
}

/// <summary>
/// Grabs a region of one display through Desktop Duplication. FFmpeg's ddagrab filter copies the region out of the
/// desktop picture on the graphics card and draws the pointer on it there, and hwdownload reads only that region back,
/// as BGRA. It waits for each frame's time itself and hands the last picture over again when the screen has not changed.
/// </summary>
internal sealed unsafe class LibavDesktopDuplicationSource : ICapturedFrameSource
{
    private AVFilterGraph* _graph;
    private readonly AVFilterContext* _sink;

    private LibavDesktopDuplicationSource(AVFilterGraph* graph, AVFilterContext* sink)
    {
        _graph = graph;
        _sink = sink;
    }

    public string Name => "ddagrab";

    /// <summary>Whether this FFmpeg has the ddagrab filter (the Windows builds do).</summary>
    public static bool Available => ffmpeg.avfilter_get_by_name("ddagrab") != null;

    /// <summary>
    /// Duplicates the display <paramref name="device"/> was made for and grabs the region (physical pixels of the
    /// virtual screen, inside that display) from it, or returns null where that does not open, saying why in the log.
    /// It waits for the display's first picture, which Windows hands over at once. Not while NVENC opens: duplicating
    /// then failed every time (AVERROR_EXTERNAL), and right after it never did.
    /// </summary>
    public static LibavDesktopDuplicationSource? TryOpen(
        DuplicationDevice device, int x, int y, int width, int height, int fps, bool drawMouse)
    {
        DuplicationOutput output = device.Output;
        AVFilterGraph* graph = null;
        try
        {
            AVFilter* ddagrab = ffmpeg.avfilter_get_by_name("ddagrab");
            if (ddagrab == null)
            {
                AppLog.Information("RecordingNative", "Desktop Duplication: this FFmpeg has no ddagrab filter.");
                return null;
            }

            graph = ffmpeg.avfilter_graph_alloc();
            if (graph == null)
            {
                throw new OutOfMemoryException("avfilter_graph_alloc");
            }

            AVFilterContext* grab = ffmpeg.avfilter_graph_alloc_filter(graph, ddagrab, "grab");
            if (grab == null)
            {
                throw new OutOfMemoryException("avfilter_graph_alloc_filter(ddagrab)");
            }

            // The filter keeps a reference of its own to the device.
            grab->hw_device_ctx = ffmpeg.av_buffer_ref(device.Device);
            string args = string.Create(
                CultureInfo.InvariantCulture,
                $"output_idx={output.Output}:framerate={fps}:video_size={width}x{height}:offset_x={x - output.Left}:offset_y={y - output.Top}:draw_mouse={(drawMouse ? 1 : 0)}:output_fmt=bgra");
            ThrowIfErr(ffmpeg.avfilter_init_str(grab, args), "init(ddagrab)");

            AVFilterContext* download = CreateFilter(graph, "hwdownload", "download", null);
            AVFilterContext* format = CreateFilter(graph, "format", "format", "pix_fmts=bgra");
            AVFilterContext* sink = CreateFilter(graph, "buffersink", "out", null);
            ThrowIfErr(ffmpeg.avfilter_link(grab, 0, download, 0), "link(ddagrab)");
            ThrowIfErr(ffmpeg.avfilter_link(download, 0, format, 0), "link(hwdownload)");
            ThrowIfErr(ffmpeg.avfilter_link(format, 0, sink, 0), "link(format)");

            // Duplicates the display, and waits for its first picture to learn the format it comes in.
            ThrowIfErr(ffmpeg.avfilter_graph_config(graph, null), "graph_config(ddagrab)");

            var source = new LibavDesktopDuplicationSource(graph, sink);
            graph = null;
            return source;
        }
        catch (Exception ex) when (ex is FFmpegNativeException or InvalidOperationException)
        {
            // No such display any more, a device that cannot duplicate, too many duplications open, a desktop that is
            // not the user's (the secure desktop of a UAC prompt), or this process duplicating the display already
            // ("Invalid argument": Windows allows one at a time, so of several windows recorded to their own files at
            // once only the first is duplicated). The error text holds no path.
            AppLog.Information("RecordingNative", $"Desktop Duplication did not open: {ex.Message}");
            return null;
        }
        finally
        {
            if (graph != null)
            {
                ffmpeg.avfilter_graph_free(&graph);
            }
        }
    }

    public bool TryRead(AVFrame* frame, CancellationToken ct)
    {
        while (true)
        {
            ffmpeg.av_frame_unref(frame);
            int got = ffmpeg.av_buffersink_get_frame(_sink, frame);
            if (got >= 0)
            {
                return true;
            }

            if (got == ffmpeg.AVERROR_EOF)
            {
                return false;
            }

            // Access lost: the desktop changed under the duplication (a display mode change, the secure desktop).
            if (got != ffmpeg.AVERROR(11))
            {
                throw FFmpegErrors.ToException(got, "buffersink_get_frame(ddagrab)");
            }

            if (ct.IsCancellationRequested)
            {
                return false;
            }

            Thread.Sleep(1);
        }
    }

    public void Dispose()
    {
        if (_graph != null)
        {
            AVFilterGraph* graph = _graph;
            ffmpeg.avfilter_graph_free(&graph);
            _graph = null;
        }
    }

    private static AVFilterContext* CreateFilter(AVFilterGraph* graph, string filterName, string name, string? args)
    {
        AVFilter* filter = ffmpeg.avfilter_get_by_name(filterName);
        if (filter == null)
        {
            throw new InvalidOperationException($"FFmpeg has no {filterName} filter.");
        }

        AVFilterContext* context = null;
        ThrowIfErr(ffmpeg.avfilter_graph_create_filter(&context, filter, name, args, null, graph), $"create_filter({filterName})");
        return context;
    }

    private static void ThrowIfErr(int err, string ctx)
    {
        if (err < 0)
        {
            throw FFmpegErrors.ToException(err, ctx);
        }
    }
}
