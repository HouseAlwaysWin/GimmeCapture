using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.AI;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Rendering;
using ReactiveUI;
using SkiaSharp;

namespace GimmeCapture.ViewModels.Floating;

// Tier-2a object redaction: the user draws a box around an object at a few playhead positions
// ("keyframes"); on export the box is interpolated between them and the chosen effect (blur / mosaic /
// black) is burned into every frame. Authoring uses the existing selection rectangle (no SAM2 wiring).
public partial class FloatingVideoViewModel
{
    /// <summary>Redaction tracks (one per tracked object) burned into the exported video.</summary>
    public ObservableCollection<RedactionTrack> RedactionTracks { get; } = new();

    // The track new keyframes are appended to. Null until the first keyframe, or after "new object".
    private RedactionTrack? _activeRedactionTrack;

    private RedactionEffect _selectedRedactionEffect = RedactionEffect.Blur;
    public RedactionEffect SelectedRedactionEffect
    {
        get => _selectedRedactionEffect;
        set => this.RaiseAndSetIfChanged(ref _selectedRedactionEffect, value);
    }

    /// <summary>True when at least one track has a keyframe (i.e. export must burn redaction in).</summary>
    public bool HasRedaction => RedactionTracks.Any(t => t.Keyframes.Count > 0);

    /// <summary>Short human summary for the toolbar tooltip / status.</summary>
    public string RedactionStatus
    {
        get
        {
            int objects = RedactionTracks.Count(t => t.Keyframes.Count > 0);
            int keyframes = RedactionTracks.Sum(t => t.Keyframes.Count);
            return $"{objects} object(s), {keyframes} keyframe(s) — {SelectedRedactionEffect}";
        }
    }

    public ReactiveCommand<Unit, Unit> AddRedactionKeyframeCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> NewRedactionObjectCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ClearRedactionCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> CycleRedactionEffectCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> TrackObjectCommand { get; private set; } = null!;

    private void InitializeRedactionCommands()
    {
        var canAdd = this.WhenAnyValue(x => x.IsSelectionActive);
        AddRedactionKeyframeCommand = ReactiveCommand.Create(AddRedactionKeyframe, canAdd);
        NewRedactionObjectCommand = ReactiveCommand.Create(() => { _activeRedactionTrack = null; });
        ClearRedactionCommand = ReactiveCommand.Create(ClearRedaction);
        CycleRedactionEffectCommand = ReactiveCommand.Create(CycleRedactionEffect);
        TrackObjectCommand = ReactiveCommand.CreateFromTask(TrackObjectAsync, canAdd);
    }

