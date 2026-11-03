using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using Microsoft.Win32.SafeHandles;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace GimmeCapture.Services.Core.Media;

/// <summary>The sounds a recording captures, each into a WAV of its own for every segment.</summary>
internal enum RecordedSoundTrack
{
    System,
    Microphone,
}

/// <summary>
/// Encodes a recording's sound to AAC while it records, so the stop only has to finish it. Encoded at the stop, the
/// sound took 16-23 ms a second of it, nearly all in the encoder: 7 s after five minutes, half a minute or more after
/// half an hour.
/// <para>
/// It reads each segment's WAVs as the captures write them and puts together the sound the finalize puts together from
/// the finished files (<c>RecordingService.BuildFinalAudioAsync</c>): each track's valid segments end to end, system
/// audio and microphone mixed at 48 kHz stereo with the microphone's gain, through the same sample providers. A track's
/// read waits until the capture has written what it asks for, or ended the segment, so the providers get what reading
/// the finished files gives them. The finalize takes the result only when it came from the segments the finalize would
/// use, at its bitrate. A segment whose format is not its track's first, which the finalize resamples, ends the encode
/// here, and the finalize builds the sound as before.
/// </para>
/// </summary>
internal sealed class RecordingSoundEncoder : IDisposable
{
    /// <summary>How often a read waiting for the capture looks at the file again.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    private readonly object _lock = new();
    private readonly Track? _system;
    private readonly Track? _microphone;
    private readonly double _micVolume;
    private readonly string _outputPath;
    private readonly CancellationTokenSource _cancel = new();
    private readonly TaskCompletionSource<Result> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;

    // Counts the changes to what the recording handed over, so a read never waits out a poll for one it missed.
    private int _version;

    /// <summary>
    /// What was encoded, from which of each track's segments. <see cref="Path"/> is null when the encode did not finish,
    /// and <see cref="Failure"/> says why.
    /// </summary>
    internal sealed record Result(
        string? Path, IReadOnlyList<string> SystemSegments, IReadOnlyList<string> MicrophoneSegments, long BitRate,
        string? Failure);

    private RecordingSoundEncoder(
        string outputPath, string? systemSegment, string? microphoneSegment, double micVolume, long bitRate)
    {
        _outputPath = outputPath;
        _system = systemSegment == null ? null : new Track(systemSegment);
        _microphone = microphoneSegment == null ? null : new Track(microphoneSegment);
        _micVolume = micVolume;
        BitRate = bitRate;
        _thread = new Thread(Run) { IsBackground = true, Name = "RecordingSound" };
    }

    /// <summary>The bitrate the sound is encoded at.</summary>
    public long BitRate { get; }

    /// <summary>
    /// Starts encoding the sound of a recording whose first segment writes <paramref name="systemSegment"/> and
    /// <paramref name="microphoneSegment"/> (at least one), into <paramref name="outputPath"/>. Below normal priority, so
    /// it never takes time from the capture and the video's encode: it needs about one core in forty.
    /// </summary>
    public static RecordingSoundEncoder Start(
        string outputPath, string? systemSegment, string? microphoneSegment, double micVolume, long bitRate)
    {
        if (systemSegment == null && microphoneSegment == null)
        {
            throw new ArgumentException("A recording's sound needs a track.", nameof(systemSegment));
        }

        var encoder = new RecordingSoundEncoder(outputPath, systemSegment, microphoneSegment, micVolume, bitRate);
        encoder.TrySetPriority(ThreadPriority.BelowNormal);
        encoder._thread.Start();
        return encoder;
    }

    /// <summary>A resumed recording's capture of <paramref name="track"/> writes <paramref name="path"/>.</summary>
    public void AddSegment(RecordedSoundTrack track, string path)
    {
        lock (_lock)
        {
            if (TrackOf(track) is { Finished: false } t)
            {
                t.Ended = t.Segments.Count;
                t.Segments.Add(path);
                Changed();
            }
        }
    }

    /// <summary>The capture of <paramref name="track"/> has closed its segment's WAV.</summary>
    public void EndSegment(RecordedSoundTrack track)
    {
        lock (_lock)
        {
            if (TrackOf(track) is { } t && t.Ended < t.Segments.Count)
            {
                t.Ended = t.Segments.Count;
                Changed();
            }
        }
    }

    /// <summary>No more segments come for <paramref name="track"/>: its capture did not start again on a resume.</summary>
    public void EndTrack(RecordedSoundTrack track)
    {
        lock (_lock)
        {
            if (TrackOf(track) is { } t)
            {
                End(t);
                Changed();
            }
        }
    }

