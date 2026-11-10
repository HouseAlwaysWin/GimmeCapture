using System;
using System.Collections.Generic;

namespace GimmeCapture.Services.Core.Media;

/// <summary>
/// The stretches of a clip that one sound plays in a row, at the speed of the file and without being opened again: the
/// pieces a preview's picture plays in one decode (<see cref="PlaybackPieces"/>). Started anew for each piece, the sound
/// stopped at every cut for as long as its file and output device took to open.
/// </summary>
/// <param name="Pieces">The pieces, in the order they play.</param>
/// <param name="First">The one the sound begins in.</param>
/// <param name="StartSeconds">Where in it the sound begins.</param>
/// <param name="GoesRound">Whether the first piece follows the last.</param>
internal sealed record SoundPieces(IReadOnlyList<PlaybackPiece> Pieces, int First, double StartSeconds, bool GoesRound)
{
    /// <summary>
    /// What to play, stretch after stretch, for a sound decoded a stretch at a time. Ends after the last piece, unless the
    /// sound goes round, or when a whole round has nothing in it to play.
    /// </summary>
    public IEnumerable<(double StartSeconds, double Seconds)> Stretches()
    {
        int index = First;
        double from = StartSeconds;
        bool roundIsWhole = false; // false for the first, which may have begun partway
        bool roundPlayed = false;
        while (true)
        {
            double seconds = Pieces[index].EndSeconds - from;
            if (seconds > 0)
            {
                roundPlayed = true;
                yield return (from, seconds);
            }

            if (++index == Pieces.Count)
            {
                if (!GoesRound || (roundIsWhole && !roundPlayed))
                {
                    yield break;
                }

                index = 0;
                roundIsWhole = true;
                roundPlayed = false;
            }

            from = Pieces[index].StartSeconds;
        }
    }
}
