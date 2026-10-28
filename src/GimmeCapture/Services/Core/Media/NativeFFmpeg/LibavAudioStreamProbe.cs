using FFmpeg.AutoGen;

namespace GimmeCapture.Services.Core.Media.NativeFFmpeg;

/// <summary>
/// Whether a media file has an audio stream at all, read from its container on the caller's thread. Unlike
/// <see cref="LibavVideoFramePlayer.ProbeHasAudioAsync"/> it does not queue on the thread pool, which a playing video's
/// decode loops keep busy: the preview audio player asks from its own worker, which may block.
/// </summary>
internal static class LibavAudioStreamProbe
{
    /// <summary>True when <paramref name="path"/> has an audio stream, false when it has none. Throws when FFmpeg cannot read the file.</summary>
    public static bool HasAudioStream(string path)
    {
        FFmpegRuntime.EnsureInitialized();
        return HasAudioStreamUnsafe(path);
    }

    private static unsafe bool HasAudioStreamUnsafe(string path)
    {
        AVFormatContext* fmt = null;
        try
        {
            ThrowIfErr(ffmpeg.avformat_open_input(&fmt, path, null, null), "audio_probe_open");
            ThrowIfErr(ffmpeg.avformat_find_stream_info(fmt, null), "audio_probe_find_stream_info");
            return ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0) >= 0;
        }
        finally
        {
            if (fmt != null)
            {
                ffmpeg.avformat_close_input(&fmt);
            }
        }
    }

    private static void ThrowIfErr(int err, string context)
    {
        if (err < 0)
        {
            throw FFmpegErrors.ToException(err, context);
        }
    }
}
