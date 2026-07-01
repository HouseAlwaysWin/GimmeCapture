using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Media;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using GimmeCapture.ViewModels.Floating;
using ReactiveUI;
using SkiaSharp;

namespace GimmeCapture.ViewModels.Main;

/// <summary>
/// Drives the standalone Pin-style multi-segment clip-trim window for a Compress queue item: the source is
/// split into contiguous pieces on a timeline strip; the user scrubs (drag), Splits at the playhead, and taps
/// a piece to keep/drop it. On Apply the kept runs are written back via <c>onApply</c> and concatenated at
/// encode time. Playback plays the raw source (to find cut points); rotation is applied at display time.
/// Reuses the shared VideoSegmentEditor / VideoEditSegment / SegmentBlockViewModel (same engine as the Pin editor).
/// </summary>
internal sealed class TrimViewModel : ViewModelBase, IDisposable
{
    private readonly string _sourcePath;
    private readonly int _fps;
    private readonly int _decodeW;
    private readonly int _decodeH;
    private readonly double _duration;
    private readonly int _rotation;                  // applied to the displayed frame so it matches output orientation
    private readonly Action<IReadOnlyList<VideoEditSegment>> _onApply;

    private CancellationTokenSource? _playCts;
    private CancellationTokenSource? _stepCts;

    internal TrimViewModel(
        string sourcePath, double durationSeconds, int fps, int sourceWidth, int sourceHeight,
        int rotation, IReadOnlyList<VideoEditSegment> initialKeptRuns, Action<IReadOnlyList<VideoEditSegment>> onApply)
    {
        _sourcePath = sourcePath;
        _fps = fps > 0 ? fps : 30;
        _rotation = ((rotation % 360) + 360) % 360;
        _onApply = onApply;
        _duration = durationSeconds > 0 ? durationSeconds : 0;

        int sw = sourceWidth > 0 ? sourceWidth : 1280;
        int sh = sourceHeight > 0 ? sourceHeight : 720;
        double scale = Math.Min(1.0, 720.0 / Math.Max(1, sw)); // cap decode width ~720px for responsiveness
        _decodeW = Math.Max(2, (int)(sw * scale)); _decodeW -= _decodeW & 1;
        _decodeH = Math.Max(2, (int)(sh * scale)); _decodeH -= _decodeH & 1;

        Title = Path.GetFileName(sourcePath);

        PlayPauseCommand = ReactiveCommand.CreateFromTask(TogglePlayAsync);
        StepBackCommand = ReactiveCommand.CreateFromTask(() => StepAsync(-1));
        StepForwardCommand = ReactiveCommand.CreateFromTask(() => StepAsync(1));
        SkipBackCommand = ReactiveCommand.CreateFromTask(() => SeekAsync(PositionSeconds - 5));
        SkipForwardCommand = ReactiveCommand.CreateFromTask(() => SeekAsync(PositionSeconds + 5));
        SplitCommand = ReactiveCommand.Create(Split);
        ApplyCommand = ReactiveCommand.Create(Apply);
        CancelCommand = ReactiveCommand.Create(() => RequestClose?.Invoke());
        PlayPauseCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.TrimPlayPause", ex));
        StepBackCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.TrimStepBack", ex));
        StepForwardCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.TrimStepFwd", ex));
        SkipBackCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.TrimSkipBack", ex));
        SkipForwardCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.TrimSkipFwd", ex));
        SplitCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.TrimSplit", ex));

        ReplaceSegments(BuildEditSegments(initialKeptRuns, _duration));
    }

    public string Title { get; }

    /// <summary>Total source duration — the strip's full-width basis (all pieces cover [0,duration]).</summary>
    public double TotalSourceDuration => _duration;

    // Timeline strip state (mirrors FloatingVideoViewModel.Segments): the source split into contiguous pieces.
    public ObservableCollection<VideoEditSegment> EditSegments { get; } = new();
    public ObservableCollection<SegmentBlockViewModel> SegmentBlocks { get; } = new();

    /// <summary>Raised after the strip blocks are rebuilt so the window can recompute proportional layout.</summary>
    public event Action? SegmentLayoutChanged;

    /// <summary>Set by the window; invoked to close it from Apply/Cancel.</summary>
    public Action? RequestClose { get; set; }

    private Bitmap? _frame;
    public Bitmap? Frame { get => _frame; private set => this.RaiseAndSetIfChanged(ref _frame, value); }

    private double _positionSeconds;
    public double PositionSeconds
    {
        get => _positionSeconds;
        set
        {
            this.RaiseAndSetIfChanged(ref _positionSeconds, value);
            this.RaisePropertyChanged(nameof(TimeText));
        }
    }

