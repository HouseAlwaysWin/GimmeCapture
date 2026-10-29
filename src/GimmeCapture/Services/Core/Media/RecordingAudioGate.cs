using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace GimmeCapture.Services.Core.Media;

/// <summary>
/// The stretch of time a segment's video covers, from its first frame until the stop: the only audio that belongs in
/// the segment. Finalize lines the tracks up at their first sample and frame, so audio from before the first frame plays
/// behind the picture by as much, and a segment whose audio ends short makes every later segment's audio play early.
/// <para>
/// NAudio's WASAPI captures poll every 50 ms and hand over 50-70 ms at a time. Gating whole bursts left part of one
/// from before the first frame at the head of the track (3-68 ms measured, 38 on average), and dropped what came before
/// the stop in the burst after it. So each <see cref="Feed"/> cuts its bursts at the gate's edges, placing a burst by
/// when it arrived: it holds the audio that ended then. Measured against the audio handed over, the arrivals spread by
/// up to 10 ms, the device's period, which is how close the cut lands.
/// </para>
/// </summary>
internal sealed class RecordingAudioGate
{
    /// <summary>The time of an edge not reached yet: a gate not opened, or not closed.</summary>
    internal const long Never = long.MaxValue;

    private long _opened = Never;
    private long _closed = Never;

    /// <summary>Whether the video is capturing now. The Linux capture, which hands over small packets, gates on this.</summary>
    public bool IsOpen => Volatile.Read(ref _opened) != Never && Volatile.Read(ref _closed) == Never;

    /// <summary>Shuts the gate for a new segment, until its first frame.</summary>
    public void Reset()
    {
        Volatile.Write(ref _closed, Never);
        Volatile.Write(ref _opened, Never);
    }

    /// <summary>The video's first frame came in at <paramref name="timestamp"/>, a <see cref="Stopwatch"/> timestamp.</summary>
    public void Open(long timestamp) => Volatile.Write(ref _opened, timestamp);

    /// <summary>The video stopped capturing at <paramref name="timestamp"/>.</summary>
    public void Close(long timestamp) => Volatile.Write(ref _closed, timestamp);

    /// <summary>A feed for one capture, opened with it.</summary>
    public Feed NewFeed() => new(this);

    /// <summary>
    /// Which of the <paramref name="frames"/> frames of a burst that arrived at <paramref name="arrival"/> were captured
    /// between <paramref name="opened"/> and <paramref name="closed"/>, as [<c>First</c>, <c>End</c>). The burst holds the
    /// audio that ended as it arrived, so it began its own length before that.
    /// </summary>
    internal static (int First, int End) FramesInside(long arrival, int frames, int sampleRate, long opened, long closed)
    {
        if (frames <= 0 || sampleRate <= 0 || opened == Never)
        {
            return (0, 0);
        }

        long began = arrival - (long)Math.Round((double)frames * Stopwatch.Frequency / sampleRate);
        int first = FramesBefore(opened);
        int end = closed == Never ? frames : FramesBefore(closed);
        return end > first ? (first, end) : (0, 0);

        int FramesBefore(long moment) =>
            (int)Math.Clamp(Math.Round((double)(moment - began) * sampleRate / Stopwatch.Frequency), 0, frames);
    }

    /// <summary>
    /// One capture's bursts through the gate, all from that capture's thread. Cuts each burst, and notes what it cut at
    /// either edge for the log and when the capture has handed over its audio up to the stop.
    /// </summary>
    internal sealed class Feed
    {
        private readonly RecordingAudioGate _gate;
        private readonly TaskCompletionSource _reachedClose = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string _head = "none";
        private string _tail = "none";
        private string _pad = "none";

        internal Feed(RecordingAudioGate gate) => _gate = gate;

        /// <summary>
        /// Completes on the first burst to arrive after the gate closed: the capture has handed over its audio up to the
        /// stop, and stopping it now loses none of that. A burst with nothing in it counts, as it means there was none.
        /// </summary>
        public Task ReachedClose => _reachedClose.Task;

        /// <summary>
        /// For the log: <c>head</c> is how much of the first burst it kept anything of came before the first frame and
        /// was cut, out of that burst; <c>tail</c> how much of the burst that reached the stop came before it and was
        /// kept; <c>pad</c> the silence that ended the audio where the video ends (<see cref="NotePad"/>). <c>none</c>
        /// where there was no such burst or video length.
        /// </summary>
        public string Describe() => $"head={_head} tail={_tail} pad={_pad}";

        /// <summary>
        /// Notes the <paramref name="frames"/> of silence that ended the segment's audio where its video ends, after the
        /// capture stopped. Negative when the audio ran that far past the video's end instead, which is left as it is.
        /// </summary>
        public void NotePad(long frames, int sampleRate) =>
            _pad = string.Create(CultureInfo.InvariantCulture, $"{frames * 1000.0 / sampleRate:F0}ms");

        /// <summary>The frames of a burst that arrived at <paramref name="arrival"/> to write, as [<c>First</c>, <c>End</c>).</summary>
        public (int First, int End) Admit(long arrival, int frames, int sampleRate)
        {
            long closed = Volatile.Read(ref _gate._closed);
            var kept = FramesInside(arrival, frames, sampleRate, Volatile.Read(ref _gate._opened), closed);
            if (kept.End > kept.First && _head == "none")
            {
                _head = Share(kept.First, frames, sampleRate);
            }

            if (arrival >= closed && !_reachedClose.Task.IsCompleted)
            {
                _tail = Share(kept.End - kept.First, frames, sampleRate);
                _reachedClose.TrySetResult();
            }

            return kept;
        }

        private static string Share(int part, int frames, int sampleRate) =>
            string.Create(CultureInfo.InvariantCulture, $"{part * 1000.0 / sampleRate:F0}/{frames * 1000.0 / sampleRate:F0}ms");
    }
}
