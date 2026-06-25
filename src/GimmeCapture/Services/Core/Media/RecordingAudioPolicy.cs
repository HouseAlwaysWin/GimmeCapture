using System;

namespace GimmeCapture.Services.Core.Media;

public static class RecordingAudioPolicy
{
    public static bool ShouldRecordSystemAudio(bool requested, string targetFormat)
    {
        if (!requested)
        {
            return false;
        }

        return !string.Equals(targetFormat, "gif", StringComparison.OrdinalIgnoreCase);
    }

    public static bool ShouldRecordMicrophone(bool requested, string targetFormat)
    {
        if (!requested)
        {
            return false;
        }

        // GIF has no audio track, so skip the mic capture entirely.
        return !string.Equals(targetFormat, "gif", StringComparison.OrdinalIgnoreCase);
    }
}
