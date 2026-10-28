using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using GimmeCapture.Models;
using GimmeCapture.ViewModels.Floating;
using Xunit;

namespace GimmeCapture.Tests;

// A video pin's playback loop runs on the thread pool and plays the timeline's kept pieces. It read them from
// EditSegments, the collection the UI thread edits, at every pass; it now plays the copy the UI thread hands over here.
// The queue stands in for the dispatcher: what the handoff posts runs once the test's own "UI work" is done.
public class KeptSegmentsHandoffTests
{
    private readonly Queue<Action> _posted = new();

    private KeptSegmentsHandoff HandoffOf(ObservableCollection<VideoEditSegment> segments) => new(segments, _posted.Enqueue);

    private void RunPosted()
    {
        while (_posted.Count > 0)
        {
            _posted.Dequeue()();
        }
    }

    private static VideoEditSegment Piece(double start, double end, bool kept = true) => new(start, end) { Kept = kept };

    [Fact]
    public void TheLoopGetsOnlyTheKeptPieces()
    {
        var segments = new ObservableCollection<VideoEditSegment> { Piece(0, 2), Piece(2, 4, kept: false), Piece(4, 6) };

        var handoff = HandoffOf(segments);

        Assert.Equal(new[] { Piece(0, 2), Piece(4, 6) }, handoff.Kept);
    }

    [Fact]
    public void ASplit_ReachesTheLoopOnlyOnceItIsComplete()
    {
        // A split or merge empties the collection and adds the pieces back one at a time (ReplaceSegments).
        var segments = new ObservableCollection<VideoEditSegment> { Piece(0, 6) };
        var handoff = HandoffOf(segments);

        segments.Clear();
        Assert.Equal(new[] { Piece(0, 6) }, handoff.Kept);
        segments.Add(Piece(0, 3));
        Assert.Equal(new[] { Piece(0, 6) }, handoff.Kept);
        segments.Add(Piece(3, 6));
        RunPosted();

        Assert.Equal(new[] { Piece(0, 3), Piece(3, 6) }, handoff.Kept);
    }

    [Fact]
    public void EachBurstOfChanges_IsHandedOverOnce()
    {
        var segments = new ObservableCollection<VideoEditSegment> { Piece(0, 6) };
        var handoff = HandoffOf(segments);

        segments.Clear();
        segments.Add(Piece(0, 3));
        segments.Add(Piece(3, 6));
        Assert.Single(_posted);
        RunPosted();

        segments[1] = Piece(3, 6, kept: false); // dropping a piece later is a burst of its own
        Assert.Single(_posted);
        RunPosted();

        Assert.Equal(new[] { Piece(0, 3) }, handoff.Kept);
    }

    [Fact]
    public void StartingPlayback_TakesThePiecesAsTheyAreNow()
    {
        var segments = new ObservableCollection<VideoEditSegment> { Piece(0, 6) };
        var handoff = HandoffOf(segments);
        segments[0] = Piece(0, 6) with { Speed = 2.0 }; // its hand-over is still queued

        VideoEditSegment[] started = handoff.Refresh();

        Assert.Equal(2.0, Assert.Single(started).Speed);
        Assert.Same(started, handoff.Kept);
    }

    [Fact]
    public void APassAlreadyPlaying_KeepsItsPieces_AndTheNextPassGetsTheEdit()
    {
        var segments = new ObservableCollection<VideoEditSegment> { Piece(0, 3), Piece(3, 6) };
        var handoff = HandoffOf(segments);
        VideoEditSegment[] playing = handoff.Kept;

        segments[1] = Piece(3, 6, kept: false);
        RunPosted();

        Assert.Equal(new[] { Piece(0, 3), Piece(3, 6) }, playing);
        Assert.Equal(new[] { Piece(0, 3) }, handoff.Kept);
    }
}