    /// <summary>
    /// The recording stopped and closed its WAVs: the encode reads them to their end and finishes. The stop waits on it,
    /// so from here it runs at normal priority.
    /// </summary>
    public Task<Result> FinishAsync()
    {
        lock (_lock)
        {
            End(_system);
            End(_microphone);
            Changed();
        }

        TrySetPriority(ThreadPriority.Normal);
        return _done.Task;
    }

    /// <summary>
    /// Stops an encode that has not finished, which deletes what it wrote. A finished encode's file is the caller's.
    /// </summary>
    public void Dispose()
    {
        if (!_done.Task.IsCompleted)
        {
            _cancel.Cancel();
            lock (_lock)
            {
                Changed();
            }

            // Below normal, it could wait seconds for the processor on a busy machine before it sees the cancel.
            TrySetPriority(ThreadPriority.Normal);
        }

        // A waiting read sees the cancel at once, an encode before its next frame.
        if (!_thread.IsAlive || _thread.Join(TimeSpan.FromSeconds(2)))
        {
            _cancel.Dispose();
        }
    }

    /// <summary>
    /// The sound <c>RecordingService.BuildFinalAudioAsync</c> builds from the same tracks, through the same providers: one
    /// track as it is, the microphone alone with its gain, or both mixed at 48 kHz stereo. The finalize writes the mix,
    /// and the microphone with a gain, as 16-bit WAVs that the AAC encode then reads, so those are taken through 16-bit
    /// here too.
    /// </summary>
    internal static ISampleProvider Compose(ISampleProvider? system, ISampleProvider? microphone, double micVolume)
    {
        if (microphone == null)
        {
            return system ?? throw new ArgumentNullException(nameof(system));
        }

        if (system == null)
        {
            return RecordingService.KeepsMicrophoneAsRecorded(micVolume)
                ? microphone
                : As16Bit(new VolumeSampleProvider(microphone) { Volume = (float)micVolume });
        }

        var systemMix = RecordingService.NormalizeSampleProvider(system, RecordingService.MixRate, RecordingService.MixChannels);
        var micMix = RecordingService.NormalizeSampleProvider(microphone, RecordingService.MixRate, RecordingService.MixChannels);
        if (!systemMix.WaveFormat.Equals(micMix.WaveFormat))
        {
            // Surround system audio: the finalize keeps it and leaves the microphone out.
            throw new NotEncodedHereException(
                $"{system.WaveFormat.Channels}-channel system audio does not mix with the microphone");
        }

        return As16Bit(new DeterministicMixSampleProvider(systemMix, micMix, (float)micVolume));
    }

    // What a 16-bit WAV of the sound reads back as.
    private static ISampleProvider As16Bit(ISampleProvider sound) => new SampleToWaveProvider16(sound).ToSampleProvider();

    private void Run()
    {
        TrackReader? system = null;
        TrackReader? microphone = null;
        Result result;
        try
        {
            system = _system == null ? null : new TrackReader(this, _system, "system");
            microphone = _microphone == null ? null : new TrackReader(this, _microphone, "microphone");
            system?.Open();
            microphone?.Open();
            LibavAacTranscoder.EncodeToM4a(Compose(system, microphone, _micVolume), _outputPath, BitRate, _cancel.Token);
            result = new Result(_outputPath, system?.Included ?? [], microphone?.Included ?? [], BitRate, Failure: null);
        }
        catch (Exception ex)
        {
            TryDelete(_outputPath);
            if (ex is not (NotEncodedHereException or OperationCanceledException))
            {
                AppLog.Warning("Recording.SoundEncoder", ex); // the finalize encodes it from the segments instead
            }

            result = new Result(null, [], [], BitRate, ex is NotEncodedHereException ? ex.Message : ex.GetType().Name);
        }
        finally
        {
            system?.Dispose();
            microphone?.Dispose();
        }

        _done.TrySetResult(result);
    }

    private Track? TrackOf(RecordedSoundTrack track) => track == RecordedSoundTrack.System ? _system : _microphone;

    private static void End(Track? track)
    {
        if (track != null)
        {
            track.Ended = track.Segments.Count;
            track.Finished = true;
        }
    }

    // Under the lock.
    private void Changed()
    {
        _version++;
        Monitor.PulseAll(_lock);
    }

    private int Version
    {
        get
        {
            lock (_lock)
            {
                return _version;
            }
        }
    }

