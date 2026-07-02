using System;
using System.IO;
using System.Text;

namespace GimmeCapture.Tests;

/// <summary>
/// Writes a tiny synthetic PCM s16le WAV file (a sine tone) for exercising the audio transcoders.
/// Standalone (no NAudio) so tests stay dependency-light; the produced file is a standard 44-byte-header
/// WAV that NAudio's WaveFileReader (used inside the transcoders) reads back.
/// </summary>
internal static class WavTestAudio
{
    /// <summary>Writes a <paramref name="seconds"/>-long stereo/mono sine WAV to <paramref name="path"/> and returns it.</summary>
    public static string WriteSineWav(
        string path,
        double seconds = 1.0,
        int sampleRate = 48_000,
        int channels = 2,
        double frequency = 440.0)
    {
        int totalFrames = Math.Max(1, (int)(seconds * sampleRate));
        short[] interleaved = new short[totalFrames * channels];
        for (int i = 0; i < totalFrames; i++)
        {
            double t = (double)i / sampleRate;
            short sample = (short)(Math.Sin(2 * Math.PI * frequency * t) * 0.4 * short.MaxValue);
            for (int c = 0; c < channels; c++)
            {
                interleaved[(i * channels) + c] = sample;
            }
        }

        WritePcm16Wav(path, interleaved, sampleRate, channels);
        return path;
    }

    private static void WritePcm16Wav(string path, short[] interleaved, int sampleRate, int channels)
    {
        const int bitsPerSample = 16;
        const int bytesPerSample = bitsPerSample / 8;
        int dataBytes = interleaved.Length * bytesPerSample;
        int byteRate = sampleRate * channels * bytesPerSample;
        short blockAlign = (short)(channels * bytesPerSample);

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs);

        // RIFF chunk
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + dataBytes);                 // ChunkSize = 36 + Subchunk2Size
        w.Write(Encoding.ASCII.GetBytes("WAVE"));

        // fmt subchunk (PCM)
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);                             // Subchunk1Size for PCM
        w.Write((short)1);                       // AudioFormat = PCM
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(byteRate);
        w.Write(blockAlign);
        w.Write((short)bitsPerSample);

        // data subchunk
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(dataBytes);
        foreach (short s in interleaved)
        {
            w.Write(s);                          // BinaryWriter is little-endian (s16le)
        }
    }
}
