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
/// Drives the standalone "進階影片編輯" window for a Compress queue item: a Pin-style multi-segment timeline
/// (split / merge / keep-drop / per-segment speed) plus crop and rotation. On Apply the whole edit — kept runs
/// (with speed), crop, and rotation — is written back via <c>onApply</c> and applied at batch-encode time.
/// Playback plays the raw source (to find cut points); the strip shows what is kept and any per-piece speed.
/// Reuses the shared VideoSegmentEditor / VideoEditSegment / SegmentBlockViewModel engine (same as the Pin editor).
/// </summary>
internal sealed partial class VideoEditViewModel : ViewModelBase, IDisposable
{
    private readonly string _sourcePath;
    private readonly int _fps;
    private readonly int _decodeW;
    private readonly int _decodeH;
    private readonly double _duration;
    private readonly int _sourceWidth;
    private readonly int _sourceHeight;
    private readonly Action<VideoEditResult> _onApply;

    private CancellationTokenSource? _playCts;
    private CancellationTokenSource? _stepCts;
    private int _playGeneration; // bumped on each (re)start so a superseded play loop's finally can't clear IsPlaying

    internal VideoEditViewModel(
        string sourcePath, double durationSeconds, int fps, int sourceWidth, int sourceHeight,
        VideoEditResult initial, Action<VideoEditResult> onApply)
    {
        _sourcePath = sourcePath;
        _fps = fps > 0 ? fps : 30;
        _rotation = ((initial.RotationDegrees % 360) + 360) % 360;
        _crop = initial.Crop;
        _onApply = onApply;
        _duration = durationSeconds > 0 ? durationSeconds : 0;

        _sourceWidth = sourceWidth > 0 ? sourceWidth : 1280;
        _sourceHeight = sourceHeight > 0 ? sourceHeight : 720;
        double scale = Math.Min(1.0, 720.0 / Math.Max(1, _sourceWidth)); // cap decode width ~720px for responsiveness
        _decodeW = Math.Max(2, (int)(_sourceWidth * scale)); _decodeW -= _decodeW & 1;
        _decodeH = Math.Max(2, (int)(_sourceHeight * scale)); _decodeH -= _decodeH & 1;

        Title = Path.GetFileName(sourcePath);

        PlayPauseCommand = ReactiveCommand.CreateFromTask(TogglePlayAsync);
        StepBackCommand = ReactiveCommand.CreateFromTask(() => StepAsync(-1));
        StepForwardCommand = ReactiveCommand.CreateFromTask(() => StepAsync(1));
        SkipBackCommand = ReactiveCommand.CreateFromTask(() => SeekAsync(PositionSeconds - 5));
        SkipForwardCommand = ReactiveCommand.CreateFromTask(() => SeekAsync(PositionSeconds + 5));
        SplitCommand = ReactiveCommand.Create(Split);
        MergeCommand = ReactiveCommand.Create(Merge);
        CycleSpeedCommand = ReactiveCommand.Create(CycleSpeed);
        RotateCommand = ReactiveCommand.CreateFromTask(RotateAsync);
        ToggleCropCommand = ReactiveCommand.CreateFromTask(ToggleCropAsync);
        ClearCropCommand = ReactiveCommand.CreateFromTask(ClearCropAsync);
        ApplyCommand = ReactiveCommand.Create(Apply);
        CancelCommand = ReactiveCommand.Create(() => RequestClose?.Invoke());
        CompareCommand = ReactiveCommand.CreateFromTask(ToggleCompareAsync);

        PlayPauseCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.EditPlayPause", ex));
        StepBackCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.EditStepBack", ex));
        StepForwardCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.EditStepFwd", ex));
        SkipBackCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.EditSkipBack", ex));
        SkipForwardCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.EditSkipFwd", ex));
        SplitCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.EditSplit", ex));
        MergeCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.EditMerge", ex));
        CycleSpeedCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.EditSpeed", ex));
        RotateCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.EditRotate", ex));
        ToggleCropCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.EditCrop", ex));
        CompareCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.EditCompare", ex));

        InitializeEditing(initial);
        ReplaceSegments(BuildEditSegments(initial.KeptRuns, _duration));
    }

    public string Title { get; }

    /// <summary>Total source duration — the strip's full-width basis (all pieces cover [0,duration]).</summary>
    public double TotalSourceDuration => _duration;

    public int SourceWidth => _sourceWidth;
    public int SourceHeight => _sourceHeight;