    // Waits for the recording to hand something over, or for a poll's length, unless it already did since `seen`.
    private void Wait(int seen)
    {
        lock (_lock)
        {
            if (_version == seen && !_cancel.IsCancellationRequested)
            {
                Monitor.Wait(_lock, PollInterval);
            }
        }

        _cancel.Token.ThrowIfCancellationRequested();
    }

    // Best effort: a thread that finished, or a platform without priorities, runs the encode as it is.
    private void TrySetPriority(ThreadPriority priority)
    {
        try
        {
            _thread.Priority = priority;
        }
        catch (Exception ex)
        {
            AppLog.Debug("Recording.SoundEncoderPriority", ex);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            AppLog.Debug("Recording.SoundEncoderDelete", ex); // it is in the session folder, which goes after finalize
        }
    }

    // A track's segments as the recording hands them over. Guarded by the encoder's lock.
    private sealed class Track(string firstSegment)
    {
        public readonly List<string> Segments = [firstSegment];

        // The segments before this one have their WAVs closed.
        public int Ended;

        // No segment comes after the last.
        public bool Finished;
    }

    // The encode stops, and the finalize builds the sound from the segments the way it always did.
    private sealed class NotEncodedHereException(string message) : Exception(message);

    /// <summary>
    /// A track's valid segments end to end, as the finalize's merge joins them, read as NAudio reads 16-bit PCM. Opens a
    /// segment once it holds enough sound to count (<see cref="RecordingService.HoldsSound"/>), or skips it once it ended
    /// without; reads wait for the capture to write more.
    /// </summary>
    private sealed class TrackReader(RecordingSoundEncoder owner, Track track, string name) : ISampleProvider, IDisposable
    {
        private int _next;
        private int _segment;
        private SafeFileHandle? _file;
        private long _dataStart;
        private long _position;
        private int _blockAlign;
        private byte[] _bytes = [];

        public List<string> Included { get; } = [];

        public WaveFormat WaveFormat { get; private set; } = null!;

        /// <summary>Waits for the track's first segment that counts, which gives the track its format.</summary>
        public void Open()
        {
            if (!OpenNextSegment())
            {
                throw new NotEncodedHereException($"no {name} segment holds sound");
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int filled = 0;
            while (filled < count)
            {
                if (_file == null && !OpenNextSegment())
                {
                    break;
                }

                long available = WaitForData();
                if (available == 0)
                {
                    CloseSegment();
                    continue;
                }

                int bytes = (int)Math.Min((long)(count - filled) * 2, available);
                if (_bytes.Length < bytes)
                {
                    _bytes = new byte[bytes];
                }

                int read = ReadAt(_bytes.AsSpan(0, bytes), _dataStart + _position);
                if (read < 2)
                {
                    CloseSegment(); // shorter than it said: the finalize's reader stops there too
                    continue;
                }

                int samples = read / 2;
                for (int i = 0; i < samples; i++)
                {
                    buffer[offset + filled + i] = BinaryPrimitives.ReadInt16LittleEndian(_bytes.AsSpan(i * 2)) / 32768f;
                }

                _position += samples * 2L;
                filled += samples;
            }

            return filled;
        }

        public void Dispose() => CloseSegment();

        // Opens the track's next segment that counts, waiting for the recording to hand one over. False at its end.
        private bool OpenNextSegment()
        {
            while (true)
            {
                string path;
                int seen = owner.Version;
                lock (owner._lock)
                {
                    if (_next >= track.Segments.Count)
                    {
                        if (track.Finished)
                        {
                            return false;
                        }

                        path = string.Empty;
                    }
                    else
                    {
                        path = track.Segments[_next];
                    }
                }

                if (path.Length == 0)
                {
                    owner.Wait(seen);
                    continue;
                }

                int index = _next++;
                if (TryOpenSegment(path, index))
                {
                    Included.Add(path);
                    return true;
                }
            }
        }

        // Waits until the segment holds enough sound to count, or ended without: then it is skipped, as the finalize
        // skips it. A format that differs from the track's first segment's ends the encode.
        private bool TryOpenSegment(string path, int index)
        {
            SafeFileHandle? file = null;
            (WaveFormat Format, long DataStart)? header = null;
            try
            {
                while (true)
                {
                    int seen = owner.Version;
                    bool ended = HasEnded(index); // before the file's length, so an ended file's length is its last
                    file ??= TryOpenFile(path);
                    header ??= file == null ? null : ReadHeader(file);
                    if (header is { } found)
                    {
                        long data = ended
                            ? EndedDataLength(file!, found.DataStart)
                            : RandomAccess.GetLength(file!) - found.DataStart;
                        if (RecordingService.HoldsSound(data, found.Format))
                        {
                            Take(path, file!, found.Format, found.DataStart, index);
                            file = null;
                            return true;
                        }

                        if (ended)
                        {
                            return false;
                        }
                    }
                    else if (ended)
                    {
                        return false; // missing or unreadable: not a segment the finalize can read either
                    }

                    owner.Wait(seen);
                }
            }
            finally
            {
                file?.Dispose();
            }
        }

        private void Take(string path, SafeFileHandle file, WaveFormat format, long dataStart, int index)
        {
            if (format.Encoding != WaveFormatEncoding.Pcm || format.BitsPerSample != 16)
            {
                throw new NotEncodedHereException($"{Path.GetFileName(path)} is not 16-bit PCM ({format})");
            }

            var floats = WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.Channels);
            if (WaveFormat == null)
            {
                WaveFormat = floats;
            }
            else if (!floats.Equals(WaveFormat))
            {
                throw new NotEncodedHereException(
                    $"{Path.GetFileName(path)} is {format.SampleRate} Hz {format.Channels} ch, where the {name} track " +
                    $"began {WaveFormat.SampleRate} Hz {WaveFormat.Channels} ch");
            }

            _file = file;
            _segment = index;
            _dataStart = dataStart;
            _position = 0;
            _blockAlign = format.BlockAlign;
        }

