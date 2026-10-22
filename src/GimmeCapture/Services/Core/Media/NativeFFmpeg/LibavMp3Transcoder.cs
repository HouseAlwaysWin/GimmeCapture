using FFmpeg.AutoGen;
using GimmeCapture.Models;

namespace GimmeCapture.Services.Core.Media.NativeFFmpeg;

/// <summary>
/// WAV -> MP3 via libmp3lame (shipped in the bundled BtbN GPL FFmpeg build). The encode itself is
/// <see cref="LibavAudioEncoder"/>, with the same bitrates and channel choice as <see cref="LibavAacTranscoder"/>.
/// </summary>
internal static class LibavMp3Transcoder
{
    private static readonly LibavAudioEncoder.Target Mp3 = new(
        "mp3", "libmp3lame", AVCodecID.AV_CODEC_ID_MP3, AVSampleFormat.AV_SAMPLE_FMT_FLTP, FallbackFrameSize: 1024, "mp3");

    public static void EncodeWavToMp3(
        string wavPath, string mp3Path, VideoQuality quality, int bitrateKbps = 0, int targetChannels = 0)
    {
        LibavAudioEncoder.EncodeWav(
            wavPath, mp3Path, Mp3, LibavAudioEncoder.BitRate(quality, bitrateKbps, lowKbps: 96),
            targetChannels == 1 ? 1 : 2);
    }
}
