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
}
