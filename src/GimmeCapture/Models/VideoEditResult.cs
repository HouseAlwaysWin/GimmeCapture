using System.Collections.Generic;

namespace GimmeCapture.Models;

/// <summary>
/// The full edit state exchanged between a compress queue item and the 進階影片編輯 editor: the kept
/// runs (with per-run speed), crop + rotation, and the burn-in layers — annotations (drawn in the
/// editor's SURFACE space: the cropped+rotated preview-frame pixel size recorded here) and redaction
/// tracks (normalized [0,1]). Also the Apply payload the editor hands back.
/// </summary>
public sealed record VideoEditResult(
    IReadOnlyList<VideoEditSegment> KeptRuns,
    VideoEditCrop? Crop,
    int RotationDegrees,
    IReadOnlyList<Annotation> Annotations,
    double AnnotationSurfaceWidth,
    double AnnotationSurfaceHeight,
    IReadOnlyList<RedactionTrack> RedactionTracks)
{
    public static VideoEditResult Empty { get; } = new(
        System.Array.Empty<VideoEditSegment>(), null, 0,
        System.Array.Empty<Annotation>(), 0, 0, System.Array.Empty<RedactionTrack>());

    /// <summary>True when annotations or redaction must be burned into the frames at encode.</summary>
    public bool HasBurnIn => Annotations.Count > 0 || RedactionTracks.Count > 0;
}
