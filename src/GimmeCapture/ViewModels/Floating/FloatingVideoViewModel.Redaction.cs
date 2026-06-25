using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using GimmeCapture.Models;
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

    private void InitializeRedactionCommands()
    {
        var canAdd = this.WhenAnyValue(x => x.IsSelectionActive);
        AddRedactionKeyframeCommand = ReactiveCommand.Create(AddRedactionKeyframe, canAdd);
        NewRedactionObjectCommand = ReactiveCommand.Create(() => { _activeRedactionTrack = null; });
        ClearRedactionCommand = ReactiveCommand.Create(ClearRedaction);
        CycleRedactionEffectCommand = ReactiveCommand.Create(CycleRedactionEffect);
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
