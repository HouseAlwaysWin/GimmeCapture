using System;
using System.IO;
using System.Threading;
using NAudio.Wave;

namespace GimmeCapture.Services.Core.Media.NativeFFmpeg;

/// <summary>
/// A 16-bit stereo WAV that ranges of a file's audio are written into as they are decoded, so that no range is held
/// whole. An export's audio was decoded into an array, copied into a stream and copied out again before it was written:
/// for a 30-minute track at 48 kHz, 330 MB of PCM, 1-2 GB allocated and up to three copies held at once, where this
/// allocates about 7 MB. The file is made at the first audio's format, and <see cref="Dispose"/> finishes its header.
/// </summary>
internal sealed class DecodedWavFile : IDisposable
{
    // The decoder hands over a few kilobytes at a time (an AAC frame is 4 KB of PCM); through the file's default 4 KB
    // buffer each of those was a write of its own, 84,000 for half an hour.
    private const int FileBufferBytes = 1 << 20;

    private readonly string _path;
    private WaveFileWriter? _writer;

    public DecodedWavFile(string path)
    {
        _path = path;
    }

    /// <summary>The PCM bytes written so far; 0 while no audio has come.</summary>
    public long BytesWritten { get; private set; }

    /// <summary>
    /// Appends <paramref name="durationSeconds"/> of <paramref name="sourcePath"/>'s audio from <paramref
    /// name="startSeconds"/>, to the end of the track for <see cref="double.PositiveInfinity"/>. A file with no audio
    /// stream throws, as <see cref="LibavPinAudioPcmDecoder.DecodeRange"/> does.
    /// </summary>
    public void AppendRange(string sourcePath, double startSeconds, double durationSeconds, CancellationToken ct) =>
        LibavPinAudioPcmDecoder.DecodeRangeStreaming(
            sourcePath,
            startSeconds,
            durationSeconds,
            onFormat: format => _writer ??= Create(format),
            onPcm: Write,
            ct);

    /// <summary>Appends PCM decoded already, in <paramref name="format"/>: a range the tempo filter retimed.</summary>
    public void Append(byte[] pcm, WaveFormat format)
    {
        _writer ??= Create(format);
        Write(pcm, pcm.Length);
    }

    private WaveFileWriter Create(WaveFormat format) =>
        new(new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.Read, FileBufferBytes), format);

    private void Write(byte[] buffer, int count)
    {
        _writer!.Write(buffer, 0, count);
        BytesWritten += count;
    }

    public void Dispose()
    {
        _writer?.Dispose();
        _writer = null;
    }
}
