using System;
using System.IO;
using System.Threading;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;

namespace GimmeCapture.Services.Core.Media;

/// <summary>
/// Transcodes an ALREADY-edited/processed video file (trim/crop/rotate/filters/downscale/fps/burn-in already
/// baked in) into GIF or WebM. The GIF/WebM encoders take a whole file and apply no edits, so callers must
/// pre-process via <see cref="LibavClipExporter"/> first. A pinned video's GIF/WebM export and the compress pipeline
/// both go through it.
/// </summary>
internal static class GifWebmVideoExporter
{
    /// <summary>
    /// The part of <see cref="TranscodeToWebm"/>'s progress the VP9 video covers; decoding the audio, encoding it as
    /// Opus and the mux take the rest.
    /// </summary>
    internal const double WebmVideoShare = 0.95;

    /// <summary>
    /// GIF via the two-pass palette encoder. <paramref name="maxWidth"/> 0 keeps the source width;
    /// <paramref name="gifFps"/> decimates to that rate. Returns true when a non-empty file is produced; cancelling
    /// throws <see cref="OperationCanceledException"/>.
    /// </summary>
    public static bool TranscodeToGif(
        string sourcePath, string outputPath, int gifFps, int maxWidth,
        IProgress<double>? progress = null, CancellationToken ct = default, ManualResetEventSlim? pauseGate = null)
    {
        LibavGifTranscoder.TranscodeToGif(
            sourcePath, outputPath, gifFps, maxWidth, progress: progress, cancellationToken: ct, pauseGate: pauseGate);
        return File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
    }

    /// <summary>
    /// WebM: VP9 video at the source fps, then (when <paramref name="keepAudio"/>) decode the audio into a WAV
    /// (<see cref="WriteAudioAsWav"/>) and mux it as Opus — otherwise video-only. Uses <paramref name="tempDir"/> for
    /// the intermediate video-only WebM and WAV. Returns true when a non-empty file is produced; cancelling throws
    /// <see cref="OperationCanceledException"/>. A reset <paramref name="pauseGate"/> holds the video's transcode.
    /// </summary>
    public static bool TranscodeToWebm(
        string sourcePath, string outputPath, string tempDir, VideoQuality quality, bool keepAudio, CancellationToken ct,
        IProgress<double>? progress = null, ManualResetEventSlim? pauseGate = null)
    {
        int webmFps = Math.Clamp(LibavClipExporter.ProbeFps(sourcePath), 1, 60);
        string videoOnly = Path.Combine(tempDir, "video.webm");
        LibavWebmTranscoder.TranscodeToWebm(
            sourcePath, videoOnly, webmFps, quality,
            progress == null ? null : new InlineProgress(value => progress.Report(WebmVideoShare * value)), ct, pauseGate);
        if (!File.Exists(videoOnly) || new FileInfo(videoOnly).Length == 0)
        {
            return false;
        }

        if (keepAudio)
        {
            string wav = Path.Combine(tempDir, "audio.wav");
            try
            {
                if (WriteAudioAsWav(sourcePath, wav, ct))
                {
                    ct.ThrowIfCancellationRequested();
                    LibavWebmTranscoder.MuxWebmWithOpus(videoOnly, wav, outputPath, quality);
                    if (File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
                    {
                        DeleteQuietly(videoOnly);
                        progress?.Report(1.0);
                        return true;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppLog.Warning("GifWebmExport.WebmAudio", ex); // genuine audio-decode failure → fall back to video-only
            }
            // OperationCanceledException propagates: a cancelled export must fail, not silently drop the audio.
            finally
            {
                // A pin's copy leaves its export folder for the clipboard, so the steps' files must not stay beside it.
                DeleteQuietly(wav);
            }
        }

        ct.ThrowIfCancellationRequested();
        File.Move(videoOnly, outputPath, overwrite: true);
        progress?.Report(1.0);
        return File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
    }

    /// <summary>
    /// Decodes <paramref name="sourcePath"/>'s audio into a 16-bit stereo WAV at <paramref name="wavPath"/>, a chunk at a
    /// time, and returns whether any audio was written. The whole track was decoded into one array and then written out:
    /// about 11 MB a minute, held twice over while the array grew and was copied.
    /// </summary>
    internal static bool WriteAudioAsWav(string sourcePath, string wavPath, CancellationToken ct)
    {
        using var wav = new DecodedWavFile(wavPath);
        wav.AppendRange(sourcePath, 0, double.PositiveInfinity, ct);
        return wav.BytesWritten > 0;
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Debug("GifWebmExport.Cleanup", ex); // the start-up sweep removes the folder
        }
    }

    // Reports on the transcoding thread itself, so the shares reach the caller's progress in order.
    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
