using System.Collections.Generic;

namespace GimmeCapture.Models;

/// <summary>
/// How a tracked object is obscured when its redaction track is burned into the video. Blur and Mosaic
/// reuse the existing annotation effect renderers; SolidBlack fills the box with opaque black.
/// </summary>
public enum RedactionEffect
{
    Blur,
    Mosaic,
    SolidBlack,
}

/// <summary>
/// One sample of a tracked object's bounding box at a point in time. Coordinates are normalized to the
/// source frame ([0,1] in both axes) so a track is resolution-independent and can be applied to any
/// export size. Authored on a keyframe via SAM2 (the mask's bounds) or by hand.
/// </summary>
public sealed class RedactionKeyframe
{
    public double TimeSeconds { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
}

/// <summary>An interpolated normalized box ([0,1]) produced by <see cref="RedactionKeyframe"/> sampling.</summary>
public readonly record struct RedactionBox(double X, double Y, double Width, double Height);

/// <summary>
/// A single object's redaction across the clip: an effect plus the keyframes that pin its box over time.
/// Between keyframes the box is linearly interpolated; before the first / after the last it is held
/// (see <c>RedactionInterpolator</c>). Multiple tracks redact multiple objects independently.
/// </summary>
public sealed class RedactionTrack
{
    public RedactionEffect Effect { get; set; } = RedactionEffect.Blur;

    public List<RedactionKeyframe> Keyframes { get; } = new();
}
