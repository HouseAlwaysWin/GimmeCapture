using System;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;

namespace GimmeCapture.Services.Core.Media;

// Pure encode-math helpers for the Compress pipeline: CRF scale mapping, the bpp size estimate, and the
// target-size bitrate math. Extracted verbatim from MainWindowViewModel so tests can assert them in isolation.
internal static class CompressEncodeMath
{
    // AV1 (SVT-AV1) uses a 0-63 CRF scale that sits ~8 higher than x264/x265 for the same visual quality.
    // The compress UI keeps ONE slider on the x265 scale; this offset is folded in HERE when snapshotting, so
    // every downstream encode, estimate, and cache key sees the encoder-native value with no further remapping.
    internal const int Av1CrfOffset = 8;

    /// <summary>
    /// Maps the UI CRF (x265 scale) into the selected encoder's native scale: x264/x265 clamp to 1-51; AV1
    /// adds <see cref="Av1CrfOffset"/> and clamps to 1-63. Pure/static so tests can assert the mapping.
    /// </summary>
    internal static int EncoderScaleCrf(VideoCodec codec, int uiCrf) =>
        codec == VideoCodec.Av1
            ? Math.Clamp(uiCrf + Av1CrfOffset, 1, 63)
            : Math.Clamp(uiCrf, 1, 51);

    /// <summary>
    /// Rough output-size estimate (bytes) for CRF mode from source geometry/duration and the chosen knobs.
    /// Uses a bits-per-pixel model (≈ halves every +6 CRF; H.265 ≈ 0.6× H.264) so it is approximate and
    /// content-agnostic — directionally correct for comparing settings, not a guarantee. Pure/testable.
    /// </summary>
    internal static long EstimateOutputSizeBytes(
        int srcWidth, int srcHeight, int srcFps, double durationSeconds,
        int maxHeight, int maxFps, VideoCodec codec, int crf, int audioKbps)
    {
        if (durationSeconds <= 0 || srcWidth <= 0 || srcHeight <= 0)
        {
            return 0;
        }

        (int w, int h) = LibavClipExporter.ScaleToMaxHeight(srcWidth, srcHeight, maxHeight);
        int baseFps = srcFps > 0 ? srcFps : 30;
        int fps = (maxFps > 0 && maxFps < baseFps) ? maxFps : baseFps;

        // Per-codec bits/pixel reference at its CRF anchor. AV1 is anchored at CRF 31 (≈ x265 CRF 23 after the
        // +8 offset) and ~30% smaller than H.265; the incoming crf is already the encoder-native scale.
        (double bppRef, double anchor) = codec switch
        {
            VideoCodec.Av1 => (0.035, 31.0),
            VideoCodec.H265 => (0.050, 23.0),
            _ => (0.085, 23.0),
        };
        double bpp = bppRef * Math.Pow(2.0, -(crf - anchor) / 6.0);
        double videoBytes = (double)w * h * fps * bpp * durationSeconds / 8.0;
        double audioBytes = audioKbps > 0 ? audioKbps * 1000.0 / 8.0 * durationSeconds : 0;
        return (long)(videoBytes + audioBytes);
    }

    /// <summary>
    /// Turns a desired output size (MB) and a duration (seconds) into an average video bitrate (kbps),
    /// reserving a fixed audio allowance and a small safety margin. Single-pass ABR is approximate, so
    /// the real file lands near (usually a touch under) the target.
    /// </summary>
    internal static int ComputeTargetVideoBitrateKbps(double targetSizeMB, double durationSeconds)
    {
        if (targetSizeMB <= 0 || durationSeconds <= 0)
        {
            return 0;
        }

        const double audioKbps = 128;   // reserve for the AAC track the exporter muxes in
        const double safety = 0.97;     // headroom for container overhead / ABR overshoot

        double totalKbps = targetSizeMB * 1024.0 * 1024.0 * 8.0 / 1000.0 / durationSeconds;
        double videoKbps = (totalKbps - audioKbps) * safety;

        // Never go below a usable floor (very large files at long durations can compute tiny bitrates).
        return (int)Math.Max(50, Math.Floor(videoKbps));
    }

    /// <summary>
    /// After a first ABR pass overshot the target, scales the video bitrate by the measured
    /// video-bytes ratio (audio held at a fixed allowance) so a single corrective re-encode lands at or
    /// under the requested size. Returns the new kbps, floored, or the original if inputs are unusable.
    /// </summary>
    internal static int RefineTargetVideoBitrateKbps(
        int attemptedVideoKbps, double actualTotalBytes, double targetTotalBytes, double durationSeconds)
    {
        if (attemptedVideoKbps <= 0 || actualTotalBytes <= 0 || targetTotalBytes <= 0 || durationSeconds <= 0)
        {
            return attemptedVideoKbps;
        }

        const double audioKbps = 128;   // matches the reservation in ComputeTargetVideoBitrateKbps
        const double safety = 0.98;     // small extra headroom so the corrective pass stays under target

        double audioBytes = audioKbps * 1000.0 / 8.0 * durationSeconds;
        double actualVideoBytes = Math.Max(1, actualTotalBytes - audioBytes);
        double targetVideoBytes = Math.Max(1, targetTotalBytes - audioBytes);

        double refined = attemptedVideoKbps * (targetVideoBytes / actualVideoBytes) * safety;
        return (int)Math.Max(50, Math.Floor(refined));
    }
}
