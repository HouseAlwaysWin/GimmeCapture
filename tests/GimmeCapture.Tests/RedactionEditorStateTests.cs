using System;
using System.Reactive.Linq;
using GimmeCapture.Models;
using GimmeCapture.ViewModels.Shared;
using Xunit;

namespace GimmeCapture.Tests;

// The shared redaction editing state (extracted from the Pin video window, reused by the compress
// 進階影片編輯 editor): keyframe capture/normalization, effect cycling, live-preview box math, and the
// export composite snapshot.
public class RedactionEditorStateTests
{
    private sealed class Host
    {
        public double DisplayW = 200, DisplayH = 100;
        public double Playhead;
        public Avalonia.Rect Selection;
        public bool SelectionCleared;

        public RedactionEditorState MakeState() => new(
            () => (DisplayW, DisplayH),
            () => Playhead,
            () => Selection,
            () => { SelectionCleared = true; Selection = new Avalonia.Rect(); },
            Observable.Return(true));
    }

    [Fact]
    public void AddKeyframe_NormalizesToDisplaySize_AndClearsSelection()
    {
        var host = new Host { Playhead = 5, Selection = new Avalonia.Rect(20, 10, 100, 50) };
        RedactionEditorState state = host.MakeState();

        state.AddKeyframe();

        Assert.Single(state.RedactionTracks);
        RedactionKeyframe k = state.RedactionTracks[0].Keyframes[0];
        Assert.Equal(5, k.TimeSeconds, 3);
        Assert.Equal(0.1, k.X, 3);    // 20/200
        Assert.Equal(0.1, k.Y, 3);    // 10/100
        Assert.Equal(0.5, k.Width, 3);  // 100/200
        Assert.Equal(0.5, k.Height, 3); // 50/100
        Assert.True(host.SelectionCleared);
        Assert.True(state.HasRedaction);
    }

    [Fact]
    public void AddKeyframe_SameTime_ReplacesExistingKeyframe()
    {
        var host = new Host { Playhead = 5, Selection = new Avalonia.Rect(0, 0, 50, 50) };
        RedactionEditorState state = host.MakeState();
        state.AddKeyframe();

        host.Selection = new Avalonia.Rect(100, 0, 50, 50); // re-draw at the same playhead
        state.AddKeyframe();

        Assert.Single(state.RedactionTracks);
        Assert.Single(state.RedactionTracks[0].Keyframes);
        Assert.Equal(0.5, state.RedactionTracks[0].Keyframes[0].X, 3); // the new box (100/200)
    }

    [Fact]
    public void NewObject_StartsASecondTrack()
    {
        var host = new Host { Playhead = 1, Selection = new Avalonia.Rect(0, 0, 40, 40) };
        RedactionEditorState state = host.MakeState();
        state.AddKeyframe();

        state.NewObject();
        host.Playhead = 2;
        host.Selection = new Avalonia.Rect(60, 0, 40, 40);
        state.AddKeyframe();

        Assert.Equal(2, state.RedactionTracks.Count);
    }

    [Fact]
    public void CycleEffect_RestylesTheActiveTrack()
    {
        var host = new Host { Playhead = 1, Selection = new Avalonia.Rect(0, 0, 40, 40) };
        RedactionEditorState state = host.MakeState();
        state.AddKeyframe(); // Blur (default)

        state.CycleEffect(); // → Mosaic

        Assert.Equal(RedactionEffect.Mosaic, state.SelectedRedactionEffect);
        Assert.Equal(RedactionEffect.Mosaic, state.RedactionTracks[0].Effect);
        state.CycleEffect(); // → SolidBlack
        state.CycleEffect(); // → Blur
        Assert.Equal(RedactionEffect.Blur, state.SelectedRedactionEffect);
    }

    [Fact]
    public void SettingSelectedEffect_RestylesTheActiveTrack()
    {
        // The dropdown sets SelectedRedactionEffect directly (not via CycleEffect); the in-progress
        // track must pick up the new effect immediately, same as cycling did.
        var host = new Host { Playhead = 1, Selection = new Avalonia.Rect(0, 0, 40, 40) };
        RedactionEditorState state = host.MakeState();
        state.AddKeyframe(); // Blur (default)
        Assert.Equal(RedactionEffect.Blur, state.RedactionTracks[0].Effect);

        state.SelectedRedactionEffect = RedactionEffect.SolidBlack;

        Assert.Equal(RedactionEffect.SolidBlack, state.RedactionTracks[0].Effect);
    }

    [Fact]
    public void AvailableEffects_ExposesTheThreeEffects()
    {
        RedactionEditorState state = new Host().MakeState();
        Assert.Equal(
            new[] { RedactionEffect.Blur, RedactionEffect.Mosaic, RedactionEffect.SolidBlack },
            state.AvailableEffects);
    }

    [Fact]
    public void RefreshActiveBoxes_MapsNormalizedBoxBackToDisplaySpace()
    {
        var host = new Host { Playhead = 5, Selection = new Avalonia.Rect(20, 10, 100, 50) };
        RedactionEditorState state = host.MakeState();
        state.AddKeyframe(); // single keyframe → static box at every time

        host.Playhead = 30;
        state.RefreshActiveBoxes();

        Assert.Single(state.ActiveRedactionBoxes);
        Avalonia.Rect box = state.ActiveRedactionBoxes[0];
        Assert.Equal(20, box.X, 3);
        Assert.Equal(10, box.Y, 3);
        Assert.Equal(100, box.Width, 3);
        Assert.Equal(50, box.Height, 3);
    }

    [Fact]
    public void BuildComposite_NullWhenEmpty_SnapshotIsolatedFromLaterEdits()
    {
        var host = new Host { Playhead = 1, Selection = new Avalonia.Rect(0, 0, 40, 40) };
        RedactionEditorState state = host.MakeState();
        Assert.Null(state.BuildComposite());

        state.AddKeyframe();
        var composite = state.BuildComposite();
        Assert.NotNull(composite);

        state.Clear(); // later edit must not affect the snapshot the export thread holds
        Assert.False(state.HasRedaction);
        Assert.NotNull(composite); // still callable (renders the snapshotted track)
    }

    [Fact]
    public void LoadTracks_RestoresPersistedTracks()
    {
        var host = new Host();
        RedactionEditorState state = host.MakeState();
        var track = new RedactionTrack { Effect = RedactionEffect.SolidBlack };
        track.Keyframes.Add(new RedactionKeyframe { TimeSeconds = 3, X = 0.1, Y = 0.1, Width = 0.2, Height = 0.2 });

        state.LoadTracks(new[] { track });

        Assert.True(state.HasRedaction);
        Assert.Single(state.RedactionTracks);
        Assert.Equal(RedactionEffect.SolidBlack, state.RedactionTracks[0].Effect);
    }
}
