using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using GimmeCapture.Models;

namespace GimmeCapture.Services.Core.Media;

/// <summary>
/// Pure compiler that turns a <see cref="VideoEditProject"/> into a single ffmpeg <c>-filter_complex</c>
/// graph plus the output map labels. No ffmpeg execution, no SkiaSharp — just string generation, so it
/// is fully unit-testable. The caller wires inputs as: <c>-i source</c> then one <c>-loop 1 -i overlay.png</c>
/// per <see cref="VideoEditProject.Overlays"/> entry (in order), then
/// <c>-filter_complex {FilterComplex} -map {VideoMap} [-map {AudioMap}]</c>.
/// </summary>
public static class VideoEditFilterCompiler
{
    /// <param name="FilterComplex">The full filtergraph (no surrounding quotes).</param>
    /// <param name="VideoMap">Bracketed label to map as the output video stream, e.g. <c>[vout]</c>.</param>
    /// <param name="AudioMap">Bracketed audio label, or null when there is no audio.</param>
    /// <param name="OverlayInputCount">How many extra image inputs the caller must add (in order).</param>
    public readonly record struct CompiledEdit(string FilterComplex, string VideoMap, string? AudioMap, int OverlayInputCount);

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Compiles the project. <paramref name="sourceHasAudio"/> gates the audio chain.</summary>
    public static CompiledEdit Compile(VideoEditProject project, bool sourceHasAudio)
    {
        ArgumentNullException.ThrowIfNull(project);

        IReadOnlyList<VideoEditSegment> segments = project.Segments;
        if (segments.Count == 0)
        {
            segments = new[] { new VideoEditSegment(0, project.TotalSourceDuration) };
        }

        var sb = new StringBuilder();

        // ---- video: per-segment trim+setpts -> concat -> crop -> timed overlays ----
        var vLabels = new List<string>(segments.Count);
        for (int i = 0; i < segments.Count; i++)
        {
            VideoEditSegment s = segments[i];
            string lbl = "v" + i;
            sb.Append("[0:v]");
            AppendTrim(sb, "trim", s.SourceStart, s.SourceEnd);
            sb.Append(",setpts=(PTS-STARTPTS)");
            if (HasSpeed(s.Speed))
            {
                sb.Append('/').Append(F(s.Speed));
            }

            sb.Append('[').Append(lbl).Append("];");
            vLabels.Add(lbl);
        }

        string vCur = Concat(sb, vLabels, video: true);

        if (project.Crop is { } crop)
        {
            sb.Append('[').Append(vCur).Append(']')
              .Append("crop=").Append(crop.Width).Append(':').Append(crop.Height)
              .Append(':').Append(crop.X).Append(':').Append(crop.Y)
              .Append("[vcrop];");
            vCur = "vcrop";
        }

        for (int k = 0; k < project.Overlays.Count; k++)
        {
            VideoEditOverlay ov = project.Overlays[k];
            int inputIndex = k + 1; // input 0 is the source video
            string outLbl = "ov" + k;
            sb.Append('[').Append(vCur).Append("][").Append(inputIndex).Append(":v]")
              .Append("overlay=").Append(ov.X).Append(':').Append(ov.Y)
              .Append(":enable='between(t,").Append(F(ov.StartSeconds)).Append(',').Append(F(ov.EndSeconds)).Append(")'")
              .Append('[').Append(outLbl).Append("];");
            vCur = outLbl;
        }

        // ---- audio: per-segment atrim+asetpts(+atempo) -> concat -> volume/fade/mute ----
        string? aMap = null;
        if (sourceHasAudio)
        {
            var aLabels = new List<string>(segments.Count);
            for (int i = 0; i < segments.Count; i++)
            {
                VideoEditSegment s = segments[i];
                string lbl = "a" + i;
                sb.Append("[0:a]");
                AppendTrim(sb, "atrim", s.SourceStart, s.SourceEnd);
                sb.Append(",asetpts=(PTS-STARTPTS)");
                if (HasSpeed(s.Speed))
                {
                    sb.Append(',').Append(BuildAtempoChain(s.Speed));
                }

                sb.Append('[').Append(lbl).Append("];");
                aLabels.Add(lbl);
            }

            string aCur = Concat(sb, aLabels, video: false);

            VideoEditAudio? audio = project.Audio;
            if (audio != null && !audio.IsIdentity)
            {
                var filters = new List<string>();
                if (Math.Abs(audio.Volume - 1.0) > 1e-6)
                {
                    filters.Add("volume=" + F(audio.Volume));
                }

                if (audio.FadeInSeconds > 0)
                {
                    filters.Add("afade=t=in:st=0:d=" + F(audio.FadeInSeconds));
                }

                if (audio.FadeOutSeconds > 0)
                {
                    double st = Math.Max(0, project.OutputDuration - audio.FadeOutSeconds);
                    filters.Add("afade=t=out:st=" + F(st) + ":d=" + F(audio.FadeOutSeconds));
                }

                if (audio.MutedRanges != null)
                {
                    foreach (VideoEditMuteRange m in audio.MutedRanges)
                    {
                        filters.Add("volume=enable='between(t," + F(m.StartSeconds) + "," + F(m.EndSeconds) + ")':volume=0");
                    }
                }

                if (filters.Count > 0)
                {
                    sb.Append('[').Append(aCur).Append(']').Append(string.Join(",", filters)).Append("[aout];");
                    aCur = "aout";
                }
            }

            aMap = "[" + aCur + "]";
        }

        string filter = sb.ToString();
        if (filter.EndsWith(";", StringComparison.Ordinal))
        {
            filter = filter[..^1];
        }

        return new CompiledEdit(filter, "[" + vCur + "]", aMap, project.Overlays.Count);
    }

    private static bool HasSpeed(double speed) => speed > 0 && Math.Abs(speed - 1.0) > 1e-6;

    // trim/atrim: always emit start; emit end only when it is a real, finite, positive bound.
    private static void AppendTrim(StringBuilder sb, string name, double start, double end)
    {
        sb.Append(name).Append("=start=").Append(F(start));
        if (end > start && !double.IsInfinity(end) && end < double.MaxValue)
        {
            sb.Append(":end=").Append(F(end));
        }
    }

    // Joins labels with concat when there is more than one; returns the resulting label.
    private static string Concat(StringBuilder sb, List<string> labels, bool video)
    {
        if (labels.Count == 1)
        {
            return labels[0];
        }

        foreach (string l in labels)
        {
            sb.Append('[').Append(l).Append(']');
        }

        string outLbl = video ? "vcat" : "acat";
        sb.Append("concat=n=").Append(labels.Count)
          .Append(video ? ":v=1:a=0[" : ":v=0:a=1[").Append(outLbl).Append("];");
        return outLbl;
    }

    // ffmpeg atempo handles 0.5..2.0 per instance; chain to reach factors outside that range.
    private static string BuildAtempoChain(double speed)
    {
        var parts = new List<string>();
        double remaining = speed;
        while (remaining > 2.0 + 1e-9)
        {
            parts.Add("atempo=2.0");
            remaining /= 2.0;
        }

        while (remaining < 0.5 - 1e-9)
        {
            parts.Add("atempo=0.5");
            remaining /= 0.5;
        }

        parts.Add("atempo=" + F(remaining));
        return string.Join(",", parts);
    }
}
