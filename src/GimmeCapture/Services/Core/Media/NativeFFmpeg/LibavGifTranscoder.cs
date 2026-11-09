using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using FFmpeg.AutoGen;

using GimmeCapture.Models;

namespace GimmeCapture.Services.Core.Media.NativeFFmpeg;

internal static class LibavGifTranscoder
{
    /// <summary>
    /// The part of <see cref="TranscodeToGif"/>'s progress the palette pass covers; the encode pass covers the rest.
    /// Both decode and scale the whole clip, and the encode also dithers and compresses it, on several cores since the
    /// dithering is spread over them (PaletteMapper): on screen recordings at Medium each pass takes about half the time.
    /// </summary>
    internal const double PalettePassShare = 0.5;

    /// <summary>
    /// Quality → (fps, max output width; 0 = source width) ladder, the single home for the numbers that
    /// were previously duplicated by the recording finalize and the pinned-video GIF export.
    /// </summary>
    public static (int Fps, int MaxWidth) QualityLadder(VideoQuality quality) => quality switch
    {
        VideoQuality.High => (24, 0),
        VideoQuality.Low => (10, 480),
        _ => (15, 720),
    };

    /// <summary>Error diffusion, the finest dithering paletteuse has, and the slowest.</summary>
    internal const string FinestDither = "dither=sierra2_4a";

    /// <summary>
    /// The dithering a GIF of this quality gets, as paletteuse's arguments: a GIF recording, a pinned recording's GIF
    /// copy and a GIF from the Compress tab (its quality follows the CRF slider) all take it. Error diffusion looks
    /// best, but its fine noise compresses poorly and makes the largest files; ordered (bayer) dithering compresses far
    /// better and leaves a regular grain on gradients; none is the smallest and bands gradients. The copy and the
    /// Compress tab's GIF were dithered with the finest at every quality: at Medium a 34-second 1450x900 recording's GIF
    /// came out 6.5 MB in 8.4 s, where bayer makes it 3.5 MB in 5.6 s.
    /// </summary>
    public static string QualityDither(VideoQuality quality) => quality switch
    {
        VideoQuality.High => FinestDither,
        VideoQuality.Low => "dither=none",
        _ => "dither=bayer:bayer_scale=3",
    };

    /// <summary>
    /// Transcodes to GIF. Tries the optimal-palette path (palettegen → paletteuse, two passes over the clip) for
    /// much better color, and falls back to the single-pass RGB8 path if the filters are unavailable or fail — so
    /// GIF export never regresses. <paramref name="progress"/> gets 0..1 across both passes. Cancelling throws
    /// <see cref="OperationCanceledException"/>, and an export that does not finish deletes the GIF it started. While
    /// <paramref name="pauseGate"/> is reset, the transcode waits before its next packet. <paramref name="mapThreads"/>
    /// is how many threads map frames to the palette, half the processor's by default, and
    /// <paramref name="decodeAhead"/> whether each pass decodes on a thread of its own a few frames ahead
    /// (<see cref="FrameFeed"/>); the GIF is the same either way.
    /// </summary>
    public static void TranscodeToGif(
        string inputPath,
        string outputPath,
        int targetFps,
        int maxWidth,
        string paletteuseArgs = FinestDither,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default,
        ManualResetEventSlim? pauseGate = null,
        int mapThreads = 0,
        bool decodeAhead = true)
    {
        FFmpegRuntime.EnsureInitialized();
        cancellationToken.ThrowIfCancellationRequested();
        var reporter = new GifProgress(progress);
        // With a diff_mode, paletteuse maps each frame from the one before it as well: one thread, then.
        int threads = paletteuseArgs.Contains("diff_mode", StringComparison.OrdinalIgnoreCase)
            ? 1
            : mapThreads > 0 ? mapThreads : Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
        try
        {
            TranscodeToGifWithPalette(
                inputPath, outputPath, targetFps, maxWidth, paletteuseArgs, reporter, pauseGate, cancellationToken,
                threads, decodeAhead);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Warning("Gif.PaletteTranscode", ex); // falls back to single-pass RGB8
            TranscodeToGifRgb8(inputPath, outputPath, targetFps, maxWidth, reporter, pauseGate, cancellationToken);
        }
    }

    // One pass that scales every frame straight to the GIF encoder's fixed 3-3-2 RGB8 palette.
    private static unsafe void TranscodeToGifRgb8(
        string inputPath,
        string outputPath,
        int targetFps,
        int maxWidth,
        GifProgress progress,
        ManualResetEventSlim? pauseGate,
        CancellationToken cancellationToken)
    {
        using var source = new GifFrameSource(inputPath, targetFps, maxWidth, AVPixelFormat.AV_PIX_FMT_RGB8, evenSize: false);
        using var writer = new GifWriter(outputPath, source.Width, source.Height, source.Fps, AVPixelFormat.AV_PIX_FMT_RGB8);
        AVFrame* frame = ffmpeg.av_frame_alloc();
        if (frame == null)
        {
            throw new OutOfMemoryException("gif_alloc_frame");
        }

        try
        {
            while (source.NextFrame(frame, pauseGate, cancellationToken))
            {
                writer.Write(frame);
                progress.Report(0, 1, source.Progress);
            }

            writer.Finish();
            progress.Report(0, 1, 1);
        }
        finally
        {
            ffmpeg.av_frame_free(&frame);
        }
    }

