using GimmeCapture.Models;

namespace GimmeCapture.Services.Core.Media.NativeFFmpeg;

/// <summary>
/// Optional encode knobs for <see cref="LibavClipExporter.TryExport"/>. Defaults reproduce the original
/// behaviour (H.264, quality-CRF ladder, <c>veryfast</c>, source resolution, audio kept), so existing
/// callers that pass no options are unaffected.
/// </summary>
internal sealed class LibavExportOptions
{
    /// <summary>Output video codec. H.265 falls back to H.264 if unavailable.</summary>
    public VideoCodec Codec { get; init; } = VideoCodec.H264;

    /// <summary>Average video bitrate (kbps) for ABR/target-size mode. 0 = use the quality CRF ladder.</summary>
    public int TargetVideoBitrateKbps { get; init; }

    /// <summary>
    /// Use true two-pass encoding for the target bitrate (more accurate size + better quality distribution
    /// than single-pass ABR). Only honoured for H.264 with <see cref="TargetVideoBitrateKbps"/> &gt; 0; H.265
    /// and CRF modes ignore it. Default false = single-pass.
    /// </summary>
    public bool TwoPass { get; init; }

    /// <summary>Explicit CRF (0-51) overriding the quality ladder. 0 = derive from <see cref="VideoQuality"/>.</summary>
    public int CrfOverride { get; init; }

    /// <summary>libx264/libx265 speed preset (ultrafast … placebo).</summary>
    public string Preset { get; init; } = "veryfast";

    /// <summary>Downscale so the output height ≤ this many pixels (keeping aspect, even dims). 0 = keep source.</summary>
    public int MaxHeight { get; init; }

    /// <summary>
    /// Cap the output frame rate to this many fps by dropping source frames (the output is CFR at the cap).
    /// 0 or ≥ source fps = keep the source rate.
    /// </summary>
    public int MaxFps { get; init; }

    /// <summary>
    /// Output rotation baked into the pixels: 0 / 90 / 180 / 270 degrees clockwise. 90/270 transpose the
    /// output dimensions. 0 = no rotation (the original behaviour), so existing callers are unaffected.
    /// </summary>
    public int RotationDegrees { get; init; }

    /// <summary>Drop the audio track entirely (smallest output, e.g. silent screen clips).</summary>
    public bool DropAudio { get; init; }

    /// <summary>AAC audio bitrate (kbps). 0 = derive from <see cref="VideoQuality"/> (the original behaviour).</summary>
    public int AudioBitrateKbps { get; init; }

    /// <summary>Output audio channels: 1 = mono mixdown, 2 = stereo. 0 = stereo (the original behaviour).</summary>
    public int AudioChannels { get; init; }
}
