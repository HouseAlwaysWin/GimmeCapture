using System;

namespace GimmeCapture.Services.Core.Media;

public static class RecordingAudioPolicy
{
    // Audio is captured for EVERY format, including gif. A gif file can't hold an audio track, but the recording
    // keeps an audio-bearing mp4 sidecar (see RecordingService gif finalize) so a pinned gif recording still
    // plays sound and non-gif re-exports keep it — only the saved .gif itself is silent.
    public static bool ShouldRecordSystemAudio(bool requested, string targetFormat)
    {
        _ = targetFormat;
        return requested;
    }

    public static bool ShouldRecordMicrophone(bool requested, string targetFormat)
    {
        _ = targetFormat;
        return requested;
    }
}
