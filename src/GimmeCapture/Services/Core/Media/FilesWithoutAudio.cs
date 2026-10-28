using System;
using System.Collections.Generic;
using System.IO;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Services.Core.Media;

/// <summary>
/// The files a preview audio player found to have no audio stream, so that starting one opens and logs nothing. Media
/// Foundation fails on such a file (MF_E_INVALIDSTREAMNUMBER, 0xC00D36B3) as on a real fault, and FFmpeg fails after
/// it, so a pinned silent recording logged a warning at every play and seek. Media Foundation also fails on files whose
/// audio only FFmpeg can read, so a failure alone proves nothing: the first time a file fails to start, a probe tells
/// whether it has an audio stream at all, and the answer is kept. A file without one is skipped from then on; a file
/// with one keeps its warning and its FFmpeg fallback. Nothing is probed until something fails.
/// </summary>
internal sealed class FilesWithoutAudio
{
    private readonly Func<string, bool> _hasAudioStream;
    private readonly object _gate = new();

    // What the probe said of each file: true when it has an audio stream.
    private readonly Dictionary<string, bool> _probed = new(StringComparer.Ordinal);

    /// <param name="hasAudioStream">The probe: true when the file has an audio stream; throws when it cannot tell.</param>
    public FilesWithoutAudio(Func<string, bool> hasAudioStream)
    {
        _hasAudioStream = hasAudioStream;
    }

    /// <summary>True when an earlier failure showed that <paramref name="path"/> has no audio stream. Never probes.</summary>
    public bool Contains(string path)
    {
        lock (_gate)
        {
            return _probed.TryGetValue(path, out bool hasAudio) && !hasAudio;
        }
    }

    /// <summary>
    /// For a file whose audio failed to start: probes it, the first time, and adds it when it has no audio stream. True
    /// when it has none. A file the probe cannot read counts as having audio until its next failure probes it again.
    /// </summary>
    public bool AddIfSilent(string path)
    {
        lock (_gate)
        {
            if (_probed.TryGetValue(path, out bool probedHasAudio))
            {
                return !probedHasAudio;
            }

            bool hasAudio;
            try
            {
                hasAudio = _hasAudioStream(path);
            }
            catch (Exception ex)
            {
                AppLog.Debug("PinAudio.AudioStreamProbe", ex);
                return false;
            }

            _probed[path] = hasAudio;
            if (!hasAudio)
            {
                AppLog.Debug("PinAudio.NoAudioStream", $"a {Path.GetExtension(path)} file without an audio stream: its preview plays silent");
            }

            return !hasAudio;
        }
    }
}
