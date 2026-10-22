using FFmpeg.AutoGen;
using GimmeCapture.Models;

namespace GimmeCapture.Services.Core.Media.NativeFFmpeg;

/// <summary>
/// WAV -> stereo Opus-in-OGG for muxing into WebM. The encode itself is <see cref="LibavAudioEncoder"/>.
/// </summary>
internal static class LibavOpusTranscoder
{
    // libopus takes interleaved samples only (s16 or flt), and its frames are 20 ms: 960 samples at 48 kHz.
    private static readonly LibavAudioEncoder.Target Opus = new(
        "ogg", "libopus", AVCodecID.AV_CODEC_ID_OPUS, AVSampleFormat.AV_SAMPLE_FMT_FLT, FallbackFrameSize: 960, "opus");

    public static void EncodeWavToOpusOgg(string wavPath, string oggPath, VideoQuality quality)
    {
        // Opus holds up at a lower bitrate than AAC, so Low is 64 kbps rather than 96.
        LibavAudioEncoder.EncodeWav(
            wavPath, oggPath, Opus, LibavAudioEncoder.BitRate(quality, overrideKbps: 0, lowKbps: 64), channels: 2);
    }
}
