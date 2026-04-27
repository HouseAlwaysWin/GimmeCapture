using System;

namespace GimmeCapture.Services.Core.Media.NativeFFmpeg;

/// <summary>Thrown when libav* returns a negative AVERROR code.</summary>
public sealed class FFmpegNativeException : Exception
{
    public int ErrorCode { get; }

    public FFmpegNativeException(int errorCode, string message) : base(message)
    {
        ErrorCode = errorCode;
    }
}
