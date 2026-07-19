using FFmpeg.AutoGen;
using GimmeCapture.Models;

namespace GimmeCapture.Services.Core.Media.NativeFFmpeg;

/// <summary>
/// WAV -> MP3 via libmp3lame (shipped in the bundled BtbN GPL FFmpeg build). Thin wrapper over the
/// codec-agnostic encode core in <see cref="LibavAacTranscoder"/>.
/// </summary>
internal static class LibavMp3Transcoder
{
    public static void EncodeWavToMp3(
        string wavPath, string mp3Path, VideoQuality quality, int bitrateKbps = 0, int targetChannels = 0)
    {
        LibavAacTranscoder.EncodeWavToAudioFile(
            wavPath, mp3Path, "mp3", "libmp3lame", AVCodecID.AV_CODEC_ID_MP3,
            quality, bitrateKbps, targetChannels, "mp3");
    }
}