    /// <summary>
    /// Auto-tracks the object under the current selection box across the clip using SAM2 (greedy per-frame
    /// re-segmentation seeded from the previous box). Builds a new redaction track from the sampled boxes;
    /// the exporter/preview interpolation fills between samples. Requires SAM2 (and a drawn selection).
    /// </summary>
    private async Task TrackObjectAsync()
    {
        var runtime = _sam2RuntimeService;
        double dw = DisplayWidth, dh = DisplayHeight;
        if (!IsSelectionActive || string.IsNullOrEmpty(VideoPath) || dw <= 0 || dh <= 0)
        {
            return;
        }

        if (runtime == null || _appSettingsService == null)
        {
            ProcessingText = LocalizationService.Instance["StatusSAM2NotFound"] ?? "SAM2 not available";
            return;
        }

        var r = SelectionRect;
        double seedX = Math.Clamp((r.X + (r.Width / 2)) / dw, 0, 1);
        double seedY = Math.Clamp((r.Y + (r.Height / 2)) / dh, 0, 1);
        int fw = Math.Max(2, (int)Math.Round(OriginalWidth));
        int fh = Math.Max(2, (int)Math.Round(OriginalHeight));
        double start = Math.Max(0, CurrentTime.TotalSeconds);
        double end = _totalDuration.TotalSeconds;
        if (end <= start)
        {
            return;
        }

        IsProcessing = true;
        ProcessingText = LocalizationService.Instance["StatusInitializingAI"] ?? "Tracking…";
        string? leaseId = null;
        SAM2Service? sam2 = null;
        try
        {
            leaseId = runtime.AcquireLease();
            sam2 = new SAM2Service(runtime, _appSettingsService);
            await sam2.InitializeAsync();

            string label = LocalizationService.Instance["StatusAIDetecting"] ?? "Tracking";
            var progress = new Progress<double>(p => ProcessingText = $"{label} {p * 100:0}%");

            SAM2Service sam2Local = sam2;
            List<RedactionKeyframe> keyframes = await Task.Run(() => Sam2RedactionTracker.TrackAsync(
                sam2Local, VideoPath, fw, fh, start, end, seedX, seedY, 0.3, progress, default));

            if (keyframes.Count > 0)
            {
                var track = new RedactionTrack { Effect = SelectedRedactionEffect };
                track.Keyframes.AddRange(keyframes);
                RedactionTracks.Add(track);
                _activeRedactionTrack = track;
                AppLog.Information($"FloatingVideo.TrackObject added {keyframes.Count} keyframes start={start:0.##} end={end:0.##}");
                RaiseRedactionChanged();
                SelectionRect = new Avalonia.Rect(); // clear the seed box now that the track exists
            }
            else
            {
                ProcessingText = LocalizationService.Instance["StatusSAM2NotFound"] ?? "No object tracked";
                await Task.Delay(1200);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("FloatingVideo.TrackObject", ex);
            ProcessingText = LocalizationService.Instance["StatusSAM2NotFound"] ?? "SAM2 not ready";
            await Task.Delay(1500);
        }
        finally
        {
            sam2?.Dispose();
            if (leaseId != null)
            {
                runtime.ReleaseLease(leaseId, true);
            }

            IsProcessing = false;
        }
    }

    // Snaps the current selection box (display coords) as a keyframe at the playhead, normalized to
    // [0,1] so it is resolution-independent at export. Replaces an existing keyframe at the same time.
    private void AddRedactionKeyframe()
    {
        double dw = DisplayWidth, dh = DisplayHeight;
        if (!IsSelectionActive || dw <= 0 || dh <= 0)
        {
            return;
        }

        var r = SelectionRect;
        double w = Math.Clamp(r.Width / dw, 0, 1);
        double h = Math.Clamp(r.Height / dh, 0, 1);
        if (w <= 0 || h <= 0)
        {
            return;
        }

        var keyframe = new RedactionKeyframe
        {
            TimeSeconds = CurrentTime.TotalSeconds,
            X = Math.Clamp(r.X / dw, 0, 1),
            Y = Math.Clamp(r.Y / dh, 0, 1),
            Width = w,
            Height = h,
        };

        if (_activeRedactionTrack == null)
        {
            _activeRedactionTrack = new RedactionTrack { Effect = SelectedRedactionEffect };
            RedactionTracks.Add(_activeRedactionTrack);
        }

        _activeRedactionTrack.Keyframes.RemoveAll(k => Math.Abs(k.TimeSeconds - keyframe.TimeSeconds) < 1e-3);
        _activeRedactionTrack.Keyframes.Add(keyframe);

        AppLog.Information($"FloatingVideo.RedactionKeyframe t={keyframe.TimeSeconds:0.###} effect={_activeRedactionTrack.Effect}");
        RaiseRedactionChanged();
    }

    private void ClearRedaction()
    {
        RedactionTracks.Clear();
        _activeRedactionTrack = null;
        RaiseRedactionChanged();
    }

    private void CycleRedactionEffect()
    {
        SelectedRedactionEffect = SelectedRedactionEffect switch
        {
            RedactionEffect.Blur => RedactionEffect.Mosaic,
            RedactionEffect.Mosaic => RedactionEffect.SolidBlack,
            _ => RedactionEffect.Blur,
        };

        // Re-style the track currently being authored so the choice takes effect immediately.
        if (_activeRedactionTrack != null)
        {
            _activeRedactionTrack.Effect = SelectedRedactionEffect;
        }

        RaiseRedactionChanged();
    }

    private void RaiseRedactionChanged()
    {
        this.RaisePropertyChanged(nameof(HasRedaction));
        this.RaisePropertyChanged(nameof(RedactionStatus));
        RefreshActiveRedactionBoxes();
    }

    /// <summary>
    /// Display-space boxes for every track at the current playhead — the live-preview overlay binds to
    /// this so a just-added keyframe is visible immediately (and the box moves as you scrub/play).
    /// </summary>
    public ObservableCollection<Avalonia.Rect> ActiveRedactionBoxes { get; } = new();

    internal void RefreshActiveRedactionBoxes()
    {
        ActiveRedactionBoxes.Clear();
        double dw = DisplayWidth, dh = DisplayHeight;
        if (dw <= 0 || dh <= 0)
        {
            return;
        }

        double t = CurrentTime.TotalSeconds;
        foreach (RedactionTrack track in RedactionTracks)
        {
            RedactionBox? box = RedactionInterpolator.EvaluateAt(track, t);
            if (box is { } b)
            {
                ActiveRedactionBoxes.Add(new Avalonia.Rect(b.X * dw, b.Y * dh, b.Width * dw, b.Height * dh));
            }
        }
    }

    /// <summary>
    /// A composite hook that burns the current redaction tracks into each frame at its source time,
    /// or null when there is nothing to redact. The tracks are snapshotted so the export worker thread
    /// is not affected by later edits.
    /// </summary>
    private Action<SKBitmap, double>? BuildRedactionComposite()
    {
        if (!HasRedaction)
        {
            return null;
        }

        List<RedactionTrack> snapshot = RedactionTracks.Where(t => t.Keyframes.Count > 0).ToList();
        return (sk, t) => RedactionRenderer.Render(sk, snapshot, t);
    }
}