    // The optimal palette in two passes over the clip (ffmpeg's `palettegen` then `paletteuse` GIF recipe), giving
    // far better color than the fixed RGB8 3-3-2 palette. palettegen only emits its palette once it has seen the
    // whole clip, and paletteuse cannot map a frame without it, so one graph feeding both (split) held every frame of
    // the clip in memory until the end: over 1 GB a minute at Medium, about 12 GB a minute of 1080p at High. Here the
    // first pass feeds palettegen alone, which keeps a color histogram and frees each frame as it counts it; the
    // second decodes the clip again and maps each frame to PAL8 for the encoder as soon as it arrives, on
    // `mapThreads` threads (PaletteMapper).
    private static unsafe void TranscodeToGifWithPalette(
        string inputPath,
        string outputPath,
        int targetFps,
        int maxWidth,
        string paletteuseArgs,
        GifProgress progress,
        ManualResetEventSlim? pauseGate,
        CancellationToken cancellationToken,
        int mapThreads,
        bool decodeAhead)
    {
        AVFilter* bufferF = ffmpeg.avfilter_get_by_name("buffer");
        AVFilter* palettegenF = ffmpeg.avfilter_get_by_name("palettegen");
        AVFilter* paletteuseF = ffmpeg.avfilter_get_by_name("paletteuse");
        AVFilter* sinkF = ffmpeg.avfilter_get_by_name("buffersink");
        if (bufferF == null || palettegenF == null || paletteuseF == null || sinkF == null)
        {
            throw new InvalidOperationException("palettegen/paletteuse filters unavailable in this libav build.");
        }

        AVFilterGraph* paletteGraph = null;
        PaletteMapper? mapper = null;
        AVFrame* frame = null;
        AVFrame* pulled = null; // what palettegen's sink gives: the palette after the first pass
        try
        {
            frame = ffmpeg.av_frame_alloc();
            pulled = ffmpeg.av_frame_alloc();
            if (frame == null || pulled == null)
            {
                throw new OutOfMemoryException("gifp_alloc_frame");
            }

            int width;
            int height;
            int fps;
            using (var firstPass = new GifFrameSource(
                inputPath, targetFps, maxWidth, AVPixelFormat.AV_PIX_FMT_RGB24, evenSize: true))
            {
                width = firstPass.Width;
                height = firstPass.Height;
                fps = firstPass.Fps;
                string frameArgs =
                    $"video_size={width}x{height}:pix_fmt={(int)AVPixelFormat.AV_PIX_FMT_RGB24}:time_base=1/{fps}:pixel_aspect=1/1";

                // Pass 1 graph: buffer → palettegen → buffersink.
                paletteGraph = AllocGraph();
                AVFilterContext* paletteFrameSrc = CreateFilter(paletteGraph, bufferF, "in", frameArgs, "gifp_src");
                AVFilterContext* pgCtx = CreateFilter(paletteGraph, palettegenF, "pg", "stats_mode=full", "gifp_pg");
                AVFilterContext* paletteSink = CreateFilter(paletteGraph, sinkF, "out", null, "gifp_sink");
                ThrowIfErr(ffmpeg.avfilter_link(paletteFrameSrc, 0, pgCtx, 0), "gifp_link_src_pg");
                ThrowIfErr(ffmpeg.avfilter_link(pgCtx, 0, paletteSink, 0), "gifp_link_pg_sink");
                ThrowIfErr(ffmpeg.avfilter_graph_config(paletteGraph, null), "gifp_graph_config");

                // Pass 2 graphs, one a mapping thread: frames on paletteuse's first input, the palette on its second.
                // Built before the first pass so that a filter that cannot be set up (bad paletteuse args) falls back
                // to RGB8 before the clip has been decoded at all. The palette input is described exactly as
                // palettegen's output (size, format, color properties), so its frame goes in as it is, without a
                // libav warning.
                string paletteArgs =
                    $"video_size={ffmpeg.av_buffersink_get_w(paletteSink)}x{ffmpeg.av_buffersink_get_h(paletteSink)}" +
                    $":pix_fmt={ffmpeg.av_buffersink_get_format(paletteSink)}:time_base=1/{fps}:pixel_aspect=1/1" +
                    $":colorspace={(int)ffmpeg.av_buffersink_get_colorspace(paletteSink)}" +
                    $":range={(int)ffmpeg.av_buffersink_get_color_range(paletteSink)}";
                mapper = new PaletteMapper(mapThreads, frameArgs, paletteArgs, paletteuseArgs);

                using var firstFrames = new FrameFeed(firstPass, decodeAhead, pauseGate, cancellationToken);
                while (firstFrames.NextFrame(frame, cancellationToken))
                {
                    ThrowIfErr(ffmpeg.av_buffersrc_add_frame(paletteFrameSrc, frame), "gifp_src_push"); // consumes frame
                    CountColors(paletteSink, frame);
                    progress.Report(0, PalettePassShare, firstFrames.Progress);
                }

                ThrowIfErr(ffmpeg.av_buffersrc_add_frame(paletteFrameSrc, null), "gifp_src_eof"); // palettegen emits palette at EOF
                int got = ffmpeg.av_buffersink_get_frame(paletteSink, pulled);
                if (got == ffmpeg.AVERROR_EOF || got == ffmpeg.AVERROR(11))
                {
                    throw new InvalidOperationException("palettegen produced no palette: the clip has no frames.");
                }

                ThrowIfErr(got, "gifp_palette_pull");
            }

            ffmpeg.avfilter_graph_free(&paletteGraph); // drops the histogram before the second decode

            pulled->pts = 0; // the palette applies from the first frame on
            mapper.SetPalette(pulled);

            using var secondPass = new GifFrameSource(
                inputPath, targetFps, maxWidth, AVPixelFormat.AV_PIX_FMT_RGB24, evenSize: true);
            using var secondFrames = new FrameFeed(secondPass, decodeAhead, pauseGate, cancellationToken);
            using var writer = new GifWriter(outputPath, width, height, fps, AVPixelFormat.AV_PIX_FMT_PAL8);
            mapper.Start();
            while (secondFrames.NextFrame(frame, cancellationToken))
            {
                mapper.Submit(frame, writer, cancellationToken); // takes the frame's picture
                mapper.WriteMapped(writer, waitForAll: false, cancellationToken);
                progress.Report(PalettePassShare, 1 - PalettePassShare, secondFrames.Progress);
            }

            mapper.WriteMapped(writer, waitForAll: true, cancellationToken);
            writer.Finish();
            progress.Report(0, 1, 1);
        }
        finally
        {
            mapper?.Dispose();
            if (frame != null) ffmpeg.av_frame_free(&frame);
            if (pulled != null) ffmpeg.av_frame_free(&pulled);
            if (paletteGraph != null) ffmpeg.avfilter_graph_free(&paletteGraph);
        }
    }

