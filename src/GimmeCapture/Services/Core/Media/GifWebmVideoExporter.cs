using System;
using System.IO;
using System.Threading;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using NAudio.Wave;

namespace GimmeCapture.Services.Core.Media;

/// <summary>
/// Transcodes an ALREADY-edited/processed video file (trim/crop/rotate/filters/downscale/fps/burn-in already
/// baked in) into GIF or WebM. The GIF/WebM encoders take a whole file and apply no edits, so callers must
/// pre-process via <see cref="LibavClipExporter"/> first. Mirrors the pin's ExportGifWebmInProcessAsync tail so
/// the pin and the compress pipeline share one transcode code path.
/// </summary>
internal static class GifWebmVideoExporter
{
    /// <summary>
    /// GIF via the two-pass palette encoder. <paramref name="maxWidth"/> 0 keeps the source width;
    /// <paramref name="gifFps"/> decimates to that rate. Returns true when a non-empty file is produced.
    /// </summary>
    public static bool TranscodeToGif(string sourcePath, string outputPath, int gifFps, int maxWidth)
    {
        LibavGifTranscoder.TranscodeToGif(sourcePath, outputPath, gifFps, maxWidth);
        return File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
    }

    /// <summary>
    /// WebM: VP9 video at the source fps, then (when <paramref name="keepAudio"/>) decode the audio to PCM,
    /// write a WAV, and mux it as Opus — otherwise video-only. Uses <paramref name="tempDir"/> for the
    /// intermediate video-only WebM and WAV. Returns true when a non-empty file is produced.
    /// </summary>
    public static bool TranscodeToWebm(
        string sourcePath, string outputPath, string tempDir, VideoQuality quality, bool keepAudio, CancellationToken ct)
    {
        int webmFps = Math.Clamp(LibavClipExporter.ProbeFps(sourcePath), 1, 60);
        string videoOnly = Path.Combine(tempDir, "video.webm");
        LibavWebmTranscoder.TranscodeToWebm(sourcePath, videoOnly, webmFps, quality);
        if (!File.Exists(videoOnly) || new FileInfo(videoOnly).Length == 0)
        {
            return false;
        }

        if (keepAudio)
        {
            try
            {
                LibavPinAudioPcmDecoder.DecodeResult pcm = LibavPinAudioPcmDecoder.Decode(sourcePath, 0, ct);
                if (pcm.PcmBytes.Length > 0)
                {
                    string wav = Path.Combine(tempDir, "audio.wav");
                    using (var writer = new WaveFileWriter(wav, pcm.WaveFormat))
                    {
                        writer.Write(pcm.PcmBytes, 0, pcm.PcmBytes.Length);
                    }

                    LibavWebmTranscoder.MuxWebmWithOpus(videoOnly, wav, outputPath, quality);
                    if (File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppLog.Warning("Compress.WebmAudio", ex); // genuine audio-decode failure → fall back to video-only
            }
            // OperationCanceledException propagates: a cancelled export must fail, not silently drop the audio.
        }

        File.Copy(videoOnly, outputPath, true);
        return File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
    }
}