    public string TimeText => $"{Fmt(_positionSeconds)} / {Fmt(_duration)}";

    public string RangeText
    {
        get
        {
            VideoEditSegment[] runs = KeptRuns();
            double total = runs.Sum(r => r.SourceDuration);
            LocalizationService loc = LocalizationService.Instance;
            return $"{runs.Length} {loc["CompressTrimSegments"]}    {loc["CompressTrimLength"]} {Fmt(total)}";
        }
    }

    private static string Fmt(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"m\:ss");

    private bool _isPlaying;
    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isPlaying, value);
            this.RaisePropertyChanged(nameof(PlayPauseGlyph));
        }
    }

    public string PlayPauseGlyph => _isPlaying ? "⏸" : "▶";

    private bool _isPreparing = true;
    public bool IsPreparing { get => _isPreparing; private set => this.RaiseAndSetIfChanged(ref _isPreparing, value); }

    public ReactiveCommand<Unit, Unit> PlayPauseCommand { get; }
    public ReactiveCommand<Unit, Unit> StepBackCommand { get; }
    public ReactiveCommand<Unit, Unit> StepForwardCommand { get; }
    public ReactiveCommand<Unit, Unit> SkipBackCommand { get; }
    public ReactiveCommand<Unit, Unit> SkipForwardCommand { get; }
    public ReactiveCommand<Unit, Unit> SplitCommand { get; }
    public ReactiveCommand<Unit, Unit> ApplyCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    /// <summary>Decodes and shows the frame at the current position. Call once after the window opens.</summary>
    public async Task InitializeAsync()
    {
        IsPreparing = true;
        try { await ShowFrameAtAsync(PositionSeconds); }
        catch (Exception ex) { AppLog.Error("Compress.TrimInit", ex); }
        finally { IsPreparing = false; }
    }

    // ── Segment editing (shared engine: VideoSegmentEditor) ──

    private VideoEditSegment[] KeptSegments() => EditSegments.Where(s => s.Kept).ToArray();

    /// <summary>Kept pieces with adjacent contiguous ones merged — the runs that get concatenated on encode.</summary>
    public VideoEditSegment[] KeptRuns() => VideoSegmentEditor.CoalesceContiguous(KeptSegments()).ToArray();

    public void Split()
    {
        if (EditSegments.Count == 0)
        {
            return;
        }

        // Split the piece under the playhead (source time); both halves inherit the Kept flag.
        ReplaceSegments(VideoSegmentEditor.SplitAtSourceTime(EditSegments.ToArray(), PositionSeconds));
    }

    /// <summary>Toggle keep/drop of the piece at index (from a strip tap). Never drops the last kept piece.</summary>
    public void ToggleKept(int index)
    {
        if (index < 0 || index >= EditSegments.Count)
        {
            return;
        }

        VideoEditSegment seg = EditSegments[index];
        bool newKept = !seg.Kept;
        if (!newKept && KeptSegments().Length <= 1)
        {
            return; // never drop the last kept piece
        }

        EditSegments[index] = seg with { Kept = newKept };
        RebuildSegmentBlocks();
    }

    private void ReplaceSegments(IReadOnlyList<VideoEditSegment> segments)
    {
        EditSegments.Clear();
        foreach (VideoEditSegment s in segments)
        {
            EditSegments.Add(s);
        }

        RebuildSegmentBlocks();
    }

    private void RebuildSegmentBlocks()
    {
        SegmentBlocks.Clear();
        for (int i = 0; i < EditSegments.Count; i++)
        {
            VideoEditSegment s = EditSegments[i];
            // Pieces are contiguous in source, so SourceStart is the block's position on the strip.
            SegmentBlocks.Add(new SegmentBlockViewModel(
                i, $"{FormatClock(s.SourceStart)}–{FormatClock(s.SourceEnd)}", s.SourceStart, s.SourceDuration)
            {
                IsKept = s.Kept,
                Speed = s.Speed,
            });
        }

        this.RaisePropertyChanged(nameof(RangeText));
        SegmentLayoutChanged?.Invoke();
    }

    private static string FormatClock(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    // Reconstruct the full source timeline (kept runs + dropped gaps) from a kept-run list over [0,duration].
    private static IReadOnlyList<VideoEditSegment> BuildEditSegments(IReadOnlyList<VideoEditSegment> keptRuns, double duration)
    {
        if (duration <= 0)
        {
            return new[] { new VideoEditSegment(0, 0) };
        }

        var runs = (keptRuns ?? Array.Empty<VideoEditSegment>())
            .Select(r => (Start: Math.Clamp(r.SourceStart, 0, duration), End: Math.Clamp(r.SourceEnd, 0, duration)))
            .Where(r => r.End > r.Start + 0.001)
            .OrderBy(r => r.Start)
            .ToList();

        if (runs.Count == 0)
        {
            return new[] { new VideoEditSegment(0, duration) }; // whole clip kept
        }

        var segs = new List<VideoEditSegment>();
        double cursor = 0;
        foreach ((double start, double end) in runs)
        {
            if (start > cursor + 0.001)
            {
                segs.Add(new VideoEditSegment(cursor, start) { Kept = false }); // dropped gap
            }
            segs.Add(new VideoEditSegment(Math.Max(cursor, start), end) { Kept = true });
            cursor = end;
        }
        if (duration > cursor + 0.001)
        {
            segs.Add(new VideoEditSegment(cursor, duration) { Kept = false });
        }

        return segs;
    }

    /// <summary>Write the kept runs back to the queue item and close.</summary>
    public void Apply()
    {
        _onApply(KeptRuns());
        RequestClose?.Invoke();
    }

    // ── Playback (raw source) ──

    private async Task TogglePlayAsync()
    {
        if (IsPlaying) { Pause(); return; }
        await PlayAsync();
    }

    private Task PlayAsync()
    {
        if (_duration <= 0)
        {
            return Task.CompletedTask;
        }

        _stepCts?.Cancel();
        _playCts?.Cancel();
        _playCts = new CancellationTokenSource();
        CancellationToken ct = _playCts.Token;
        IsPlaying = true;

        double start = PositionSeconds >= _duration - 0.05 ? 0 : PositionSeconds; // replay from start if at end
        PositionSeconds = start;

        _ = Task.Run(async () =>
        {
            try
            {
                using var p = new LibavVideoFramePlayer();
                await p.PlayAsync(_sourcePath, _decodeW, _decodeH, start, 1.0, false, (data, ts) =>
                {
                    Bitmap? bmp = BytesToBitmap(data);
                    Dispatcher.UIThread.Post(() => { Frame = bmp; PositionSeconds = Math.Min(ts, _duration); });
                }, ct);
            }
            catch (OperationCanceledException) { /* paused or reached the clip end */ }
            catch (Exception ex) { AppLog.Error("Compress.TrimPlay", ex); }
            finally { Dispatcher.UIThread.Post(() => IsPlaying = false); }
        }, ct);

        return Task.CompletedTask;
    }

    private void Pause()
    {
        _playCts?.Cancel();
        IsPlaying = false;
    }

    // Slider/strip scrubbing: pause on grab, seek to the dropped position on release.
    public void BeginScrub() => Pause();

    public async Task SeekAsync(double pos)
    {
        Pause();
        await ShowFrameAtAsync(pos);
    }

    private async Task StepAsync(int direction)
    {
        Pause();
        await ShowFrameAtAsync(PositionSeconds + direction * (1.0 / _fps));
    }

    private async Task ShowFrameAtAsync(double pos)
    {
        if (_duration <= 0)
        {
            return;
        }

        pos = Math.Clamp(pos, 0, _duration);
        PositionSeconds = pos;

        _stepCts?.Cancel();
        _stepCts = new CancellationTokenSource();
        CancellationToken ct = _stepCts.Token;

        byte[]? frame;
        try
        {
            frame = await LibavVideoFramePlayer.DecodeFrameAtAsync(_sourcePath, pos, _decodeW, _decodeH, ct);
        }
        catch (Exception ex)
        {
            AppLog.Error("Compress.TrimSeek", ex);
            return;
        }

        if (ct.IsCancellationRequested || frame == null)
        {
            return;
        }

        Frame = BytesToBitmap(frame);
    }

    private Bitmap? BytesToBitmap(byte[] bgra)
    {
        try
        {
            using var sk = new SKBitmap(new SKImageInfo(_decodeW, _decodeH, SKColorType.Bgra8888, SKAlphaType.Premul));
            Marshal.Copy(bgra, 0, sk.GetPixels(), Math.Min(bgra.Length, _decodeW * _decodeH * 4));
            if (!FloatingBitmapConversionHelper.TryCreateDetachedBitmapFromSkBitmap(sk, out Bitmap? bmp, out _) || bmp == null)
            {
                return null;
            }

            if (_rotation == 0)
            {
                return bmp;
            }

            Bitmap? rotated = FloatingBitmapConversionHelper.TransformBitmap(bmp, _rotation, false, false);
            bmp.Dispose();
            return rotated;
        }
        catch (Exception ex)
        {
            AppLog.Error("Compress.TrimFrameBuild", ex);
            return null;
        }
    }

    public void Dispose()
    {
        _playCts?.Cancel();
        _stepCts?.Cancel();
        _playCts?.Dispose();
        _stepCts?.Dispose();
    }
}