    // Maps the second pass's frames to the palette on a few threads, each with a paletteuse graph of its own, and hands
    // them to the writer in order. Without diff_mode, paletteuse maps a frame from that frame alone, so this writes the
    // same GIF, byte for byte, as one graph mapping every frame in turn: that dithering ran on one core and took most of
    // an export, 10 of the 18 s a 34 s 1450x900 recording took at Medium. One thread hands the frames in and writes them
    // out, so a few at most are on their way (two a mapping thread) and memory stays as it was: when that many are, it
    // waits for the oldest and writes it before it hands in the next.
    private sealed unsafe class PaletteMapper : IDisposable
    {
        private readonly AVFilterGraph*[] _graphs;
        private readonly AVFilterContext*[] _frameSources;
        private readonly AVFilterContext*[] _paletteSources;
        private readonly AVFilterContext*[] _sinks;
        private readonly Thread[] _threads;
        private readonly BlockingCollection<(long Index, IntPtr Frame)> _queue = new();
        private readonly Dictionary<long, IntPtr> _mapped = new(); // under _gate
        private readonly object _gate = new();
        private readonly int _capacity;
        private Exception? _failure; // under _gate
        private volatile bool _stopping;
        private long _submitted;
        private long _written;
        private bool _started;

        public PaletteMapper(int threads, string frameArgs, string paletteArgs, string paletteuseArgs)
        {
            AVFilter* bufferF = ffmpeg.avfilter_get_by_name("buffer");
            AVFilter* paletteuseF = ffmpeg.avfilter_get_by_name("paletteuse");
            AVFilter* sinkF = ffmpeg.avfilter_get_by_name("buffersink");
            int count = Math.Max(1, threads);
            _graphs = new AVFilterGraph*[count];
            _frameSources = new AVFilterContext*[count];
            _paletteSources = new AVFilterContext*[count];
            _sinks = new AVFilterContext*[count];
            _threads = new Thread[count];
            _capacity = count * 2;
            try
            {
                for (int i = 0; i < count; i++)
                {
                    AVFilterGraph* graph = AllocGraph();
                    _graphs[i] = graph;
                    _frameSources[i] = CreateFilter(graph, bufferF, "in", frameArgs, "gifp_src2");
                    _paletteSources[i] = CreateFilter(graph, bufferF, "palette", paletteArgs, "gifp_palette_src");
                    AVFilterContext* pu = CreateFilter(graph, paletteuseF, "pu", paletteuseArgs, "gifp_pu");
                    _sinks[i] = CreateFilter(graph, sinkF, "out", null, "gifp_sink2");
                    ThrowIfErr(ffmpeg.avfilter_link(_frameSources[i], 0, pu, 0), "gifp_link_src_pu");     // frames -> in0
                    ThrowIfErr(ffmpeg.avfilter_link(_paletteSources[i], 0, pu, 1), "gifp_link_palette_pu"); // palette -> in1
                    ThrowIfErr(ffmpeg.avfilter_link(pu, 0, _sinks[i], 0), "gifp_link_pu_sink");
                    ThrowIfErr(ffmpeg.avfilter_graph_config(graph, null), "gifp_graph2_config");
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        // paletteuse waits for every input to have its next frame or to have ended, so each graph's palette input is
        // closed right after its one frame: from then on it holds the palette for the whole clip and paletteuse maps
        // each frame the moment it arrives.
        public void SetPalette(AVFrame* palette)
        {
            for (int i = 0; i < _graphs.Length; i++)
            {
                AVFrame* copy = ffmpeg.av_frame_clone(palette);
                if (copy == null)
                {
                    throw new OutOfMemoryException("gifp_palette_clone");
                }

                int pushed = ffmpeg.av_buffersrc_add_frame(_paletteSources[i], copy); // takes the copy's palette
                ffmpeg.av_frame_free(&copy);
                ThrowIfErr(pushed, "gifp_palette_push");
                ThrowIfErr(ffmpeg.av_buffersrc_add_frame(_paletteSources[i], null), "gifp_palette_eof");
            }
        }

        public void Start()
        {
            for (int i = 0; i < _threads.Length; i++)
            {
                int graph = i;
                _threads[i] = new Thread(() => Map(graph)) { IsBackground = true, Name = "GIF palette map" };
                _threads[i].Start();
            }

            _started = true;
        }

        // Takes the picture of `frame` (leaving it empty) for the next mapping thread free; with as many frames on
        // their way as it allows, it first waits for the oldest and writes it.
        public void Submit(AVFrame* frame, GifWriter writer, CancellationToken cancellationToken)
        {
            while (_submitted - _written >= _capacity)
            {
                WriteNext(writer, wait: true, cancellationToken);
            }

            ThrowIfFailed();
            AVFrame* job = ffmpeg.av_frame_alloc();
            if (job == null)
            {
                throw new OutOfMemoryException("gifp_job_alloc");
            }

            ffmpeg.av_frame_move_ref(job, frame);
            try
            {
                _queue.Add((_submitted, (IntPtr)job), cancellationToken);
            }
            catch
            {
                ffmpeg.av_frame_free(&job); // cancelled: never queued, so no thread frees it
                throw;
            }

            _submitted++;
        }

        // Writes the frames mapped so far, in order; with `waitForAll`, every frame submitted.
        public void WriteMapped(GifWriter writer, bool waitForAll, CancellationToken cancellationToken)
        {
            if (waitForAll)
            {
                _queue.CompleteAdding();
            }

            while (_written < _submitted && WriteNext(writer, waitForAll, cancellationToken))
            {
            }
        }

        // Writes the oldest frame on its way once it is mapped: false when it is not yet and `wait` is false.
        private bool WriteNext(GifWriter writer, bool wait, CancellationToken cancellationToken)
        {
            AVFrame* mapped;
            lock (_gate)
            {
                while (true)
                {
                    if (_failure != null)
                    {
                        throw new InvalidOperationException("Mapping a GIF frame to its palette failed.", _failure);
                    }

                    if (_mapped.Remove(_written, out IntPtr ready))
                    {
                        mapped = (AVFrame*)ready;
                        break;
                    }

                    if (!wait)
                    {
                        return false;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    Monitor.Wait(_gate, 50);
                }
            }

            try
            {
                writer.Write(mapped);
            }
            finally
            {
                ffmpeg.av_frame_free(&mapped);
                _written++;
            }

            return true;
        }

        // A mapping thread: each frame through this thread's own graph, one frame out for each frame in.
        private void Map(int graph)
        {
            AVFrame* mapped = null;
            try
            {
                foreach (var (index, pointer) in _queue.GetConsumingEnumerable())
                {
                    AVFrame* frame = (AVFrame*)pointer;
                    if (_stopping || Volatile.Read(ref _failure) != null)
                    {
                        ffmpeg.av_frame_free(&frame);
                        continue;
                    }

                    int pushed = ffmpeg.av_buffersrc_add_frame(_frameSources[graph], frame); // takes its picture
                    ffmpeg.av_frame_free(&frame);
                    ThrowIfErr(pushed, "gifp_src2_push");
                    mapped = ffmpeg.av_frame_alloc();
                    if (mapped == null)
                    {
                        throw new OutOfMemoryException("gifp_mapped_alloc");
                    }

                    ThrowIfErr(ffmpeg.av_buffersink_get_frame(_sinks[graph], mapped), "gifp_sink_pull");
                    lock (_gate)
                    {
                        _mapped[index] = (IntPtr)mapped;
                        Monitor.PulseAll(_gate);
                    }

                    mapped = null;
                }
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    _failure ??= ex;
                    Monitor.PulseAll(_gate);
                }
            }
            finally
            {
                if (mapped != null)
                {
                    ffmpeg.av_frame_free(&mapped);
                }
            }
        }

        private void ThrowIfFailed()
        {
            lock (_gate)
            {
                if (_failure != null)
                {
                    throw new InvalidOperationException("Mapping a GIF frame to its palette failed.", _failure);
                }
            }
        }

        // Stops the threads, then frees whatever frames are left and the graphs: a cancelled or failed export too.
        public void Dispose()
        {
            _stopping = true; // what is still queued is freed, not mapped
            if (!_queue.IsAddingCompleted)
            {
                _queue.CompleteAdding();
            }

            if (_started)
            {
                foreach (Thread thread in _threads)
                {
                    thread?.Join();
                }
            }

            while (_queue.TryTake(out var left))
            {
                AVFrame* frame = (AVFrame*)left.Frame;
                ffmpeg.av_frame_free(&frame);
            }

            foreach (IntPtr left in _mapped.Values)
            {
                AVFrame* frame = (AVFrame*)left;
                ffmpeg.av_frame_free(&frame);
            }

            _mapped.Clear();
            for (int i = 0; i < _graphs.Length; i++)
            {
                AVFilterGraph* graph = _graphs[i];
                if (graph != null)
                {
                    ffmpeg.avfilter_graph_free(&graph);
                    _graphs[i] = null;
                }
            }

            _queue.Dispose();
        }
    }

    // Runs the palette graph on the frame just pushed, so palettegen adds it to its histogram and frees it now
    // instead of the frame queueing in the graph. Nothing comes out before the end of the clip.
    private static unsafe void CountColors(AVFilterContext* paletteSink, AVFrame* scratch)
    {
        int got;
        while ((got = ffmpeg.av_buffersink_get_frame(paletteSink, scratch)) >= 0)
        {
            ffmpeg.av_frame_unref(scratch);
        }

        if (got != ffmpeg.AVERROR(11))
        {
            ThrowIfErr(got, "gifp_palette_run");
        }
    }

    private static unsafe AVFilterGraph* AllocGraph()
    {
        AVFilterGraph* graph = ffmpeg.avfilter_graph_alloc();
        if (graph == null)
        {
            throw new OutOfMemoryException("gifp_graph_alloc");
        }

        return graph;
    }

    private static unsafe AVFilterContext* CreateFilter(
        AVFilterGraph* graph, AVFilter* filter, string name, string? args, string ctx)
    {
        AVFilterContext* filterCtx = null;
        ThrowIfErr(ffmpeg.avfilter_graph_create_filter(&filterCtx, filter, name, args, null, graph), ctx);
        return filterCtx;
    }

    private static void ThrowIfErr(int err, string ctx)
    {
        if (err < 0)
        {
            throw FFmpegErrors.ToException(err, ctx);
        }
    }

    // One pass over the clip's video: decodes it, keeps the frames GifFrameTimeline schedules at the GIF's frame
    // rate and scales each to the GIF's size. Every pass opens the clip afresh, so both passes of the palette path
    // see the very same frames.
    /// <summary>
    /// A pass's frames, decoded and scaled on a thread of their own a few ahead of the caller, who meanwhile counts
    /// their colours (the first pass) or maps and writes them (the second); or on the caller's thread, one at a time,
    /// as before. One at a time, a 75-frame 720x396 GIF spent about 250 ms decoding in each pass while nothing else
    /// ran, of a transcode of 1.3 s. The same frames come in the same order with the same timestamps either way, so
    /// the GIF is the same byte for byte. Each frame keeps the pass's progress as it was when that frame was decoded,
    /// so progress reads as before. Up to about 8 MB of frames wait at once.
    /// </summary>
    private sealed unsafe class FrameFeed : IDisposable
    {
        private const long QueueBytes = 8L << 20;

        private readonly GifFrameSource _source;
        private readonly ManualResetEventSlim? _pauseGate;
        private readonly BlockingCollection<(IntPtr Frame, double Progress)>? _ready;
        private readonly CancellationTokenSource? _stop;
        private readonly Thread? _thread;
        private Exception? _failure;

        public FrameFeed(
            GifFrameSource source, bool ahead, ManualResetEventSlim? pauseGate, CancellationToken cancellationToken)
        {
            _source = source;
            _pauseGate = pauseGate;
            if (!ahead)
            {
                return;
            }

            long frameBytes = Math.Max(1L, (long)source.Width * source.Height * 3);
            int depth = (int)Math.Clamp(QueueBytes / frameBytes, 2, 8);
            _ready = new BlockingCollection<(IntPtr, double)>(depth);
            _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _thread = new Thread(Decode) { IsBackground = true, Name = "GIF decode" };
            _thread.Start();
        }

        /// <summary>How far the pass had decoded when the frame last handed over was decoded, 0..1.</summary>
        public double Progress { get; private set; }

        /// <summary>
        /// The pass's next frame, moved into <paramref name="output"/>, or false at the end of the clip. Rethrows what
        /// the decode failed with, and throws <see cref="OperationCanceledException"/> once
        /// <paramref name="cancellationToken"/> is cancelled.
        /// </summary>
        public bool NextFrame(AVFrame* output, CancellationToken cancellationToken)
        {
            if (_ready == null)
            {
                bool got = _source.NextFrame(output, _pauseGate, cancellationToken);
                Progress = _source.Progress;
                return got;
            }

            if (!_ready.TryTake(out var next, Timeout.Infinite, cancellationToken))
            {
                // The decode thread has finished: at the end of the clip, or stopped by a failure or a cancellation.
                if (_failure != null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(_failure).Throw();
                }

                cancellationToken.ThrowIfCancellationRequested();
                return false;
            }

            AVFrame* frame = (AVFrame*)next.Frame;
            ffmpeg.av_frame_unref(output);
            ffmpeg.av_frame_move_ref(output, frame);
            ffmpeg.av_frame_free(&frame);
            Progress = next.Progress;
            return true;
        }

        private void Decode()
        {
            try
            {
                while (true)
                {
                    AVFrame* frame = ffmpeg.av_frame_alloc();
                    if (frame == null)
                    {
                        throw new OutOfMemoryException("gif_decode_ahead_frame");
                    }

                    bool queued = false;
                    try
                    {
                        if (!_source.NextFrame(frame, _pauseGate, _stop!.Token))
                        {
                            return;
                        }

                        _ready!.Add(((IntPtr)frame, _source.Progress), _stop.Token);
                        queued = true;
                    }
                    finally
                    {
                        if (!queued)
                        {
                            ffmpeg.av_frame_free(&frame);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (_stop!.IsCancellationRequested)
            {
                // Disposed, or the caller cancelled: NextFrame tells which.
            }
            catch (Exception ex)
            {
                _failure = ex;
            }
            finally
            {
                _ready!.CompleteAdding();
            }
        }

        public void Dispose()
        {
            if (_ready == null)
            {
                return;
            }

            _stop!.Cancel();
            _thread!.Join();
            while (_ready.TryTake(out var left))
            {
                AVFrame* frame = (AVFrame*)left.Frame;
                ffmpeg.av_frame_free(&frame);
            }

            _ready.Dispose();
            _stop.Dispose();
        }
    }

    private sealed unsafe class GifFrameSource : IDisposable
    {
        private readonly AVPixelFormat _format;
        private readonly GifFrameTimeline _timeline;
        private readonly int _videoStream;
        private readonly double _timeBaseSeconds;
        private readonly double _durationSeconds;
        private AVFormatContext* _input;
        private AVCodecContext* _decoder;
        private SwsContext* _scaler;
        private AVPacket* _packet;
        private AVFrame* _decoded;
        private bool _flushed;
        private long? _firstTimestamp;
        private double _decodedSeconds;

        public GifFrameSource(string inputPath, int targetFps, int maxWidth, AVPixelFormat format, bool evenSize)
        {
            _format = format;
            try
            {
                AVFormatContext* input = null;
                ThrowIfErr(LibavInput.Open(&input, inputPath), "gif_open_input");
                _input = input;
                ThrowIfErr(ffmpeg.avformat_find_stream_info(_input, null), "gif_find_stream_info");
                _videoStream = ffmpeg.av_find_best_stream(_input, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
                if (_videoStream < 0)
                {
                    throw new InvalidOperationException("No video stream for GIF transcoding.");
                }

                AVStream* inStream = _input->streams[_videoStream];
                AVCodec* dec = ffmpeg.avcodec_find_decoder(inStream->codecpar->codec_id);
                if (dec == null)
                {
                    throw new InvalidOperationException("Video decoder unavailable for GIF transcoding.");
                }

                _decoder = ffmpeg.avcodec_alloc_context3(dec);
                if (_decoder == null)
                {
                    throw new OutOfMemoryException("gif_alloc_decoder");
                }

                ThrowIfErr(ffmpeg.avcodec_parameters_to_context(_decoder, inStream->codecpar), "gif_par_to_ctx");
                // Multi-core decode (auto), as the clip exporter does: the palette path decodes the clip twice.
                _decoder->thread_count = 0;
                _decoder->thread_type = ffmpeg.FF_THREAD_FRAME | ffmpeg.FF_THREAD_SLICE;
                ThrowIfErr(ffmpeg.avcodec_open2(_decoder, dec, null), "gif_open_decoder");

                int srcW = _decoder->width > 0 ? _decoder->width : inStream->codecpar->width;
                int srcH = _decoder->height > 0 ? _decoder->height : inStream->codecpar->height;
                if (srcW <= 0 || srcH <= 0)
                {
                    throw new InvalidOperationException("Invalid input dimensions for GIF transcoding.");
                }

                int outW = srcW;
                int outH = srcH;
                if (maxWidth > 0 && srcW > maxWidth)
                {
                    double scale = maxWidth / (double)srcW;
                    outW = maxWidth;
                    outH = Math.Max(1, (int)Math.Round(srcH * scale));
                }

                if (evenSize)
                {
                    outW = Math.Max(2, outW & ~1);
                    outH = Math.Max(2, outH & ~1);
                }

                Width = outW;
                Height = outH;
                Fps = Math.Clamp(targetFps, 1, 60);
                AVRational guessed = ffmpeg.av_guess_frame_rate(_input, inStream, null);
                double sourceFps = guessed.num > 0 && guessed.den > 0 ? ffmpeg.av_q2d(guessed) : Fps;
                _timeBaseSeconds = ffmpeg.av_q2d(inStream->time_base);
                _timeline = new GifFrameTimeline(_timeBaseSeconds, Fps, sourceFps);
                _durationSeconds = inStream->duration > 0 && _timeBaseSeconds > 0
                    ? inStream->duration * _timeBaseSeconds
                    : _input->duration > 0 ? _input->duration / (double)ffmpeg.AV_TIME_BASE : 0;

                _scaler = ffmpeg.sws_getContext(
                    srcW, srcH, _decoder->pix_fmt,
                    outW, outH, format,
                    (int)SwsFlags.SWS_BILINEAR, null, null, null);
                if (_scaler == null)
                {
                    throw new InvalidOperationException("Failed to create sws context for GIF.");
                }

                // The stream's own colours, as its first frames say them (avformat_find_stream_info decoded them).
                LibavColorMatrix.ReadAs(_scaler, _decoder->colorspace, _decoder->color_range);

                _packet = ffmpeg.av_packet_alloc();
                _decoded = ffmpeg.av_frame_alloc();
                if (_packet == null || _decoded == null)
                {
                    throw new OutOfMemoryException("gif_alloc_packet_frame");
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public int Width { get; }

        public int Height { get; }

        public int Fps { get; }

        /// <summary>
        /// How far into the clip this pass has decoded, 0..1, by source time; stays 0 when the clip's length is
        /// unknown. (Not by bytes read: opening a short clip probes all of it.)
        /// </summary>
        public double Progress => _durationSeconds > 0 ? Math.Clamp(_decodedSeconds / _durationSeconds, 0, 1) : 0;

        /// <summary>
        /// Scales the next frame the timeline keeps into <paramref name="output"/> (a fresh buffer, with the GIF's pts)
        /// and returns true, or returns false at the end of the clip. Checks for cancellation, and waits while
        /// <paramref name="pauseGate"/> is reset, before every packet read.
        /// </summary>
        public bool NextFrame(AVFrame* output, ManualResetEventSlim? pauseGate, CancellationToken cancellationToken)
        {
            while (true)
            {
                int gr = ffmpeg.avcodec_receive_frame(_decoder, _decoded);
                if (gr == ffmpeg.AVERROR_EOF)
                {
                    return false;
                }

                if (gr == ffmpeg.AVERROR(11))
                {
                    if (_flushed)
                    {
                        return false;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    pauseGate?.Wait(cancellationToken);
                    SendNextPacket();
                    continue;
                }

                ThrowIfErr(gr, "gif_receive_frame");

                long? ts = _decoded->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE
                    ? _decoded->best_effort_timestamp
                    : null;
                if (ts.HasValue)
                {
                    _firstTimestamp ??= ts.Value;
                    _decodedSeconds = Math.Max(_decodedSeconds, (ts.Value - _firstTimestamp.Value) * _timeBaseSeconds);
                }

                if (!_timeline.TrySchedule(ts, out long outputPts))
                {
                    continue;
                }

                ffmpeg.av_frame_unref(output);
                output->format = (int)_format;
                output->width = Width;
                output->height = Height;
                ThrowIfErr(ffmpeg.av_frame_get_buffer(output, 0), "gif_frame_buffer");
                ffmpeg.sws_scale(_scaler, _decoded->data, _decoded->linesize, 0, _decoded->height, output->data, output->linesize);
                output->pts = outputPts;
                return true;
            }
        }

        // Hands the decoder the clip's next video packet, or tells it the clip has ended.
        private void SendNextPacket()
        {
            while (true)
            {
                int rr = ffmpeg.av_read_frame(_input, _packet);
                if (rr == ffmpeg.AVERROR_EOF)
                {
                    ThrowIfErr(ffmpeg.avcodec_send_packet(_decoder, null), "gif_flush_decoder");
                    _flushed = true;
                    return;
                }

                if (rr == ffmpeg.AVERROR(11))
                {
                    continue;
                }

                ThrowIfErr(rr, "gif_read_frame");
                if (_packet->stream_index != _videoStream)
                {
                    ffmpeg.av_packet_unref(_packet);
                    continue;
                }

                int sent = ffmpeg.avcodec_send_packet(_decoder, _packet);
                ffmpeg.av_packet_unref(_packet);
                ThrowIfErr(sent, "gif_send_packet");
                return;
            }
        }

        public void Dispose()
        {
            AVPacket* packet = _packet;
            if (packet != null) ffmpeg.av_packet_free(&packet);
            _packet = null;
            AVFrame* decoded = _decoded;
            if (decoded != null) ffmpeg.av_frame_free(&decoded);
            _decoded = null;
            if (_scaler != null) ffmpeg.sws_freeContext(_scaler);
            _scaler = null;
            AVCodecContext* decoder = _decoder;
            if (decoder != null) ffmpeg.avcodec_free_context(&decoder);
            _decoder = null;
            AVFormatContext* input = _input;
            if (input != null) ffmpeg.avformat_close_input(&input);
            _input = null;
        }
    }

    // The GIF encoder and muxer for one output file. Disposed without Finish — the export was cancelled or failed —
    // it deletes the file it had started, so no half-written GIF is left behind.
    private sealed unsafe class GifWriter : IDisposable
    {
        private readonly string _path;
        private AVFormatContext* _output;
        private AVCodecContext* _encoder;
        private AVStream* _stream;
        private AVPacket* _packet;
        private bool _fileOpened;
        private bool _finished;

        public GifWriter(string path, int width, int height, int fps, AVPixelFormat pixelFormat)
        {
            _path = path;
            try
            {
                AVFormatContext* output = null;
                ThrowIfErr(ffmpeg.avformat_alloc_output_context2(&output, null, "gif", path), "gif_alloc_output");
                _output = output;
                if (_output == null)
                {
                    throw new InvalidOperationException("Failed to allocate GIF output context.");
                }

                AVCodec* enc = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_GIF);
                if (enc == null)
                {
                    throw new InvalidOperationException("GIF encoder unavailable.");
                }

                _encoder = ffmpeg.avcodec_alloc_context3(enc);
                if (_encoder == null)
                {
                    throw new OutOfMemoryException("gif_alloc_encoder");
                }

                _encoder->codec_id = AVCodecID.AV_CODEC_ID_GIF;
                _encoder->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
                _encoder->width = width;
                _encoder->height = height;
                _encoder->pix_fmt = pixelFormat;
                _encoder->time_base = new AVRational { num = 1, den = fps };
                _encoder->framerate = new AVRational { num = fps, den = 1 };
                ThrowIfErr(ffmpeg.avcodec_open2(_encoder, enc, null), "gif_open_encoder");

                _stream = ffmpeg.avformat_new_stream(_output, null);
                if (_stream == null)
                {
                    throw new OutOfMemoryException("gif_new_stream");
                }

                ThrowIfErr(ffmpeg.avcodec_parameters_from_context(_stream->codecpar, _encoder), "gif_stream_par");
                _stream->time_base = _encoder->time_base;
                _stream->avg_frame_rate = _encoder->framerate;

                if ((_output->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
                {
                    ThrowIfErr(ffmpeg.avio_open(&_output->pb, path, ffmpeg.AVIO_FLAG_WRITE), "gif_avio_open");
                    _fileOpened = true;
                }

                ThrowIfErr(ffmpeg.avformat_write_header(_output, null), "gif_write_header");

                _packet = ffmpeg.av_packet_alloc();
                if (_packet == null)
                {
                    throw new OutOfMemoryException("gif_alloc_packet");
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Write(AVFrame* frame)
        {
            ThrowIfErr(ffmpeg.avcodec_send_frame(_encoder, frame), "gif_send_frame");
            WritePackets();
        }

        public void Finish()
        {
            ThrowIfErr(ffmpeg.avcodec_send_frame(_encoder, null), "gif_flush_encoder");
            WritePackets();
            ThrowIfErr(ffmpeg.av_write_trailer(_output), "gif_write_trailer");
            _finished = true;
        }

        private void WritePackets()
        {
            while (true)
            {
                int gp = ffmpeg.avcodec_receive_packet(_encoder, _packet);
                if (gp == ffmpeg.AVERROR_EOF || gp == ffmpeg.AVERROR(11))
                {
                    break;
                }
                ThrowIfErr(gp, "gif_receive_packet");

                ffmpeg.av_packet_rescale_ts(_packet, _encoder->time_base, _stream->time_base);
                _packet->stream_index = _stream->index;
                ThrowIfErr(ffmpeg.av_interleaved_write_frame(_output, _packet), "gif_write_packet");
                ffmpeg.av_packet_unref(_packet);
            }
        }

        public void Dispose()
        {
            AVPacket* packet = _packet;
            if (packet != null) ffmpeg.av_packet_free(&packet);
            _packet = null;
            AVCodecContext* encoder = _encoder;
            if (encoder != null) ffmpeg.avcodec_free_context(&encoder);
            _encoder = null;
            if (_output != null)
            {
                if ((_output->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0 && _output->pb != null)
                {
                    ffmpeg.avio_closep(&_output->pb);
                }
                ffmpeg.avformat_free_context(_output);
                _output = null;
            }

            if (_fileOpened && !_finished)
            {
                _fileOpened = false;
                try
                {
                    File.Delete(_path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AppLog.Warning("Gif.DeletePartialOutput", ex);
                }
            }
        }
    }

    // Turns a pass's progress into the whole export's, 0..1, in whole-percent steps (enough for a progress bar,
    // without flooding it) that never go back: the RGB8 fallback starts the clip over after a failed palette path.
    private sealed class GifProgress
    {
        private readonly IProgress<double>? _sink;
        private int _lastPercent = -1;

        public GifProgress(IProgress<double>? sink) => _sink = sink;

        // done (0..1) of a pass that covers [start, start + share] of the export.
        public void Report(double start, double share, double done)
        {
            if (_sink == null)
            {
                return;
            }

            double value = Math.Clamp(start + (share * done), 0, 1);
            int percent = (int)(value * 100);
            if (percent <= _lastPercent)
            {
                return;
            }

            _lastPercent = percent;
            _sink.Report(value);
        }
    }
}

internal sealed class GifFrameTimeline
{
    private readonly double _inputTimeBaseSeconds;
    private readonly int _targetFps;
    private readonly double _fallbackSourceFps;
    private long? _firstInputTimestamp;
    private long _decodedFrameIndex;
    private long _lastOutputPts = -1;

    public GifFrameTimeline(double inputTimeBaseSeconds, int targetFps, double fallbackSourceFps)
    {
        _inputTimeBaseSeconds = inputTimeBaseSeconds > 0 ? inputTimeBaseSeconds : 0;
        _targetFps = Math.Clamp(targetFps, 1, 60);
        _fallbackSourceFps = fallbackSourceFps > 0 ? fallbackSourceFps : _targetFps;
    }

    public bool TrySchedule(long? inputTimestamp, out long outputPts)
    {
        double elapsedSeconds;
        if (inputTimestamp.HasValue && _inputTimeBaseSeconds > 0)
        {
            _firstInputTimestamp ??= inputTimestamp.Value;
            elapsedSeconds = Math.Max(
                0,
                (inputTimestamp.Value - _firstInputTimestamp.Value) * _inputTimeBaseSeconds);
        }
        else
        {
            elapsedSeconds = _decodedFrameIndex / _fallbackSourceFps;
        }

        _decodedFrameIndex++;
        long candidatePts = Math.Max(
            0,
            (long)Math.Floor((elapsedSeconds * _targetFps) + 0.000001));
        if (candidatePts <= _lastOutputPts)
        {
            outputPts = 0;
            return false;
        }

        _lastOutputPts = candidatePts;
        outputPts = candidatePts;
        return true;
    }
}
