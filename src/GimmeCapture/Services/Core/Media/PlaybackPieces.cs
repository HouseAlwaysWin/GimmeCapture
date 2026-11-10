using System;
using System.Collections.Generic;

namespace GimmeCapture.Services.Core.Media;

/// <summary>A stretch of a video that a preview plays, in the file's time, at a speed of its own.</summary>
internal readonly record struct PlaybackPiece(double StartSeconds, double EndSeconds, double Speed);

/// <summary>
/// The pieces of a video that a preview plays one after the other in one decode, what lies between them left out: the
/// piece under way, and what comes after it, round to the first piece again while the caller wants that.
/// </summary>
internal sealed class PlaybackPieces
{
    // A frame within half a millisecond of a time counts as at it: the decoder's timestamps are rounded.
    private const double Epsilon = 0.0005;

    private IReadOnlyList<PlaybackPiece> _pieces;
    private readonly Func<bool> _goesRound;
    private int _index;
    private bool _roundIsWhole; // false for the first, which may have begun partway
    private bool _roundShowedAFrame;

    /// <param name="pieces">The pieces, in the order they play.</param>
    /// <param name="firstPiece">The one the playback begins in.</param>
    /// <param name="startSeconds">Where in it the playback begins.</param>
    /// <param name="goesRound">Asked when the last piece is over: whether the first follows it.</param>
    public PlaybackPieces(IReadOnlyList<PlaybackPiece> pieces, int firstPiece, double startSeconds, Func<bool> goesRound)
    {
        _pieces = pieces;
        _goesRound = goesRound;
        _index = firstPiece;
        Current = pieces[firstPiece] with { StartSeconds = startSeconds };
    }

    /// <summary>The piece under way, starting where it plays from: the first one played may begin partway.</summary>
    public PlaybackPiece Current { get; private set; }

    /// <summary>
    /// Ends every piece where the picture ends at the latest: past that, a piece would hold its last frame through a sound
    /// that runs on (0.2-1 s on a recording). A time of 0 means the file did not say, and changes nothing.
    /// </summary>
    public void EndAtMost(double pictureEndSeconds)
    {
        if (pictureEndSeconds <= 0)
        {
            return;
        }

        var ended = new PlaybackPiece[_pieces.Count];
        for (int i = 0; i < ended.Length; i++)
        {
            ended[i] = _pieces[i] with { EndSeconds = Math.Min(_pieces[i].EndSeconds, pictureEndSeconds) };
        }

        _pieces = ended;
        Current = Current with { EndSeconds = Math.Min(Current.EndSeconds, pictureEndSeconds) };
    }

    /// <summary>A frame at or past the end of the piece, which ends the piece instead of being shown.</summary>
    public bool EndsPiece(double frameSeconds) => frameSeconds >= Current.EndSeconds - Epsilon;

    /// <summary>
    /// The piece under way is over, and the next one becomes <see cref="Current"/>. False when nothing follows: the last
    /// piece is over and the caller wants no other round, or a whole round went by without a frame, as round after round
    /// would when the pieces lie past the end of the picture.
    /// </summary>
    public bool MoveNext(bool pieceShowedAFrame)
    {
        _roundShowedAFrame |= pieceShowedAFrame;
        if (++_index < _pieces.Count)
        {
            Current = _pieces[_index];
            return true;
        }

        if (!_goesRound() || (_roundIsWhole && !_roundShowedAFrame))
        {
            return false;
        }

        _index = 0;
        Current = _pieces[0];
        _roundIsWhole = true;
        _roundShowedAFrame = false;
        return true;
    }
}
