using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Media;

namespace GimmeCapture.ViewModels.Floating;

// A timeline with pieces cut out or at other speeds, in one decode. Played a pass per piece, every piece opened the file,
// its decoder and its GPU device again: on a 1450x900 recording the last frame of a piece stood 182-221 ms where a frame
// lasts 33, and the sound, started on its own each time, came -154 to +19 ms off the picture, another amount each piece.
// The file now stays open from the first piece to the last and round again, the picture keeps to each piece's sound, and
// the next piece begins as soon as the last one's time is over.
public partial class FloatingVideoViewModel
{
    // The pieces being played that way, while they are: any thread reads it, the playback loop sets it.
    private PiecesRun? _piecesRun;

    private sealed record PiecesRun(IReadOnlyList<VideoEditSegment> Runs, int Generation);

    /// <summary>
    /// The pieces to play for <paramref name="runs"/> (kept stretches, contiguous ones joined) from
    /// <paramref name="fromSeconds"/>, each at its own speed times <paramref name="playbackSpeed"/>. A time in a cut, or
    /// before the first piece, starts at the next piece. Past the last piece it is the end of the clip: back to the first
    /// piece when <paramref name="goesRound"/> says so, and null otherwise.
    /// </summary>
    internal static PlaybackPieces? PiecesFor(
        IReadOnlyList<VideoEditSegment> runs, double fromSeconds, double playbackSpeed, Func<bool> goesRound)
    {
        VideoEditSegment[] kept = runs as VideoEditSegment[] ?? runs.ToArray();
        double start = fromSeconds;
        int first = ResolveSegmentForPlayback(kept, ref start);
        if (first < 0)
        {
            if (!goesRound())
            {
                return null;
            }

            first = 0;
            start = kept[0].SourceStart;
        }

        return new PlaybackPieces(
            kept.Select(r => new PlaybackPiece(r.SourceStart, r.SourceEnd, (r.Speed > 0 ? r.Speed : 1.0) * playbackSpeed)).ToArray(),
            first,
            start,
            goesRound);
    }

    // UI thread: the pieces the decode now playing was given, not those of one being cancelled.
    private PiecesRun? ActivePiecesRun() =>
        _isPlaybackActive
        && Volatile.Read(ref _piecesRun) is { } run
        && run.Generation == Volatile.Read(ref _playbackGeneration)
            ? run
            : null;

    // The playback loop. Plays the pieces until they are over or `ct` cancels: true then, and false when they could not
    // be played on in one decode, for the loop to play the rest pass by pass.
    private async Task<bool> PlayKeptPiecesAsync(
        CancellationToken ct, int generation, PlaybackPieces pieces, IReadOnlyList<VideoEditSegment> runs)
    {
        Volatile.Write(ref _piecesRun, new PiecesRun(runs, generation));
        try
        {
            await _nativeFramePlayer.PlayFramesAsync(
                VideoPath,
                _width,
                _height,
                pieces.Current.StartSeconds,
                pieces.Current.Speed,
                false,
                (bgra, seconds) =>
                {
                    if (_isDisposed || generation != Volatile.Read(ref _playbackGeneration))
                    {
                        return;
                    }

                    UpdateBitmap(bgra, generation);
                    _decodedPlayhead.Record(Math.Max(0, seconds), generation);
                    RequestCurrentTimeUiRefresh();
                },
                ct,
                onDurationKnown: ApplyProbedDuration,
                pieces: pieces,
                onPieceStarts: piece => StartPieceSound(piece, ct)).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
        {
            AppLog.Debug("FloatingVideo.KeptPieces", ex);
            return false;
        }
        finally
        {
            Volatile.Write(ref _piecesRun, null);

            // The last frame can land inside the refresh throttle: show the frame and time it stopped on.
            if (generation == Volatile.Read(ref _playbackGeneration))
            {
                TryScheduleFrameUiRefresh(force: true);
                RequestCurrentTimeUiRefresh(force: true);
            }
        }
    }

    // The decode thread, as it reaches a piece: starts that piece's sound and returns its clock for the picture to keep to.
    private Func<SoundClockReading>? StartPieceSound(PlaybackPiece piece, CancellationToken ct)
    {
        if (IsMuted || _isDisposed || ct.IsCancellationRequested || _audioPreview.FoundNoAudioStream(VideoPath))
        {
            return null;
        }

        long ticket = _audioPreview.Start(VideoPath, piece.StartSeconds, piece.Speed);
        if (ct.IsCancellationRequested)
        {
            // A pause between the check and the start has stopped the sound already, ahead of this one.
            StopAudioPlayback();
            return null;
        }

        return () => _audioPreview.ReadClock(ticket);
    }
}
