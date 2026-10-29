using System;
using System.IO;
using System.Text;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;

namespace GimmeCapture.Tests;

/// <summary>
/// <see cref="LibavPinAudioPcmDecoder"/>: how its PCM buffer grows, and decoding one range of a file, as the clip
/// exporter builds an export's audio. The buffer's size doubled as an int, wrapped once it passed 1 GiB (about 93
/// minutes of 48 kHz stereo) and then spun forever, and a range decode ran on to the end of the file and kept only the
/// range. The decode tests use a WAV tone written in-process, so they need only the bundled FFmpeg and run everywhere.
/// </summary>
public sealed class LibavPinAudioPcmDecoderTests : IDisposable
{
    private const int SampleRate = 48_000;
    private const double Frequency = 440.0;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));

    public LibavPinAudioPcmDecoderTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(0, 1, 16 * 1024)]                 // starts at 16 KiB
    [InlineData(16 * 1024, 16 * 1024 + 1, 32 * 1024)]
    [InlineData(64 * 1024, 1_000_000, 1024 * 1024)]
    [InlineData(64 * 1024, 100, 64 * 1024)]       // already big enough
    public void GrowPcmCapacity_DoublesUntilItFits(int current, long required, int expected)
    {
        Assert.Equal(expected, LibavPinAudioPcmDecoder.GrowPcmCapacity(current, required));
    }

    [Fact]
    public void GrowPcmCapacity_PastOneGibibyte_StopsAtTheLargestArray()
    {
        // Doubled as an int, 1 GiB became int.MinValue and then 0, and the loop never ended.
        Assert.Equal(Array.MaxLength, LibavPinAudioPcmDecoder.GrowPcmCapacity(1 << 30, (1L << 30) + 1));
        Assert.Equal(Array.MaxLength, LibavPinAudioPcmDecoder.GrowPcmCapacity(1 << 30, Array.MaxLength));
    }

    [Fact]
    public void GrowPcmCapacity_BeyondTheLargestArray_Throws()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => LibavPinAudioPcmDecoder.GrowPcmCapacity(Array.MaxLength, (long)Array.MaxLength + 1));
        Assert.Contains("more than one buffer can hold", error.Message);
    }

    [Fact]
    public void RangeByteLimit_IsWholeStereoSampleFrames()
    {
        Assert.Equal(288_000, LibavPinAudioPcmDecoder.RangeByteLimit(1.5, SampleRate));
        Assert.Equal(0, LibavPinAudioPcmDecoder.RangeByteLimit(1.5, SampleRate) % 4);
        Assert.Equal(long.MaxValue, LibavPinAudioPcmDecoder.RangeByteLimit(double.PositiveInfinity, SampleRate));
    }

    [Fact]
    public void DecodeRange_ReturnsJustTheRange_FromItsStart()
    {
        string wav = WriteStereoTone(Path.Combine(_dir, "tone.wav"), seconds: 6.0);

        LibavPinAudioPcmDecoder.DecodeResult range = LibavPinAudioPcmDecoder.DecodeRange(wav, 2.25, 1.5);

        Assert.Equal(SampleRate, range.WaveFormat.SampleRate);
        Assert.Equal(LibavPinAudioPcmDecoder.RangeByteLimit(1.5, SampleRate), range.PcmBytes.Length);
        // The first samples are the tone at 2.25 s, not at the start of the file.
        for (int i = 0; i < 64; i++)
        {
            short expected = ToneAt(2.25 + ((double)i / SampleRate));
            short left = BitConverter.ToInt16(range.PcmBytes, i * 4);
            Assert.InRange(left, expected - 2, expected + 2);
        }
    }

    [Fact]
    public void DecodeRange_StopsReadingAtTheRangeEnd()
    {
        // Eight seconds of tone fill a whole number of the WAV reader's 4 KiB packets, so one stray byte after them comes
        // out as a packet of its own, which the decoder rejects. Decoding to the end fails on it; a range that ends well
        // before it can only succeed by not reading that far.
        string wav = WriteStereoTone(Path.Combine(_dir, "tail.wav"), seconds: 8.0, strayByte: true);
        var toTheEnd = Assert.Throws<FFmpegNativeException>(() => LibavPinAudioPcmDecoder.Decode(wav, 0));
        Assert.Equal(FFmpeg.AutoGen.ffmpeg.AVERROR_INVALIDDATA, toTheEnd.ErrorCode);

        LibavPinAudioPcmDecoder.DecodeResult range = LibavPinAudioPcmDecoder.DecodeRange(wav, 2.0, 3.0);

        Assert.Equal(LibavPinAudioPcmDecoder.RangeByteLimit(3.0, SampleRate), range.PcmBytes.Length);
    }

    [Fact]
    public void DecodeRange_PastTheEndOfTheAudio_ReturnsWhatThereIs()
    {
        string wav = WriteStereoTone(Path.Combine(_dir, "short.wav"), seconds: 2.0);

        LibavPinAudioPcmDecoder.DecodeResult range = LibavPinAudioPcmDecoder.DecodeRange(wav, 1.0, 5.0);

        Assert.Equal(LibavPinAudioPcmDecoder.RangeByteLimit(1.0, SampleRate), range.PcmBytes.Length);
    }

    [Fact]
    public void DecodeRange_WhenCancelled_Throws()
    {
        string wav = WriteStereoTone(Path.Combine(_dir, "cancel.wav"), seconds: 1.0);
        using var cancelled = new System.Threading.CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => LibavPinAudioPcmDecoder.DecodeRange(wav, 0, 1.0, cancelled.Token));
    }

    private static short ToneAt(double seconds) => (short)(Math.Sin(2 * Math.PI * Frequency * seconds) * 0.4 * short.MaxValue);

    // A 16-bit stereo tone in a WAVE_FORMAT_EXTENSIBLE file, which names its channels (front left and right) the way
    // most real audio does, optionally with one stray byte after the samples.
    private static string WriteStereoTone(string path, double seconds, bool strayByte = false)
    {
        int frames = (int)Math.Round(seconds * SampleRate);
        int dataBytes = (frames * 4) + (strayByte ? 1 : 0);
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(file);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(4 + (8 + 40) + (8 + dataBytes));
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(40);
        writer.Write(unchecked((short)0xFFFE)); // WAVE_FORMAT_EXTENSIBLE
        writer.Write((short)2);
        writer.Write(SampleRate);
        writer.Write(SampleRate * 4);
        writer.Write((short)4);
        writer.Write((short)16);
        writer.Write((short)22);  // size of the extension below
        writer.Write((short)16);  // valid bits per sample
        writer.Write(3);          // channel mask: front left, front right
        writer.Write(new Guid("00000001-0000-0010-8000-00aa00389b71").ToByteArray()); // KSDATAFORMAT_SUBTYPE_PCM
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataBytes);
        for (int i = 0; i < frames; i++)
        {
            short sample = ToneAt((double)i / SampleRate);
            writer.Write(sample);
            writer.Write(sample);
        }

        if (strayByte)
        {
            writer.Write((byte)0);
        }

        return path;
    }
}
