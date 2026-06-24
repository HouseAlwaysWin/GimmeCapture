using System;
using System.Collections.Generic;

namespace GimmeCapture.Models;

/// <summary>
/// One kept slice of the source video, expressed in SOURCE seconds, optionally time-scaled.
/// <see cref="Speed"/> &gt; 1 plays faster (shorter output); &lt; 1 is slow-motion.
/// </summary>
public sealed record VideoEditSegment(double SourceStart, double SourceEnd, double Speed = 1.0)
{
    public double SourceDuration => Math.Max(0.0, SourceEnd - SourceStart);

    public double OutputDuration => Speed > 0 ? SourceDuration / Speed : SourceDuration;

    /// <summary>
    /// Whether this piece is kept in the output. The timeline shows all contiguous source pieces; the
    /// user toggles which to keep. Export/preview use only kept pieces. The pure compiler/editor ignore
    /// this flag (the VM filters before building the project); <c>with { }</c> preserves it across splits.
    /// </summary>
    public bool Kept { get; init; } = true;
}

/// <summary>Pixel crop rectangle applied to the whole output.</summary>
public sealed record VideoEditCrop(int X, int Y, int Width, int Height);

/// <summary>
/// A pre-rendered PNG overlay (vector annotations / text burned to an image) shown only between
/// <see cref="StartSeconds"/> and <see cref="EndSeconds"/> of the OUTPUT timeline. A whole-clip
/// overlay is just one whose range spans the output.
/// </summary>
public sealed record VideoEditOverlay(string PngPath, int X, int Y, double StartSeconds, double EndSeconds);

/// <summary>A span of OUTPUT time whose audio is silenced.</summary>
public sealed record VideoEditMuteRange(double StartSeconds, double EndSeconds);

/// <summary>Audio adjustments applied after the segments are concatenated.</summary>
public sealed record VideoEditAudio(
    double Volume = 1.0,
    double FadeInSeconds = 0.0,
    double FadeOutSeconds = 0.0,
    IReadOnlyList<VideoEditMuteRange>? MutedRanges = null)
{
    public bool IsIdentity =>
        Math.Abs(Volume - 1.0) < 1e-6
        && FadeInSeconds <= 0
        && FadeOutSeconds <= 0
        && (MutedRanges is null || MutedRanges.Count == 0);
}

/// <summary>
/// Declarative description of a video edit. The single source of truth shared by the timeline UI, the
/// player preview, and export. <see cref="VideoEditFilterCompiler"/> turns it into one ffmpeg
/// filtergraph. Today's single-range trim is one segment; a whole-clip annotation is one overlay
/// spanning the output.
/// </summary>
public sealed class VideoEditProject
{
    public IReadOnlyList<VideoEditSegment> Segments { get; init; } = Array.Empty<VideoEditSegment>();

    public VideoEditCrop? Crop { get; init; }

    public IReadOnlyList<VideoEditOverlay> Overlays { get; init; } = Array.Empty<VideoEditOverlay>();

    public VideoEditAudio? Audio { get; init; }

    /// <summary>Source duration in seconds (0/unknown means "to end of file").</summary>
    public double TotalSourceDuration { get; init; }

    /// <summary>Total output duration after trims/speed.</summary>
    public double OutputDuration
    {
        get
        {
            if (Segments.Count == 0)
            {
                return TotalSourceDuration;
            }

            double total = 0;
            foreach (VideoEditSegment s in Segments)
            {
                total += s.OutputDuration;
            }

            return total;
        }
    }

    /// <summary>
    /// True when the project is a no-op (a single full-speed segment covering the whole clip, no crop,
    /// no overlays, no audio edit). Callers can copy/passthrough instead of running the filtergraph.
    /// </summary>
    public bool IsTrivial =>
        Crop is null
        && Overlays.Count == 0
        && (Audio is null || Audio.IsIdentity)
        && (Segments.Count == 0
            || (Segments.Count == 1
                && Math.Abs(Segments[0].Speed - 1.0) < 1e-6
                && Segments[0].SourceStart <= 0.0001
                && (TotalSourceDuration <= 0 || Segments[0].SourceEnd >= TotalSourceDuration - 0.0001)));
}
