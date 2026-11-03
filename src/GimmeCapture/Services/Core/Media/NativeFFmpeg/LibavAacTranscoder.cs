using System.Threading;
using FFmpeg.AutoGen;
using GimmeCapture.Models;

namespace GimmeCapture.Services.Core.Media.NativeFFmpeg;

/// <summary>
/// WAV -> AAC-in-MP4/M4A for muxing into MP4/MOV outputs. The encode itself is <see cref="LibavAudioEncoder"/>.
/// </summary>
internal static class LibavAacTranscoder
{
    private static readonly LibavAudioEncoder.Target Aac = new(
        "mp4", "aac", AVCodecID.AV_CODEC_ID_AAC, AVSampleFormat.AV_SAMPLE_FMT_FLTP, FallbackFrameSize: 1024, "aac");

    /// <param name="bitrateKbps">Overrides the bitrate <paramref name="quality"/> picks (192/128/96 kbps).</param>
    /// <param name="targetChannels">1 mixes down to mono; 0 or 2 keeps stereo.</param>
    /// <param name="ct">Checked before every frame.</param>
    /// <param name="pauseGate">While reset, the encode waits before its next frame.</param>
    public static void EncodeWavToM4a(
        string wavPath, string m4aPath, VideoQuality quality, int bitrateKbps = 0, int targetChannels = 0,
        CancellationToken ct = default, ManualResetEventSlim? pauseGate = null)
    {
        LibavAudioEncoder.EncodeWav(
            wavPath, m4aPath, Aac, LibavAudioEncoder.BitRate(quality, bitrateKbps, lowKbps: 96),
            targetChannels == 1 ? 1 : 2, ct, pauseGate);
    }
}
