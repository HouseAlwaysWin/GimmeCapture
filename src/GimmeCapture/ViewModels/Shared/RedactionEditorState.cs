using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Rendering;
using ReactiveUI;
using SkiaSharp;

namespace GimmeCapture.ViewModels.Shared;

/// <summary>
/// Composable object-redaction editing state shared by the video editors (the Pin floating video window
/// and the compress 進階影片編輯 editor): the user draws a box around an object at a few playhead positions
/// ("keyframes"); on export the box is interpolated between them and the chosen effect (blur / mosaic /
/// black) is burned into every frame. The host supplies its display size, playhead time, and selection
/// rectangle through constructor delegates, so this state carries no window/view-model coupling —
/// mirrors <see cref="AnnotationEditorState"/>.
/// </summary>
public sealed class RedactionEditorState : ReactiveObject
{
    private readonly Func<(double Width, double Height)> _getDisplaySize;
    private readonly Func<double> _getPlayheadSeconds;
    private readonly Func<Avalonia.Rect> _getSelectionRect;
    private readonly Action _clearSelectionRect;

    // The track new keyframes are appended to. Null until the first keyframe, or after "new object".
    private RedactionTrack? _activeTrack;

    public RedactionEditorState(
        Func<(double Width, double Height)> getDisplaySize,
        Func<double> getPlayheadSeconds,
        Func<Avalonia.Rect> getSelectionRect,
        Action clearSelectionRect,
        IObservable<bool> canAddKeyframe)
    {
        _getDisplaySize = getDisplaySize;
        _getPlayheadSeconds = getPlayheadSeconds;
        _getSelectionRect = getSelectionRect;
        _clearSelectionRect = clearSelectionRect;

        AddKeyframeCommand = ReactiveCommand.Create(AddKeyframe, canAddKeyframe);
        NewObjectCommand = ReactiveCommand.Create(NewObject);
        ClearCommand = ReactiveCommand.Create(Clear);
        CycleEffectCommand = ReactiveCommand.Create(CycleEffect);
    }

    /// <summary>Redaction tracks (one per tracked object) burned into the exported video.</summary>
    public ObservableCollection<RedactionTrack> RedactionTracks { get; } = new();

    /// <summary>
    /// Display-space boxes for every track at the current playhead — the live-preview overlay binds to
    /// this so a just-added keyframe is visible immediately (and the box moves as you scrub/play).
    /// </summary>
    public ObservableCollection<Avalonia.Rect> ActiveRedactionBoxes { get; } = new();

    private RedactionEffect _selectedEffect = RedactionEffect.Blur;
    public RedactionEffect SelectedRedactionEffect
    {
        get => _selectedEffect;
        set => this.RaiseAndSetIfChanged(ref _selectedEffect, value);
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

    /// <summary>Raised after any redaction edit so hosts can re-raise their derived/delegating properties.</summary>
    public event Action? Changed;

    public ReactiveCommand<Unit, Unit> AddKeyframeCommand { get; }
    public ReactiveCommand<Unit, Unit> NewObjectCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearCommand { get; }
    public ReactiveCommand<Unit, Unit> CycleEffectCommand { get; }

    // Snaps the current selection box (display coords) as a keyframe at the playhead, normalized to
    // [0,1] so it is resolution-independent at export. Replaces an existing keyframe at the same time.
    public void AddKeyframe()
    {
        (double dw, double dh) = _getDisplaySize();
        if (dw <= 0 || dh <= 0)
        {
            return;
        }

        Avalonia.Rect r = _getSelectionRect();
        double w = Math.Clamp(r.Width / dw, 0, 1);
        double h = Math.Clamp(r.Height / dh, 0, 1);
        if (w <= 0 || h <= 0)
        {
            return;
        }

        var keyframe = new RedactionKeyframe
        {
            TimeSeconds = _getPlayheadSeconds(),
            X = Math.Clamp(r.X / dw, 0, 1),
            Y = Math.Clamp(r.Y / dh, 0, 1),
            Width = w,
            Height = h,
        };

        if (_activeTrack == null)
        {
            _activeTrack = new RedactionTrack { Effect = SelectedRedactionEffect };
            RedactionTracks.Add(_activeTrack);
        }

        _activeTrack.Keyframes.RemoveAll(k => Math.Abs(k.TimeSeconds - keyframe.TimeSeconds) < 1e-3);
        _activeTrack.Keyframes.Add(keyframe);

        AppLog.Information($"Redaction.Keyframe t={keyframe.TimeSeconds:0.###} effect={_activeTrack.Effect}");
        RaiseRedactionChanged();

        // Clear the selection marquee so the semi-transparent box doesn't linger on screen (it looked like
        // a stuck redaction); the red dashed preview now shows the keyframe. Re-draw for the next keyframe.
        _clearSelectionRect();
    }

    /// <summary>Ends the current object: the next keyframe starts a new track.</summary>
    public void NewObject() => _activeTrack = null;

    public void Clear()
    {
        RedactionTracks.Clear();
        _activeTrack = null;
        RaiseRedactionChanged();
    }

    public void CycleEffect()
    {
        SelectedRedactionEffect = SelectedRedactionEffect switch
        {
            RedactionEffect.Blur => RedactionEffect.Mosaic,
            RedactionEffect.Mosaic => RedactionEffect.SolidBlack,
            _ => RedactionEffect.Blur,
        };

        // Re-style the track currently being authored so the choice takes effect immediately.
        if (_activeTrack != null)
        {
            _activeTrack.Effect = SelectedRedactionEffect;
        }

        RaiseRedactionChanged();
    }

    /// <summary>Restore previously saved tracks (compress items round-trip them across restarts).</summary>
    public void LoadTracks(IEnumerable<RedactionTrack> tracks)
    {
        RedactionTracks.Clear();
        foreach (RedactionTrack track in tracks)
        {
            RedactionTracks.Add(track);
        }

        _activeTrack = null;
        RaiseRedactionChanged();
    }

    private void RaiseRedactionChanged()
    {
        this.RaisePropertyChanged(nameof(HasRedaction));
        this.RaisePropertyChanged(nameof(RedactionStatus));
        RefreshActiveBoxes();
        Changed?.Invoke();
    }

    /// <summary>Recompute the live-preview boxes for the current playhead (call when the playhead moves).</summary>
    public void RefreshActiveBoxes()
    {
        ActiveRedactionBoxes.Clear();
        (double dw, double dh) = _getDisplaySize();
        if (dw <= 0 || dh <= 0)
        {
            return;
        }

        double t = _getPlayheadSeconds();
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
    public Action<SKBitmap, double>? BuildComposite()
    {
        if (!HasRedaction)
        {
            return null;
        }

        List<RedactionTrack> snapshot = RedactionTracks.Where(t => t.Keyframes.Count > 0).ToList();
        return (sk, t) => RedactionRenderer.Render(sk, snapshot, t);
    }
}
