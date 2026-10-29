using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace GimmeCapture.Services.Core.Media;

/// <summary>
/// Stage timings of one recording start, logged as <c>Recording.StartTimings</c> (and of the warm-up before a run's
/// first one, <c>Recording.WarmUp</c>). A run's first recording took 0.2-2 s from opening the audio to its first frame
/// and every later one 40-70 ms, and the log could not say which stage the difference was in. Each stage reports how
/// long it took after the previous one; figures that are not stages of their own, such as the encoder's share of
/// <c>video</c>, follow the total.
/// </summary>
internal sealed class RecordingStartTimings
{
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly StringBuilder _stages;
    private readonly StringBuilder _notes = new();
    private long _previous;

    internal RecordingStartTimings(string label)
    {
        _previous = _started;
        _stages = new StringBuilder(label);
    }

    /// <summary>Ends the stage that ran since the previous mark, or since the start.</summary>
    internal void Mark(string stage)
    {
        long now = Stopwatch.GetTimestamp();
        _stages.Append(' ').Append(stage).Append('=').Append(Format(Stopwatch.GetElapsedTime(_previous, now)));
        _previous = now;
    }

    internal void Note(string name, string value) => _notes.Append(' ').Append(name).Append('=').Append(value);

    internal void Note(string name, TimeSpan value) => Note(name, Format(value));

    /// <summary>The line: the label and the stages, the total since the start, then the notes.</summary>
    internal string Finish()
    {
        return new StringBuilder().Append(_stages)
            .Append(" total=").Append(Format(Stopwatch.GetElapsedTime(_started))).Append("ms")
            .Append(_notes)
            .ToString();
    }

    private static string Format(TimeSpan elapsed) => elapsed.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture);
}
