using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Media;
using ReactiveUI;

namespace GimmeCapture.ViewModels.Floating;

public partial class FloatingVideoViewModel
{
    // ── Timeline edit state ──
    // EditSegments holds the SOURCE timeline split into contiguous pieces (always covering [0,total]).
    // Each piece carries a Kept flag; the OUTPUT is the kept pieces joined. The edit applies whenever a
    // piece is dropped — INDEPENDENT of IsTimelineMode (which only shows/hides the strip), so cuts still
    // export after the panel is collapsed.
    public ObservableCollection<VideoEditSegment> EditSegments { get; } = new();

    private bool _isTimelineMode;
    public bool IsTimelineMode
    {
        get => _isTimelineMode;
        set
        {
            this.RaiseAndSetIfChanged(ref _isTimelineMode, value);
            this.RaisePropertyChanged(nameof(WindowPadding));
        }
    }

    /// <summary>Timeline strip blocks (one per source piece), rebuilt whenever the pieces change.</summary>
    public ObservableCollection<SegmentBlockViewModel> SegmentBlocks { get; } = new();

    /// <summary>Raised after the strip blocks are rebuilt so the view can recompute proportional layout.</summary>
    public event Action? SegmentLayoutChanged;

    public string TimelineHint => LocalizationService.Instance["TimelineHint"]
        ?? "Drag = scrub · Split · tap a piece to keep/drop";

    // The kept pieces = the output. Filtering happens here so the pure compiler stays Kept-agnostic.
    private VideoEditSegment[] KeptSegments() => EditSegments.Where(s => s.Kept).ToArray();

    // Kept pieces with adjacent contiguous ones merged into single runs. Dropping only the head/tail
    // leaves one continuous run → the fast single-trim export path; a real gap stays as 2+ runs → concat.
    private VideoEditSegment[] KeptRuns() => VideoSegmentEditor.CoalesceContiguous(KeptSegments()).ToArray();

    /// <summary>True once the kept pieces form more than one run (route export through the concat compiler).</summary>
    private bool UseMultiSegment => KeptRuns().Length > 1;

    /// <summary>True when ≥1 piece is dropped — the only thing that makes the output differ from the source.</summary>
    private bool AnyPieceDropped => EditSegments.Any(s => !s.Kept);

    /// <summary>True when any kept piece has a non-1× speed, so the output must be re-encoded.</summary>
    private bool AnySpeedChanged => KeptSegments().Any(s => Math.Abs(s.Speed - 1.0) > 0.001);

    /// <summary>The edit changes the output (a cut, speed change, or redaction) → route through the in-process exporter.</summary>
    private bool EditChangesOutput => AnyPieceDropped || AnySpeedChanged || HasRedaction;

    // Cached audio-stream presence (probed once); the compiler's audio chain needs to know.
    private bool? _sourceHasAudio;

    public string TimelineTooltip => LocalizationService.Instance["TipTimeline"] ?? "Timeline (cut)";
    public string SplitTooltip => LocalizationService.Instance["TipSplitSegment"] ?? "Split at playhead";
    public string MergeSegmentTooltip => LocalizationService.Instance["TipMergeSegment"] ?? "Merge at playhead (undo split)";
    public string PieceSpeedTooltip => LocalizationService.Instance["TipPieceSpeed"] ?? "Cycle speed of the piece at the playhead";

    public ReactiveCommand<Unit, Unit> ToggleTimelineCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> SplitSegmentCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> MergeSegmentCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> CyclePieceSpeedCommand { get; private set; } = null!;

    private void InitializeSegmentCommands()
    {
        ToggleTimelineCommand = ReactiveCommand.Create(() =>
        {
            bool activate = !IsTimelineMode;
            if (activate)
            {
                // Timeline is mutually exclusive with the simple drawing tools.
                CurrentTool = FloatingTool.None;
                CurrentAnnotationTool = AnnotationType.None;
                SeedSegmentsFromTrim();
            }

            IsTimelineMode = activate;
        });

        SplitSegmentCommand = ReactiveCommand.Create(() =>
        {
            if (EditSegments.Count == 0)
            {
                return;
            }

            // Split the piece under the playhead (source time); both halves inherit the Kept flag.
            ReplaceSegments(VideoSegmentEditor.SplitAtSourceTime(EditSegments.ToArray(), _currentTime.TotalSeconds));
        });

        // Remove the split boundary nearest the playhead, merging the two pieces back into one (inverse of split).
        MergeSegmentCommand = ReactiveCommand.Create(() =>
        {
            if (EditSegments.Count < 2)
            {
                return;
            }

            int boundary = VideoSegmentEditor.NearestBoundaryIndex(EditSegments.ToArray(), _currentTime.TotalSeconds);
            if (boundary < 0)
            {
                return;
            }

            // Don't merge across a genuine speed seam — that would silently discard one piece's speed.
            if (Math.Abs(EditSegments[boundary].Speed - EditSegments[boundary + 1].Speed) > 0.001)
            {
                return;
            }

            ReplaceSegments(VideoSegmentEditor.MergeAt(EditSegments.ToArray(), boundary));
        });

        // Cycle the speed (1 → 1.5 → 2 → 0.5 → 1) of the piece currently under the playhead.
        CyclePieceSpeedCommand = ReactiveCommand.Create(() =>
        {
            if (EditSegments.Count == 0)
            {
                return;
            }

            int index = VideoSegmentEditor.IndexForSourceTime(EditSegments.ToArray(), _currentTime.TotalSeconds);
            if (index < 0 || index >= EditSegments.Count)
            {
                return;
            }

            VideoEditSegment seg = EditSegments[index];
            double next = seg.Speed switch
            {
                < 0.99 => 1.0,        // 0.5 → 1
                < 1.49 => 1.5,        // 1   → 1.5
                < 1.99 => 2.0,        // 1.5 → 2
                _ => 0.5,             // 2   → 0.5
            };

            EditSegments[index] = seg with { Speed = next };
            RebuildSegmentBlocks();
        });
    }

