using System;
using System.Collections.ObjectModel;
using System.Reactive;
using GimmeCapture.Models;
using GimmeCapture.ViewModels.Shared;
using ReactiveUI;
using SkiaSharp;

namespace GimmeCapture.ViewModels.Floating;

// Tier-2a object redaction: the user draws a box around an object at a few playhead positions
// ("keyframes"); on export the box is interpolated between them and the chosen effect (blur / mosaic /
// black) is burned into every frame. Authoring uses the existing selection rectangle (no SAM2 wiring).
// The editing logic lives in the shared RedactionEditorState (also used by the compress 進階影片編輯
// editor); this partial is pure delegation so the Pin window's public surface and XAML bindings are
// unchanged.
public partial class FloatingVideoViewModel
{
    private RedactionEditorState _redaction = null!;

    /// <summary>Redaction tracks (one per tracked object) burned into the exported video.</summary>
    public ObservableCollection<RedactionTrack> RedactionTracks => _redaction.RedactionTracks;

    public RedactionEffect SelectedRedactionEffect
    {
        get => _redaction.SelectedRedactionEffect;
        set => _redaction.SelectedRedactionEffect = value;
    }

    /// <summary>The selectable redaction effects, for the effect dropdown.</summary>
    public System.Collections.Generic.IReadOnlyList<RedactionEffect> AvailableRedactionEffects => _redaction.AvailableEffects;

    /// <summary>True when at least one track has a keyframe (i.e. export must burn redaction in).</summary>
    public bool HasRedaction => _redaction.HasRedaction;

    /// <summary>Short human summary for the toolbar tooltip / status.</summary>
    public string RedactionStatus => _redaction.RedactionStatus;

    /// <summary>
    /// Display-space boxes for every track at the current playhead — the live-preview overlay binds to
    /// this so a just-added keyframe is visible immediately (and the box moves as you scrub/play).
    /// </summary>
    public ObservableCollection<Avalonia.Rect> ActiveRedactionBoxes => _redaction.ActiveRedactionBoxes;

    public ReactiveCommand<Unit, Unit> AddRedactionKeyframeCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> NewRedactionObjectCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ClearRedactionCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> CycleRedactionEffectCommand { get; private set; } = null!;

    private void InitializeRedactionCommands()
    {
        _redaction = new RedactionEditorState(
            () => (DisplayWidth, DisplayHeight),
            () => CurrentTime.TotalSeconds,
            () => SelectionRect,
            () => SelectionRect = new Avalonia.Rect(),
            this.WhenAnyValue(x => x.IsSelectionActive));

        // Re-raise the delegating properties so existing bindings (tooltip, export guards) stay live.
        _redaction.RedactionChanged += () =>
        {
            this.RaisePropertyChanged(nameof(HasRedaction));
            this.RaisePropertyChanged(nameof(RedactionStatus));
            this.RaisePropertyChanged(nameof(SelectedRedactionEffect));
        };

        AddRedactionKeyframeCommand = _redaction.AddKeyframeCommand;
        NewRedactionObjectCommand = _redaction.NewObjectCommand;
        ClearRedactionCommand = _redaction.ClearCommand;
        CycleRedactionEffectCommand = _redaction.CycleEffectCommand;
    }

    internal void RefreshActiveRedactionBoxes() => _redaction.RefreshActiveBoxes();

    /// <summary>
    /// A composite hook that burns the current redaction tracks into each frame at its source time,
    /// or null when there is nothing to redact. The tracks are snapshotted so the export worker thread
    /// is not affected by later edits.
    /// </summary>
    private Action<SKBitmap, double>? BuildRedactionComposite() => _redaction.BuildComposite();
}
