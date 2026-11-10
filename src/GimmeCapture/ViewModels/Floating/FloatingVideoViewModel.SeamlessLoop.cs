using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media;

namespace GimmeCapture.ViewModels.Floating;

// A plain pin's loop in one decode. Played pass by pass, every pass opened the file, the decoder, its GPU device and the
// sound again: the picture stood still 70-120 ms at each loop, and the sound, started anew each time, began a different
// 30-150 ms off the picture every pass. Now the file stays open across each jump back, the sound loops in step without
// being reopened, and the picture keeps to the sound's clock. Timelines with pieces cut out or at other speeds play in one
// decode as well (FloatingVideoViewModel.KeptPieces.cs).
public partial class FloatingVideoViewModel
{
    // The loop being played that way, while one is: any thread reads it, the playback loop sets it.
    private SeamlessRun? _seamlessRun;

    // A file that did not say how long it is loops pass by pass from then on.
    private volatile bool _seamlessUnavailable;

    private sealed record SeamlessRun(PlaybackLoop Loop, int Generation);

    /// <summary>
    /// The loop a pin plays in one decode, or null when it plays pass by pass: looping at 1×, either the whole clip (a
    /// NaN end: wherever the picture ends, which the player reads from the file) or one stretch of it kept at 1×
    /// (contiguous pieces count as one).
    /// </summary>
    internal static PlaybackLoop? SeamlessLoopFor(IReadOnlyList<VideoEditSegment> kept, bool looping, double playbackSpeed)
    {
        if (!looping || Math.Abs(playbackSpeed - 1.0) > 0.001)
        {
            return null;
        }

        if (kept.Count == 0)
        {
            return new PlaybackLoop(0, double.NaN);
        }

        IReadOnlyList<VideoEditSegment> runs = VideoSegmentEditor.CoalesceContiguous(kept);
        if (runs.Count != 1)
        {
            return null;
        }

        VideoEditSegment run = runs[0];
        double speed = run.Speed > 0 ? run.Speed : 1.0;
        var loop = new PlaybackLoop(run.SourceStart, run.SourceEnd);
        return Math.Abs(speed - 1.0) <= 0.001 && loop.CanLoop ? loop : null;
    }

    // Two loops asked for are the same one when they start alike and end alike, the picture's end matching any: the
    // player ends a loop there at the latest.
    internal static bool IsSameLoop(PlaybackLoop running, PlaybackLoop? next) =>
        next is { } loop
        && Math.Abs(loop.StartSeconds - running.StartSeconds) < 0.001
        && (double.IsNaN(loop.EndSeconds) || double.IsNaN(running.EndSeconds)
            || Math.Abs(loop.EndSeconds - running.EndSeconds) < 0.001);

    // UI thread: the loop asked of the decode playing for the playback now under way, not of one being cancelled.
    private PlaybackLoop? ActiveSeamlessLoop() =>
        _isPlaybackActive
        && Volatile.Read(ref _seamlessRun) is { } run
        && run.Generation == Volatile.Read(ref _playbackGeneration)
            ? run.Loop
            : null;

    // UI thread. Muted, the picture goes on by its own clock; unmuted, both start again from the playhead together,
    // as the sound must begin where the picture is. False when no such loop plays, for the pass-by-pass handling.
    private bool OnMutedChangedInSeamlessLoop()
    {
        if (ActiveSeamlessLoop() is null && ActivePiecesRun() is null)
        {
            return false;
        }

        if (IsMuted)
        {
            StopAudioPlayback();
        }
        else
        {
            StartPlayback();
        }

        return true;
    }

    // UI thread: a loop playing in one decode never reaches a pass end to stop at, so looping turned off starts the
    // playback again from the playhead, pass by pass, to stop at the end.
    private void OnLoopingChangedInSeamlessLoop()
    {
        if (!IsLooping && ActiveSeamlessLoop() is not null)
        {
            StartPlayback();
            return;
        }

        // One sound through the pieces was laid out for the Loop as it was; and turned on, what can loop without being
        // opened again does so from here.
        if (ActivePiecesRun() is { } run
            && (run.OneSound || (IsLooping && SeamlessLoopFor(_keptForPlayback.Kept, IsLooping, _playbackSpeed) is not null)))
        {
            StartPlayback();
        }
    }

    // UI thread: an edit that changes what loops starts the playback again from the playhead to play it.
    private void OnKeptPiecesChanged()
    {
        if (ActiveSeamlessLoop() is { } running
            && !IsSameLoop(running, SeamlessLoopFor(_keptForPlayback.Kept, IsLooping, _playbackSpeed)))
        {
            StartPlayback();
            return;
        }

        if (ActivePiecesRun() is { } playing
            && !playing.Runs.SequenceEqual(VideoSegmentEditor.CoalesceContiguous(_keptForPlayback.Kept)))
        {
            StartPlayback();
        }
    }

    // Plays the loop over and over in one decode until cancelled, the sound looping with it while the picture keeps to
    // the sound's clock. The sound starts once the player has opened the file and laid the loop out (it ends where the
    // picture does at the latest), as the picture holds its first frame until the sound starts anyway. Returns by
    // itself only when the file does not say how long its picture is, or cannot seek back to the loop's start, after
    // playing to its end once.
    private async Task PlaySeamlessLoopAsync(CancellationToken ct, int generation, double startSeconds, PlaybackLoop loop)
    {
        Volatile.Write(ref _seamlessRun, new SeamlessRun(loop, generation));

        bool withSound = !IsMuted && !_audioPreview.FoundNoAudioStream(VideoPath);
        bool soundAsked = false; // decode thread, once the run has started
        long soundTicket = 0;
        StopAudioPlayback(); // a pass-by-pass sound may still be playing

        Func<SoundClockReading>? soundClock = null;
        if (withSound)
        {
            soundClock = () =>
            {
                long ticket = Interlocked.Read(ref soundTicket);
                return ticket != 0 ? _audioPreview.ReadClock(ticket) : SoundClockReading.Pending;
            };
        }

        try
        {
            await _nativeFramePlayer.PlayFramesAsync(
                VideoPath,
                _width,
                _height,
                startSeconds,
                1.0,
                false,
                (bgra, seconds) =>
                {
                    if (_isDisposed || generation != Volatile.Read(ref _playbackGeneration))
                    {
                        return;
                    }

                    // No loop could be laid out: the one pass plays with an unlooped sound.
                    if (!soundAsked)
                    {
                        StartSound(startSeconds, null);
                    }

                    UpdateBitmap(bgra, generation);
                    _decodedPlayhead.Record(Math.Max(0, seconds), generation);
                    RequestCurrentTimeUiRefresh();
                },
                ct,
                onDurationKnown: ApplyProbedDuration,
                loopRange: loop,
                soundClock: soundClock,
                onLoopKnown: playing => StartSound(playing.ClampStart(startSeconds), playing)).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _seamlessRun, null);

            // The last frame can land inside the refresh throttle: show the frame and time it stopped on.
            if (generation == Volatile.Read(ref _playbackGeneration))
            {
                TryScheduleFrameUiRefresh(force: true);
                RequestCurrentTimeUiRefresh(force: true);
            }
        }

        void StartSound(double fromSeconds, PlaybackLoop? soundLoop)
        {
            soundAsked = true;
            if (withSound && !ct.IsCancellationRequested && !_isDisposed)
            {
                Interlocked.Exchange(ref soundTicket, _audioPreview.Start(VideoPath, fromSeconds, 1.0, soundLoop));
            }
        }
    }
}