    // Timeline strip state (mirrors FloatingVideoViewModel.Segments): the source split into contiguous pieces.
    public ObservableCollection<VideoEditSegment> EditSegments { get; } = new();
    public ObservableCollection<SegmentBlockViewModel> SegmentBlocks { get; } = new();

    /// <summary>True when there is a split boundary to remove (more than one piece) — gates the Merge button.</summary>
    public bool CanMerge => EditSegments.Count > 1;

    /// <summary>Raised after the strip blocks are rebuilt so the window can recompute proportional layout.</summary>
    public event Action? SegmentLayoutChanged;

    /// <summary>Set by the window; invoked to close it from Apply/Cancel.</summary>
    public Action? RequestClose { get; set; }

    /// <summary>Set by the host: builds a <see cref="CompareViewModel"/> for this item with the current output
    /// settings, anchored at the given playhead position. The editor hosts it inline (no separate window).</summary>
    public Func<double, CompareViewModel?>? BuildCompareViewModel { get; set; }

    public ReactiveCommand<Unit, Unit> CompareCommand { get; }

    private string _outputFileName = string.Empty;
    /// <summary>Output file name (no extension; may contain \subfolder\). Written back to the queue item on Apply.</summary>
    public string OutputFileName
    {
        get => _outputFileName;
        set => this.RaiseAndSetIfChanged(ref _outputFileName, value);
    }

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
            double outLen = runs.Sum(r => r.OutputDuration);
            LocalizationService loc = LocalizationService.Instance;
            return $"{runs.Length} {loc["CompressTrimSegments"]}    {loc["CompressTrimLength"]} {Fmt(outLen)}";
        }
    }

    // Timeline clock: seconds to 2 decimals; switches to h:mm:ss once the SOURCE is an hour or longer
    // (keyed to total duration, not the value, so every readout has a consistent shape).
    private string Fmt(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        int centis = (int)Math.Round(t.Milliseconds / 10.0);
        if (centis >= 100)
        {
            centis = 99; // guard the .995+ rounding edge so we never print ".100"
        }

        return _duration >= 3600
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}.{centis:00}"
            : $"{(int)t.TotalMinutes}:{t.Seconds:00}.{centis:00}";
    }

    // ── Rotation ──
    private int _rotation;
    public int RotationDegrees
    {
        get => _rotation;
        private set
        {
            this.RaiseAndSetIfChanged(ref _rotation, value);
            this.RaisePropertyChanged(nameof(RotationText));
            this.RaisePropertyChanged(nameof(HasRotation));
        }
    }

    public string RotationText => $"{_rotation}°";
    public bool HasRotation => _rotation != 0;

    /// <summary>Cycle the output rotation 0 → 90 → 180 → 270 → 0.</summary>
    public void Rotate()
    {
        RotationDegrees = (_rotation + 90) % 360;
        RaiseSurfaceChanged();
    }

    private async Task RotateAsync()
    {
        // Surface-space annotations/redaction don't survive a transform change — confirm, then clear.
        if (!await ConfirmTransformChangeAsync())
        {
            return;
        }

        Rotate();
        await ShowFrameAtAsync(PositionSeconds); // re-render the current frame at the new angle
    }

    // ── Crop ──
    private VideoEditCrop? _crop;
    public VideoEditCrop? Crop
    {
        get => _crop;
        private set
        {
            this.RaiseAndSetIfChanged(ref _crop, value);
            this.RaisePropertyChanged(nameof(HasCrop));
            this.RaisePropertyChanged(nameof(CropText));
        }
    }

    public bool HasCrop => _crop != null;
    public string CropText => _crop != null ? $"{_crop.Width}×{_crop.Height}" : string.Empty;

    private bool _isCropMode;
    /// <summary>When on, the preview shows the un-rotated frame and the overlay captures a crop rectangle.</summary>
    public bool IsCropMode { get => _isCropMode; private set => this.RaiseAndSetIfChanged(ref _isCropMode, value); }

    private async Task ToggleCropAsync()
    {
        IsCropMode = !IsCropMode;
        // Crop is defined in SOURCE pixels (the encoder crops before rotating), so crop mode shows the un-rotated frame.
        await ShowFrameAtAsync(PositionSeconds);
    }

    public async Task ClearCropAsync()
    {
        if (_crop == null || !await ConfirmTransformChangeAsync())
        {
            return;
        }

        Crop = null;
        RaiseSurfaceChanged();
        await ShowFrameAtAsync(PositionSeconds);
    }

    /// <summary>Set the crop from a selection drawn on the un-rotated preview content rect (view maps to content coords).</summary>
    public async Task SetCropFromSelectionAsync(double relX, double relY, double relWidth, double relHeight, double contentWidth, double contentHeight)
    {
        VideoEditCrop? crop = VideoCropMath.SelectionToCrop(
            relX, relY, relWidth, relHeight, contentWidth, contentHeight, _sourceWidth, _sourceHeight);
        if (crop != null && await ConfirmTransformChangeAsync())
        {
            Crop = crop;
            RaiseSurfaceChanged();
        }
        IsCropMode = false;
        await ShowFrameAtAsync(PositionSeconds);
    }

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
    public ReactiveCommand<Unit, Unit> MergeCommand { get; }
    public ReactiveCommand<Unit, Unit> CycleSpeedCommand { get; }
    public ReactiveCommand<Unit, Unit> RotateCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleCropCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearCropCommand { get; }
    public ReactiveCommand<Unit, Unit> ApplyCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    /// <summary>Decodes and shows the frame at the current position. Call once after the window opens.</summary>
    public async Task InitializeAsync()
    {
        IsPreparing = true;
        try { await ShowFrameAtAsync(PositionSeconds); }
        catch (Exception ex) { AppLog.Error("Compress.EditInit", ex); }
        finally { IsPreparing = false; }
    }

    // ── Segment editing (shared engine: VideoSegmentEditor) ──

    private VideoEditSegment[] KeptSegments() => EditSegments.Where(s => s.Kept).ToArray();

    /// <summary>Kept pieces with adjacent contiguous, same-speed ones merged — the runs concatenated on encode.</summary>
    public VideoEditSegment[] KeptRuns() => VideoSegmentEditor.CoalesceContiguous(KeptSegments()).ToArray();

    public void Split()
    {
        if (EditSegments.Count == 0)
        {
            return;
        }

        // Split the piece under the playhead (source time); both halves inherit the Kept flag + speed.
        ReplaceSegments(VideoSegmentEditor.SplitAtSourceTime(EditSegments.ToArray(), PositionSeconds));
        RefreshAfterEdit();
    }

    /// <summary>
    /// Remove the split boundary nearest the playhead, merging the two pieces back into one (the inverse
    /// of Split). The merged piece is kept if either side was — so this "un-cuts" a dropped piece.
    /// </summary>
    public void Merge()
    {
        if (EditSegments.Count < 2)
        {
            return;
        }

        int boundary = VideoSegmentEditor.NearestBoundaryIndex(EditSegments.ToArray(), PositionSeconds);
        if (boundary < 0)
        {
            return;
        }

        // Don't merge across a genuine speed seam — flattening two different-speed pieces into one would
        // silently discard a speed edit. Only cut-artifact boundaries (equal speed on both sides) coalesce.
        if (Math.Abs(EditSegments[boundary].Speed - EditSegments[boundary + 1].Speed) > 0.001)
        {
            return;
        }

        ReplaceSegments(VideoSegmentEditor.MergeAt(EditSegments.ToArray(), boundary));
        RefreshAfterEdit();
    }

    /// <summary>Cycle the speed (0.5 → 1 → 1.5 → 2 → 0.5) of the piece under the playhead.</summary>
    public void CycleSpeed()
    {
        if (EditSegments.Count == 0)
        {
            return;
        }

        int index = VideoSegmentEditor.IndexForSourceTime(EditSegments.ToArray(), PositionSeconds);
        if (index < 0 || index >= EditSegments.Count)
        {
            return;
        }

        VideoEditSegment seg = EditSegments[index];
        double next = seg.Speed switch
        {
            < 0.99 => 1.0,   // 0.5 → 1
            < 1.49 => 1.5,   // 1   → 1.5
            < 1.99 => 2.0,   // 1.5 → 2
            _ => 0.5,        // 2   → 0.5
        };
        EditSegments[index] = seg with { Speed = next };
        RebuildSegmentBlocks();
        RefreshAfterEdit();
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
        RefreshAfterEdit();
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
                i, $"{Fmt(s.SourceStart)}–{Fmt(s.SourceEnd)}", s.SourceStart, s.SourceDuration)
            {
                IsKept = s.Kept,
                Speed = s.Speed,
            });
        }

        this.RaisePropertyChanged(nameof(RangeText));
        this.RaisePropertyChanged(nameof(CanMerge));
        SegmentLayoutChanged?.Invoke();
    }


    // Reconstruct the full source timeline (kept runs + dropped gaps) from a kept-run list over [0,duration].
    private static IReadOnlyList<VideoEditSegment> BuildEditSegments(IReadOnlyList<VideoEditSegment> keptRuns, double duration)
    {
        if (duration <= 0)
        {
            return new[] { new VideoEditSegment(0, 0) };
        }

        var runs = (keptRuns ?? Array.Empty<VideoEditSegment>())
            .Select(r => (Start: Math.Clamp(r.SourceStart, 0, duration), End: Math.Clamp(r.SourceEnd, 0, duration),
                Speed: r.Speed > 0 ? r.Speed : 1.0))
            .Where(r => r.End > r.Start + 0.001)
            .OrderBy(r => r.Start)
            .ToList();

        if (runs.Count == 0)
        {
            return new[] { new VideoEditSegment(0, duration) }; // whole clip kept
        }

        var segs = new List<VideoEditSegment>();
        double cursor = 0;
        foreach ((double start, double end, double speed) in runs)
        {
            if (start > cursor + 0.001)
            {
                segs.Add(new VideoEditSegment(cursor, start) { Kept = false }); // dropped gap
            }
            segs.Add(new VideoEditSegment(Math.Max(cursor, start), end, speed) { Kept = true });
            cursor = end;
        }
        if (duration > cursor + 0.001)
        {
            segs.Add(new VideoEditSegment(cursor, duration) { Kept = false });
        }

        return segs;
    }

    /// <summary>Write the full edit (kept runs, crop, rotation, annotations, redaction) back and close.</summary>
    public void Apply()
    {
        _onApply(new VideoEditResult(
            KeptRuns(), _crop, _rotation,
            EditorState.Annotations.ToList(), SurfaceWidth, SurfaceHeight,
            Redaction.RedactionTracks.Where(t => t.Keyframes.Count > 0).ToList()));
        RequestClose?.Invoke();
    }

    // ── Playback (raw source) ──

    private async Task TogglePlayAsync()
    {
        if (IsPlaying) { Pause(); return; }
        await PlayAsync();
    }

    // Output-aware playback: play each KEPT run at its own speed, skipping the dropped gaps — so the preview
    // reflects the actual edit (2× plays in half the wall-clock time; cut pieces are skipped). Frames are
    // cropped/rotated by BytesToBitmap to match the output. Video-only (the editor has no audio preview).
    private Task PlayAsync()
    {
        if (_duration <= 0)
        {
            return Task.CompletedTask;
        }

        VideoEditSegment[] runs = KeptRuns();
        if (runs.Length == 0)
        {
            return Task.CompletedTask;
        }

        _stepCts?.Cancel();
        _playCts?.Cancel();
        _playCts = new CancellationTokenSource();
        CancellationToken ct = _playCts.Token;
        int gen = ++_playGeneration;
        IsPlaying = true;

        // Resume from the playhead when it sits inside a kept run; at the end (or in a gap past everything) restart.
        double pos = PositionSeconds >= _duration - 0.05 ? runs[0].SourceStart : PositionSeconds;
        int startIdx = runs.Length;
        double startWithin = runs[0].SourceStart;
        for (int i = 0; i < runs.Length; i++)
        {
            if (runs[i].SourceEnd > pos + 0.0005)
            {
                startIdx = i;
                startWithin = Math.Max(pos, runs[i].SourceStart);
                break;
            }
        }
        if (startIdx >= runs.Length)
        {
            startIdx = 0;
            startWithin = runs[0].SourceStart;
        }
        PositionSeconds = startWithin;

        _ = Task.Run(async () =>
        {
            try
            {
                bool firstLap = true;
                do
                {
                    int lapStartIdx = firstLap ? startIdx : 0;
                    for (int i = lapStartIdx; i < runs.Length && !ct.IsCancellationRequested; i++)
                    {
                        VideoEditSegment run = runs[i];
                        double from = firstLap && i == startIdx ? startWithin : run.SourceStart;
                        double pieceSpeed = run.Speed > 0 ? run.Speed : 1.0;
                        double speed = pieceSpeed * _playbackSpeed; // per-run × global preview speed
                        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        using var p = new LibavVideoFramePlayer();

                        // (Re)start preview audio at this run's start, retimed to the effective speed.
                        if (!IsMuted)
                        {
                            _audioPreview.Start(_sourcePath, from, speed);
                        }

                        try
                        {
                            await p.PlayAsync(_sourcePath, _decodeW, _decodeH, from, speed, false, (data, ts) =>
                            {
                                if (ts >= run.SourceEnd - 0.0005)
                                {
                                    runCts.Cancel(); // reached this kept run's end → advance to the next kept run
                                    return;
                                }
                                Bitmap? bmp = BytesToBitmap(data);
                                Dispatcher.UIThread.Post(() =>
                                {
                                    if (gen != _playGeneration)
                                    {
                                        return; // a superseded/cancelled loop must not overwrite the current frame/playhead
                                    }
                                    Frame = bmp;
                                    PositionSeconds = Math.Min(ts, _duration);
                                });
                            }, runCts.Token);
                        }
                        catch (OperationCanceledException) { /* run end or paused */ }
                    }

                    firstLap = false;
                } while (IsLooping && !ct.IsCancellationRequested);
            }
            catch (Exception ex) { AppLog.Error("Compress.EditPlay", ex); }
            finally
            {
                _audioPreview.Stop();
                Dispatcher.UIThread.Post(() => { if (gen == _playGeneration) IsPlaying = false; });
            }
        }, ct);

        return Task.CompletedTask;
    }

    private void Pause()
    {
        _playCts?.Cancel();
        _audioPreview.Stop();
        IsPlaying = false;
    }

    // After an edit that changes the segments/speed, reflect it immediately: restart playback from the current
    // position if playing, otherwise re-render the still frame. (Crop/rotation apply live and refresh themselves.)
    private void RefreshAfterEdit()
    {
        if (IsPlaying)
        {
            _ = PlayAsync(); // re-captures the kept runs + speeds from the current position
        }
        else
        {
            _ = ShowFrameAtAsync(PositionSeconds);
        }
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
            AppLog.Error("Compress.EditSeek", ex);
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

            // Outside crop mode, show the cropped region (what the output will be); in crop mode show the whole frame.
            SKBitmap source = sk;
            SKBitmap? cropped = null;
            if (!IsCropMode && _crop != null)
            {
                cropped = CropSkBitmap(sk, _crop);
                if (cropped != null)
                {
                    source = cropped;
                }
            }

            bool ok = FloatingBitmapConversionHelper.TryCreateDetachedBitmapFromSkBitmap(source, out Bitmap? bmp, out _);
            cropped?.Dispose();
            if (!ok || bmp == null)
            {
                return null;
            }

            // Crop mode shows the source orientation (crop coords map to source pixels); otherwise apply the rotation.
            int rot = IsCropMode ? 0 : _rotation;
            if (rot == 0)
            {
                return bmp;
            }

            Bitmap? rotated = FloatingBitmapConversionHelper.TransformBitmap(bmp, rot, false, false);
            bmp.Dispose();
            return rotated;
        }
        catch (Exception ex)
        {
            AppLog.Error("Compress.EditFrameBuild", ex);
            return null;
        }
    }

    // Deep-copy the crop region (source pixels scaled into decode space) out of the decoded frame for the preview.
    private SKBitmap? CropSkBitmap(SKBitmap src, VideoEditCrop crop)
    {
        double sx = _decodeW / (double)_sourceWidth;
        double sy = _decodeH / (double)_sourceHeight;
        int x = Math.Clamp((int)Math.Round(crop.X * sx), 0, _decodeW - 2);
        int y = Math.Clamp((int)Math.Round(crop.Y * sy), 0, _decodeH - 2);
        int w = Math.Clamp((int)Math.Round(crop.Width * sx), 2, _decodeW - x);
        int h = Math.Clamp((int)Math.Round(crop.Height * sy), 2, _decodeH - y);
        var dst = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(dst))
        {
            canvas.DrawBitmap(src, new SKRect(x, y, x + w, y + h), new SKRect(0, 0, w, h));
        }
        return dst;
    }

    public void Dispose()
    {
        _playCts?.Cancel();
        _stepCts?.Cancel();
        _playCts?.Dispose();
        _stepCts?.Dispose();
        _audioPreview.Dispose();
        Draw.Dispose();
        Compare?.Dispose(); // stops the inline compare's playback + deletes its temp sample, if still open
    }
}