    /// <summary>
    /// Toggles whether the piece at <paramref name="index"/> is kept in the output (tap on the strip).
    /// Always leaves at least one kept piece.
    /// </summary>
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

    /// <summary>Seeds the strip with the whole clip once (one kept piece covering [0,total]).</summary>
    private void SeedSegmentsFromTrim()
    {
        // Reseed when empty, or when the only piece is the degenerate one seeded before the duration was
        // known (a single zero/negative-length block). Never wipe a real edit.
        if (EditSegments.Count > 0 && !IsSingleDegenerateSegment())
        {
            return;
        }

        double total = _totalDuration.TotalSeconds;
        ReplaceSegments(VideoSegmentEditor.FromTrim(0, total, total));
    }

    // The single kept run's source range (used by the fast single-piece export path); (0,total) otherwise.
    private (double Start, double End) SingleSegmentSourceRange()
    {
        VideoEditSegment[] runs = KeptRuns();
        if (runs.Length == 1)
        {
            return (runs[0].SourceStart, runs[0].SourceEnd);
        }

        return (0, _totalDuration.TotalSeconds);
    }

    // True when the kept set is a single run that is a sub-range of the clip (a plain trim → fast -ss/-to copy).
    private bool SingleSegmentIsTrimmed
    {
        get
        {
            VideoEditSegment[] runs = KeptRuns();
            if (runs.Length != 1)
            {
                return false;
            }

            double total = _totalDuration.TotalSeconds;
            return runs[0].SourceStart > 0.001 || (total > 0 && runs[0].SourceEnd < total - 0.001);
        }
    }

    // True when the list holds exactly one block whose end is at/under its start, or runs past the
    // (now-known) duration — i.e. it was auto-seeded before the real length arrived.
    private bool IsSingleDegenerateSegment()
    {
        if (EditSegments.Count != 1)
        {
            return false;
        }

        VideoEditSegment only = EditSegments[0];
        double total = _totalDuration.TotalSeconds;
        return only.SourceEnd <= only.SourceStart + 0.001
            || (total > 0 && only.SourceEnd > total + 0.001);
    }

    /// <summary>
    /// Repairs the auto-seeded full-clip piece once the duration is known. Called from the TotalDuration
    /// setter (marshaled to the UI thread). No-op unless the strip holds a single degenerate block.
    /// </summary>
    private void RepairFullClipSegmentForDuration()
    {
        if (_isDisposed || _totalDuration.TotalSeconds <= 0 || !IsSingleDegenerateSegment())
        {
            return;
        }

        ReplaceSegments(VideoSegmentEditor.FromTrim(0, _totalDuration.TotalSeconds, _totalDuration.TotalSeconds));
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
                i,
                $"{FormatClock(s.SourceStart)}–{FormatClock(s.SourceEnd)}",
                s.SourceStart,
                s.SourceDuration)
            {
                IsKept = s.Kept,
                Speed = s.Speed,
            });
        }

        SegmentLayoutChanged?.Invoke();
    }

    /// <summary>Total source duration across all pieces — the strip's full width basis.</summary>
    public double TotalOutputDuration
    {
        get
        {
            double total = 0;
            foreach (VideoEditSegment s in EditSegments)
            {
                total += s.SourceDuration;
            }

            return total;
        }
    }

    private static string FormatClock(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    /// <summary>Builds the declarative edit from the KEPT pieces (Phase 1: cuts + optional crop).</summary>
    private VideoEditProject BuildEditProject(VideoEditCrop? crop)
    {
        VideoEditSegment[] runs = KeptRuns();
        IReadOnlyList<VideoEditSegment> segments = runs.Length > 0
            ? runs
            : VideoSegmentEditor.FromTrim(0, _totalDuration.TotalSeconds, _totalDuration.TotalSeconds);

        return new VideoEditProject
        {
            Segments = segments,
            Crop = crop,
            TotalSourceDuration = _totalDuration.TotalSeconds,
        };
    }

    private async Task<bool> EnsureSourceHasAudioAsync()
    {
        if (_sourceHasAudio.HasValue)
        {
            return _sourceHasAudio.Value;
        }

        try
        {
            _sourceHasAudio = await _nativeFramePlayer.ProbeHasAudioAsync(VideoPath).ConfigureAwait(false);
        }
        catch
        {
            _sourceHasAudio = false; // safer than a failing atrim on a non-existent [0:a]
        }

        return _sourceHasAudio.Value;
    }
}
