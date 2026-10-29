using System;
using GimmeCapture.Models;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using FFmpeg.AutoGen;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Media;
using GimmeCapture.Services.Interop;
using GimmeCapture.Services.Platforms.Windows;
using SkiaSharp;

namespace GimmeCapture.Services.Core.Media.NativeFFmpeg;

/// <summary>
/// Records several windows at once via Windows Graphics Capture, tiling them into an auto-grid on a single
/// canvas and encoding ONE H.264/H.265-in-Matroska segment. Each window is an independent
/// <see cref="WgcWindowCaptureSource"/>; every output frame composites the latest BGRA of each source into
/// its grid cell (aspect-fit) with SkiaSharp, then runs the same sws → encoder → muxer path as
/// <see cref="LibavWgcMkvSession"/>. The single output means the existing audio/finalize pipeline is reused
/// unchanged.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed class LibavWgcCompositeMkvSession : LibavRecordingSession
{
    /// <summary>
    /// If no captured window has delivered a frame within this window, the start is treated as failed so the
    /// caller can fall back to gdigrab region capture rather than hang. See docs/WGC_HANDOFF.md "Fix A".
    /// 4000ms gives the cold first off-thread GPU→CPU readback headroom; see LibavWgcMkvSession for rationale.
    /// </summary>
    private const int FirstFrameTimeoutMs = 4000;

    /// <summary>
    /// Hard cap on each window's WGC/D3D bring-up (<see cref="WgcWindowCaptureSource.Start"/>). On the
    /// dual-monitor repro the bring-up itself can block for many seconds (not covered by the first-frame
    /// timeout). All windows are brought up concurrently under this single shared cap; any that don't finish
    /// in time are abandoned (detached) and skipped. See docs/WGC_HANDOFF.md Fix A.
    /// </summary>
    private const int BringupTimeoutMs = 1500;

    /// <summary>True when no captured window delivered a first frame — the "no frames" repro. See
    /// <see cref="LibavWgcMkvSession.TimedOutWaitingForFrame"/>.</summary>
    public bool TimedOutWaitingForFrame { get; private set; }

    protected override TimeSpan StartupGateTimeout =>
        TimeSpan.FromMilliseconds(BringupTimeoutMs + FirstFrameTimeoutMs + 2000);

    // See LibavWgcMkvSession: the worker ends promptly, this only lets it close its file before a fallback.
    protected override TimeSpan DisposeWait => TimeSpan.FromSeconds(3);

    protected override bool OnStartupGateTimedOut()
    {
        // Report a FAILED start (not started=true) so the caller falls back to gdigrab and the worker can't
        // hang frameless on stop. See docs/WGC_HANDOFF.md Fix A.
        AppLog.Information("Wgc.Composite.StartupGate timeout → started=false (fallback to gdigrab)");
        return false;
    }

    public Task<bool> StartAsync(string outputPath, IReadOnlyList<IntPtr> hwnds, int fps, bool drawMouse, VideoCodec codec)
    {
        FFmpegRuntime.EnsureInitialized();
        AppLog.Information($"Wgc.Build sessionType=composite windows={hwnds.Count} bringupTimeoutMs={BringupTimeoutMs} firstFrameTimeoutMs={FirstFrameTimeoutMs}");
        LogNative($"StartAsync requested: out={outputPath}, windows={hwnds.Count}, fps={fps}, drawMouse={drawMouse}, codec={codec}");
        var hwndList = new List<IntPtr>(hwnds);
        return StartWorker((ct, firstFrameTcs) => RunTranscode(outputPath, hwndList, fps, drawMouse, codec, ct, firstFrameTcs));
    }

    private unsafe void RunTranscode(
        string outputPath,
        List<IntPtr> hwnds,
        int fps,
        bool drawMouse,
        VideoCodec codec,
        CancellationToken ct,
        TaskCompletionSource<bool> firstFrameTcs)
    {
        SwsContext* sws = null;
        RecordingSegmentWriter? writer = null;

        var sources = new List<WgcWindowCaptureSource>();
        var buffers = new List<byte[]?>();
        try
        {
            // Bring up every window's capture source CONCURRENTLY under a single shared timeout. WGC/D3D bring-up
            // can wedge for many seconds on the dual-monitor repro, so (a) never let a wedged Start() block the
            // worker and (b) don't pay the timeout once per window sequentially. Any source that doesn't come up
            // in time is abandoned (detached) and skipped. See docs/WGC_HANDOFF.md Fix A.
            var sizes = new List<(int Width, int Height)>();
            var bringups = new List<(IntPtr Hwnd, WgcWindowCaptureSource Src, Task<bool> Task)>();
            foreach (var hwnd in hwnds)
            {
                // A window that is gone is skipped before any WGC work, not left to a bring-up to abandon.
                if (!User32.IsWindow(hwnd))
                {
                    AppLog.Information($"Wgc.Composite.Start.Gone hwnd=0x{hwnd.ToInt64():X} → skipped");
                    continue;
                }

                var src = new WgcWindowCaptureSource(hwnd, drawMouse);
                bringups.Add((hwnd, src, Task.Run(() => src.Start())));
            }

            var bringupTasks = new Task[bringups.Count];
            for (int i = 0; i < bringups.Count; i++)
            {
                bringupTasks[i] = bringups[i].Task;
            }

            try
            {
                Task.WaitAll(bringupTasks, BringupTimeoutMs, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Individual faults are inspected per-task below.
            }

            foreach (var (hwnd, src, task) in bringups)
            {
                if (task.IsCompletedSuccessfully && task.Result)
                {
                    sources.Add(src);
                    buffers.Add(null);
                    sizes.Add((src.InitialWidth, src.InitialHeight));
                    AppLog.Information($"Wgc.Composite.Start hwnd=0x{hwnd.ToInt64():X} size={src.InitialWidth}x{src.InitialHeight} adapter='{src.AdapterDescription}'");
                    AppLog.Information($"Wgc.Composite.Start hwnd=0x{hwnd.ToInt64():X} {WgcInterop.DescribeWindowMonitor(hwnd)}");
                }
                else
                {
                    AppLog.Information($"Wgc.Composite.Start.Timeout hwnd=0x{hwnd.ToInt64():X} bringupMs={BringupTimeoutMs} → skipped/abandoned");
                    // Abandon after the (possibly wedged) Start() settles; never block this loop on teardown.
                    WgcWindowCaptureSource.DisposeDetachedAfter(task, src, $"composite hwnd=0x{hwnd.ToInt64():X} (bringup-abandon)");
                }
            }

            if (sources.Count == 0)
            {
                throw new InvalidOperationException("None of the selected windows could be captured.");
            }

            if (sources.Count != hwnds.Count)
            {
                LastWarningMessage = $"{hwnds.Count - sources.Count} window(s) could not be captured and were skipped.";
            }

            var (canvasW, canvasH) = CompositeGridLayout.CanvasSize(sizes);
            var cells = CompositeGridLayout.ComputeCells(sources.Count, canvasW, canvasH);

            writer = RecordingSegmentWriter.Open(
                outputPath, codec, PreferHardwareEncoder, canvasW, canvasH, fps, VideoCrf, VideoBitrate);
            AVCodecContext* encCtx = writer.Encoder;

            SelectedEncoderName = writer.EncoderName;
            EncoderOpenTime = writer.EncoderOpenTime;
            string? warningMessage = writer.EncoderWarning;
            if (!string.IsNullOrWhiteSpace(warningMessage))
            {
                LastWarningMessage = string.IsNullOrEmpty(LastWarningMessage) ? warningMessage : $"{LastWarningMessage} {warningMessage}";
            }
            LogNative($"Composite encoder: {writer.EncoderName} ({canvasW}x{canvasH}@{fps}, {sources.Count} windows)");

            // Persistent composite canvas (BGRA) reused every frame — no per-frame Skia allocation.
            using var canvas = new SKBitmap(new SKImageInfo(canvasW, canvasH, SKColorType.Bgra8888, SKAlphaType.Premul));
            using var skCanvas = new SKCanvas(canvas);
            IntPtr canvasPixels = canvas.GetPixels();
            int canvasStride = canvas.RowBytes;

            sws = ffmpeg.sws_getContext(
                canvasW, canvasH, AVPixelFormat.AV_PIX_FMT_BGRA,
                encCtx->width, encCtx->height, encCtx->pix_fmt,
                (int)SwsFlags.SWS_FAST_BILINEAR, null, null, null);
            if (sws == null)
            {
                throw new InvalidOperationException("sws_getContext failed for composite canvas.");
            }

            // Prime: wait until at least one source has delivered a frame. Bail out fast if none ever do (the
            // dual-monitor failure mode) so the caller can fall back to gdigrab instead of hanging.
            var primeClock = Stopwatch.StartNew();
            while (!ct.IsCancellationRequested)
            {
                bool any = false;
                for (int i = 0; i < sources.Count; i++)
                {
                    var buf = buffers[i];
                    if (sources[i].TryCopyLatest(ref buf, out _, out _))
                    {
                        buffers[i] = buf;
                        any = true;
                    }
                }

                if (any)
                {
                    AppLog.Information($"Wgc.Composite.FirstFrame arrivedMs={primeClock.ElapsedMilliseconds} windows={sources.Count}");
                    break;
                }

                if (primeClock.ElapsedMilliseconds >= FirstFrameTimeoutMs)
                {
                    AppLog.Information($"Wgc.Composite.FirstFrame.Timeout waitedMs={primeClock.ElapsedMilliseconds} windows={sources.Count} → fallback");
                    TimedOutWaitingForFrame = true;
                    throw new TimeoutException($"Composite WGC produced no first frame within {FirstFrameTimeoutMs}ms.");
                }

                Thread.Sleep(5);
            }

            var clock = Stopwatch.StartNew();
            while (!ct.IsCancellationRequested)
            {
                if (!RecordingFramePacer.IsFrameDue(clock, writer.FrameCount, fps))
                {
                    continue;
                }

                skCanvas.Clear(SKColors.Black);
                for (int i = 0; i < sources.Count; i++)
                {
                    var buf = buffers[i];
                    if (!sources[i].TryCopyLatest(ref buf, out int w, out int h) || buf == null || w <= 0 || h <= 0)
                    {
                        continue;
                    }

                    buffers[i] = buf;
                    DrawSourceIntoCell(skCanvas, buf, w, h, cells[i]);
                }
                skCanvas.Flush();

                AVFrame* encFrame = writer.FrameToFill();
                var srcData = new byte_ptrArray4();
                srcData[0] = (byte*)canvasPixels;
                var srcStride = new int_array4();
                srcStride[0] = canvasStride;
                ffmpeg.sws_scale(sws, srcData, srcStride, 0, canvasH, encFrame->data, encFrame->linesize);

                if (writer.FrameCount == 0)
                {
                    firstFrameTcs.TrySetResult(true);
                }

                writer.EncodeFrame(pts: writer.FrameCount);
            }

            writer.Flush();
            writer.WriteTrailer();
            VideoLength = writer.VideoLength;

            if (writer.FrameCount == 0)
            {
                throw new InvalidOperationException("Composite WGC pipeline produced zero frames.");
            }

            AppLog.Information($"Wgc.Composite.Stop windows={sources.Count} frames={writer.FrameCount} packets={writer.PacketCount}");
            AppLog.Debug("LibavWgcCompositeMkvSession", $"[LibavWgcComposite] segment done: frames={writer.FrameCount}, packets={writer.PacketCount}, output='{outputPath}'");
        }
        finally
        {
            // Detach each source's disposal: a wedged WGC teardown (dual-monitor "no frames" repro) must not keep
            // this worker alive, or stop / pin / the gdigrab fallback would block on it. See docs/WGC_HANDOFF.md.
            for (int i = 0; i < sources.Count; i++)
            {
                WgcWindowCaptureSource.DisposeDetached(sources[i], $"composite#{i}");
            }

            writer?.Dispose();

            if (sws != null)
            {
                ffmpeg.sws_freeContext(sws);
            }
        }
    }

    private static void DrawSourceIntoCell(SKCanvas canvas, byte[] bgra, int width, int height, SKRect cell)
    {
        var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bmp = new SKBitmap();
            if (!bmp.InstallPixels(info, handle.AddrOfPinnedObject(), width * 4))
            {
                return;
            }

            canvas.DrawBitmap(bmp, CompositeGridLayout.FitInto(width, height, cell));
        }
        finally
        {
            handle.Free();
        }
    }
}
