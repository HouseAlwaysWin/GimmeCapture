using System;
using FFmpeg.AutoGen;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;

namespace GimmeCapture.Services.Core.Media;

public static class RecordingFormatCapabilities
{
    public static bool IsGifAvailable()
    {
        try
        {
            FFmpegRuntime.EnsureInitialized();
            unsafe
            {
                return ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_GIF) != null;
            }
        }
        catch
        {
            return false;
        }
    }
}
