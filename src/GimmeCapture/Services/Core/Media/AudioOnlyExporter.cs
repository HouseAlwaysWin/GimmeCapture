using System;
using System.IO;
using System.Threading;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using NAudio.Wave;

namespace GimmeCapture.Services.Core.Media;

/// <summary>
/// Extracts a video's audio track into a standalone audio file (WAV / MP3 / M4A-AAC / OGG-Opus).
/// Whole-file only: editor keep-ranges/speed/crop are video-editing concepts and are not applied here
/// (mirrors GifWebmVideoExporter's role as a format tail, but sources the ORIGINAL file's audio).
/// </summary>
internal static class AudioOnlyExporter
{
    /// <summary>Output extensions handled by the audio-only pipeline branch.</summary>
    public static bool IsAudioOnlyExtension(string ext) =>
        ext is ".wav" or ".mp3" or ".m4a" or ".ogg";

    /// <summary>
    /// Decodes the source's audio to PCM and encodes it per the output extension. Returns true when a
    /// non-empty file is produced; false when the source has no decodable audio.
    /// </summary>
    public static bool ExtractAudio(
        string sourcePath, string outputPath, string tempDir, VideoQuality quality,
        int bitrateKbps, int channels, CancellationToken ct)
    {
        LibavPinAudioPcmDecoder.DecodeResult pcm = LibavPinAudioPcmDecoder.Decode(sourcePath, 0, ct);
        if (pcm.PcmBytes.Length == 0)
        {
            return false;
        }

        ct.ThrowIfCancellationRequested();

        string ext = Path.GetExtension(outputPath).ToLowerInvariant();
        if (ext == ".wav")
        {
            using (var writer = new WaveFileWriter(outputPath, pcm.WaveFormat))
            {
                writer.Write(pcm.PcmBytes, 0, pcm.PcmBytes.Length);
            }

            return File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
        }

        // Compressed targets go via a temp WAV into the WAV→codec transcoders.
        string wav = Path.Combine(tempDir, "extract.wav");
        using (var writer = new WaveFileWriter(wav, pcm.WaveFormat))
        {
            writer.Write(pcm.PcmBytes, 0, pcm.PcmBytes.Length);
        }

        ct.ThrowIfCancellationRequested();

        switch (ext)
        {
            case ".mp3":
                LibavMp3Transcoder.EncodeWavToMp3(wav, outputPath, quality, bitrateKbps, channels);
                break;
            case ".m4a":
                LibavAacTranscoder.EncodeWavToM4a(wav, outputPath, quality, bitrateKbps, channels);
                break;
            case ".ogg":
                LibavOpusTranscoder.EncodeWavToOpusOgg(wav, outputPath, quality);
                break;
            default:
                throw new NotSupportedException($"Unsupported audio-only extension: {ext}");
        }

        return File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
    }
}
