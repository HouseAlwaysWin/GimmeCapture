using System.Runtime.InteropServices;
using System.Text;
using FFmpeg.AutoGen;

namespace GimmeCapture.Services.Core.Media.NativeFFmpeg;

internal static unsafe class FFmpegErrors
{
    internal static string Describe(int err)
    {
        if (err >= 0)
        {
            return "OK";
        }

        var buf = stackalloc byte[512];
        ffmpeg.av_strerror(err, buf, (ulong)512);
        var s = Marshal.PtrToStringAnsi((nint)buf);
        return string.IsNullOrEmpty(s) ? $"AVERROR({err})" : s;
    }

    internal static FFmpegNativeException ToException(int err, string context)
    {
        return new FFmpegNativeException(err, $"{context}: {Describe(err)} ({err})");
    }
}