        // Bytes of the open segment's data that are not read yet, waiting while there are none and the capture still
        // writes it. 0: the segment is done.
        private long WaitForData()
        {
            while (true)
            {
                int seen = owner.Version;
                bool ended = HasEnded(_segment);
                long end = ended
                    ? EndedDataLength(_file!, _dataStart) & ~1L
                    : (RandomAccess.GetLength(_file!) - _dataStart) / _blockAlign * _blockAlign;
                if (end > _position)
                {
                    return end - _position;
                }

                if (ended)
                {
                    return 0;
                }

                owner.Wait(seen);
            }
        }

        private bool HasEnded(int index)
        {
            lock (owner._lock)
            {
                return index < track.Ended;
            }
        }

        private int ReadAt(Span<byte> destination, long fileOffset)
        {
            int total = 0;
            while (total < destination.Length)
            {
                int read = RandomAccess.Read(_file!, destination[total..], fileOffset + total);
                if (read <= 0)
                {
                    break;
                }

                total += read;
            }

            return total & ~1;
        }

        private void CloseSegment()
        {
            _file?.Dispose();
            _file = null;
        }

        // Shared for writing: the capture still has the file open for it.
        private static SafeFileHandle? TryOpenFile(string path)
        {
            try
            {
                return File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null; // not there yet, or locked: looked at again until its segment ends
            }
        }

        // An ended segment's data as the finalize's WaveFileReader reads it: what the header says, up to the file's end.
        private static long EndedDataLength(SafeFileHandle file, long dataStart)
        {
            Span<byte> size = stackalloc byte[4];
            long inFile = RandomAccess.GetLength(file) - dataStart;
            return RandomAccess.Read(file, size, dataStart - 4) == 4
                ? Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(size), inFile)
                : inFile;
        }

        // The format, and where the data starts, once the writer's header is in the file; null until then.
        private static (WaveFormat Format, long DataStart)? ReadHeader(SafeFileHandle file)
        {
            byte[] head = new byte[(int)Math.Min(RandomAccess.GetLength(file), 4096)];
            int length = RandomAccess.Read(file, head, 0);
            if (length < 12)
            {
                return null;
            }

            using var reader = new BinaryReader(new MemoryStream(head, 0, length));
            uint riff = reader.ReadUInt32();
            reader.ReadUInt32(); // the RIFF chunk's size, written when the writer closes
            if (riff != 0x46464952 || reader.ReadUInt32() != 0x45564157) // "RIFF", "WAVE"
            {
                throw new NotEncodedHereException("a segment is not a WAV");
            }

            WaveFormat? format = null;
            while (reader.BaseStream.Position + 8 <= length)
            {
                uint id = reader.ReadUInt32();
                int size = reader.ReadInt32();
                if (id == 0x61746164) // "data"
                {
                    return format == null ? null : (format, reader.BaseStream.Position);
                }

                long next = reader.BaseStream.Position + size;
                if (size < 0 || next > length)
                {
                    return null; // the rest of the header is not in the file yet
                }

                if (id == 0x20746D66) // "fmt "
                {
                    format = WaveFormat.FromFormatChunk(reader, size);
                }

                reader.BaseStream.Position = next;
            }

            return null;
        }
    }
}
