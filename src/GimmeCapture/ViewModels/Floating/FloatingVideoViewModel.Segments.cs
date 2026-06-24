using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media;
using ReactiveUI;

namespace GimmeCapture.ViewModels.Floating;

public partial class FloatingVideoViewModel
{
    // ── Multi-segment (timeline) edit state ──
    // EditSegments is the source of truth ONLY in timeline mode. While empty (the default), the pin
    // behaves exactly as before — the single-segment export/preview paths are untouched.
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

    private int _selectedSegmentIndex = -1;
    public int SelectedSegmentIndex
    {
        get => _selectedSegmentIndex;
        set => this.RaiseAndSetIfChanged(ref _selectedSegmentIndex, value);
    }

    /// <summary>True once the user has cut the clip into more than one kept segment.</summary>
    private bool UseMultiSegment => IsTimelineMode && EditSegments.Count > 1;

    // Cached audio-stream presence (probed once); the compiler's audio chain needs to know.
    private bool? _sourceHasAudio;

    public ReactiveCommand<Unit, Unit> ToggleTimelineCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> SplitSegmentCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> DeleteSegmentCommand { get; private set; } = null!;

    private void InitializeSegmentCommands()
    {
        ToggleTimelineCommand = ReactiveCommand.Create(() =>
        {
            bool activate = !IsTimelineMode;
            if (activate)
            {
                // Timeline is mutually exclusive with the simple tools/trim, like the trim toggle.
                CurrentTool = FloatingTool.None;
                CurrentAnnotationTool = AnnotationType.None;
                IsTrimmingMode = false;
                SeedSegmentsFromTrim();
            }

            IsTimelineMode = activate;
        });

        SplitSegmentCommand = ReactiveCommand.Create(() =>
        {
            if (!IsTimelineMode || EditSegments.Count == 0)
            {
                return;
            }

            // The player tracks the SOURCE position, so split at the source time under the playhead.
            ReplaceSegments(VideoSegmentEditor.SplitAtSourceTime(EditSegments.ToArray(), _currentTime.TotalSeconds));
        });

        DeleteSegmentCommand = ReactiveCommand.Create(() =>
        {
            if (!IsTimelineMode || SelectedSegmentIndex < 0 || SelectedSegmentIndex >= EditSegments.Count)
            {
                return;
            }

            int removed = SelectedSegmentIndex;
            ReplaceSegments(VideoSegmentEditor.RemoveAt(EditSegments.ToArray(), removed));
            SelectedSegmentIndex = Math.Min(removed, EditSegments.Count - 1);
        });
    }

    /// <summary>Seeds the segment list from the current trim range (or the whole clip) once.</summary>
    private void SeedSegmentsFromTrim()
    {
        if (EditSegments.Count > 0)
        {
            return;
        }

        double start = IsTrimmingMode ? TrimStartSeconds : 0;
        double end = IsTrimmingMode && TrimEndSeconds > 0 ? TrimEndSeconds : _totalDuration.TotalSeconds;
        ReplaceSegments(VideoSegmentEditor.FromTrim(start, end, _totalDuration.TotalSeconds));
    }

    private void ReplaceSegments(IReadOnlyList<VideoEditSegment> segments)
    {
        EditSegments.Clear();
        foreach (VideoEditSegment s in segments)
        {
            EditSegments.Add(s);
        }
    }

    /// <summary>Builds the declarative edit from the current segment list (Phase 1: cuts + optional crop).</summary>
    private VideoEditProject BuildEditProject(VideoEditCrop? crop)
    {
        IReadOnlyList<VideoEditSegment> segments = EditSegments.Count > 0
            ? EditSegments.ToArray()
            : VideoSegmentEditor.FromTrim(
                IsTrimmingMode ? TrimStartSeconds : 0,
                IsTrimmingMode && TrimEndSeconds > 0 ? TrimEndSeconds : _totalDuration.TotalSeconds,
                _totalDuration.TotalSeconds);

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
