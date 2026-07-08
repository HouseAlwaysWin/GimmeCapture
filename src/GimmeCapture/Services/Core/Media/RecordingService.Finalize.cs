using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace GimmeCapture.Services.Core.Media;

public partial class RecordingService
{
    private async Task FinalizeRecordingAsync()
    {
        if (_segments.Count == 0 || string.IsNullOrEmpty(_outputFile)) return;

        // Wait a moment for file handles to be released
        await Task.Delay(100);

        var validSegments = GetValidVideoSegments();

        if (validSegments.Count == 0)
        {
            Debug.WriteLine("No valid segments to finalize!");
            return;
        }

        string mergedMkv = Path.Combine(_tempDir, "merged.mkv");

        try
        {
            mergedMkv = await MergeVideoSegmentsAsync(validSegments, mergedMkv);

            FinalizationProgress = 30;
            var mergedAudio = await BuildFinalAudioAsync();
            await FinalizeByTargetFormatAsync(mergedMkv, mergedAudio, cropFilter: null);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error finalizing recording: {ex.Message}");
            Infrastructure.PlatformErrorDialog.ShowError($"Error saving recording: {ex.Message}", "Save Error");
        }
        finally
        {
            CleanupTempDirectory();
            // Recording finalized — release the large frame/audio/encode buffers back to the OS.
            _ = Infrastructure.ProcessMemoryTrimService.RequestIdleTrimAsync("after-recording");
        }
    }

    // Separate-files mode: build the shared audio once, then finalize each window's track to its own output
    // file (muxing the same audio into every file). Finalize runs sequentially, so we retarget _outputFile
    // per track and reuse the existing single-output finalize path.
    private async Task FinalizeSeparateRecordingsAsync()
    {
        if (_tracks.Count == 0)
        {
            return;
        }

        await Task.Delay(100);

        string? mergedAudio = await BuildFinalAudioAsync();

        try
        {
            int total = _tracks.Count;
            for (int i = 0; i < _tracks.Count; i++)
            {
                var track = _tracks[i];
                var validSegments = track.Segments
                    .AsValueEnumerable()
                    .Where(s => File.Exists(s) && new FileInfo(s).Length > 0)
                    .ToList();

                if (validSegments.Count == 0)
                {
                    Debug.WriteLine($"[Finalize] Track {i} has no valid segments; skipping.");
                    track.OutputFile = string.Empty;
                    continue;
                }

                try
                {
                    string trackMerged = Path.Combine(_tempDir, $"track{i}_merged.mkv");
                    trackMerged = await MergeVideoSegmentsAsync(validSegments, trackMerged);

                    // Retarget the single-output finalize path at this track's file (sequential, so safe).
                    _outputFile = track.OutputFile;
                    // Separate-window pins go through TryHandleSeparateOutputs (the .gif files directly), which
                    // never uses PinSourceFilePath — so don't build per-track sidecars that nothing consumes.
                    await FinalizeByTargetFormatAsync(trackMerged, mergedAudio, cropFilter: null, allowPinSidecar: false);
                    track.OutputFile = _outputFile; // EnsureOutputExtension may have changed the extension
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Finalize] Track {i} finalize failed: {ex.Message}");
                    track.OutputFile = string.Empty;
                }

                FinalizationProgress = 100.0 * (i + 1) / total;
            }
        }
        finally
        {
            CleanupTempDirectory();
        }
    }

    private List<string> GetValidVideoSegments() =>
        _segments.AsValueEnumerable().Where(s => File.Exists(s) && new FileInfo(s).Length > 0).ToList();

    private List<string> GetValidAudioSegments() =>
        _audioSegments.AsValueEnumerable().Where(HasValidAudioData).ToList();

    private List<string> GetValidMicSegments() =>
        _micSegments.AsValueEnumerable().Where(HasValidAudioData).ToList();

    // Merge the system-audio and microphone tracks separately, then mix them into a single
    // WAV when both are present (applying the user's mic gain). Returns the system or mic
    // track alone if only one was captured, or null if neither produced audio.
    private async Task<string?> BuildFinalAudioAsync()
    {
        var system = await MergeAudioSegmentsAsync(GetValidAudioSegments(), "merged_system_audio.wav");
        var mic = await MergeAudioSegmentsAsync(GetValidMicSegments(), "merged_mic_audio.wav");

        if (mic == null)
        {
            return system;
        }

        if (system == null)
        {
            // Mic only: apply gain if the user changed it, otherwise pass through.
            if (Math.Abs(_micVolume - 1.0) < 0.001)
            {
                return mic;
            }

            try
            {
                return await Task.Run(() => ApplyGainToWav(mic, _micVolume));
            }
            catch (Exception ex)
            {
                LogToFile($"[Finalize] Mic gain failed, using raw mic: {ex.Message}");
                return mic;
            }
        }

        try
        {
            string mixed = await Task.Run(() => MixWavFiles(system, mic, _micVolume));
            LogToFile($"[Finalize] Mixed system + mic audio: {mixed}");
            return mixed;
        }
        catch (Exception ex)
        {
            LogToFile($"[Finalize] Audio mix failed, using system audio only: {ex.Message}");
            return system;
        }
    }

    // Mixes two WAV files into one 16-bit WAV at 48 kHz stereo. Both inputs are resampled /
    // channel-adapted to the common format, then summed deterministically (mic gain folded into the
    // mixer) so the result length is exactly the longer source and writer termination does not depend
    // on NAudio's post-EOF silence behavior — see DeterministicMixSampleProvider.
    private string MixWavFiles(string systemPath, string micPath, double micVolume)
    {
        const int rate = 48000;
        const int channels = 2;
        string mixedPath = Path.Combine(_tempDir, "mixed_audio.wav");

        using var systemReader = new AudioFileReader(systemPath);
        using var micReader = new AudioFileReader(micPath);

        ISampleProvider systemProvider = NormalizeSampleProvider(systemReader, rate, channels);
        ISampleProvider micProvider = NormalizeSampleProvider(micReader, rate, channels);

        var mixer = new DeterministicMixSampleProvider(systemProvider, micProvider, (float)micVolume);
        WaveFileWriter.CreateWaveFile16(mixedPath, mixer);
        return mixedPath;
    }

    private string ApplyGainToWav(string wavPath, double volume)
    {
        string outPath = Path.Combine(_tempDir, "mic_gain.wav");
        using var reader = new AudioFileReader(wavPath);
        var amplified = new VolumeSampleProvider(reader) { Volume = (float)volume };
        WaveFileWriter.CreateWaveFile16(outPath, amplified);
        return outPath;
    }

    private static ISampleProvider NormalizeSampleProvider(ISampleProvider source, int targetRate, int targetChannels)
    {
        ISampleProvider provider = source;

        if (provider.WaveFormat.Channels == 1 && targetChannels == 2)
        {
            provider = new MonoToStereoSampleProvider(provider);
        }
        else if (provider.WaveFormat.Channels == 2 && targetChannels == 1)
        {
            provider = new StereoToMonoSampleProvider(provider);
        }

        if (provider.WaveFormat.SampleRate != targetRate)
        {
            provider = new WdlResamplingSampleProvider(provider, targetRate);
        }

        return provider;
    }

    private async Task<string> MergeVideoSegmentsAsync(IReadOnlyList<string> validSegments, string mergedMkvPath)
    {
        if (validSegments.Count == 1)
        {
            return validSegments[0];
        }

        // Concatenate pause/resume segments by copying packets (no re-encode). Falling
        // back to the first segment would silently drop everything recorded after the
        // first pause, so failures are logged and degrade gracefully.
        try
        {
            var stats = await Task.Run(() =>
                LibavMuxer.ConcatVideoSegments(validSegments, mergedMkvPath, "matroska"));
            LogToFile($"[Finalize] Native concat: {mergedMkvPath}, segments={validSegments.Count}, videoPackets={stats.VideoPackets}");

            if (File.Exists(mergedMkvPath) && new FileInfo(mergedMkvPath).Length > 0)
            {
                return mergedMkvPath;
            }

            LogToFile("[Finalize] Native concat produced empty output; using first valid segment.");
        }
        catch (Exception ex)
        {
            LogToFile($"[Finalize] Native concat failed; using first valid segment: {ex}");
        }

        return validSegments[0];
    }

    private async Task<string?> MergeAudioSegmentsAsync(IReadOnlyList<string> validAudioSegments, string mergedFileName = "merged_audio.wav")
    {
        if (validAudioSegments.Count == 0)
        {
            return null;
        }

        if (validAudioSegments.Count == 1)
        {
            return validAudioSegments[0];
        }

        // Minimal DLL-only path: merge WAV segments with NAudio.
        var mergedPath = Path.Combine(_tempDir, mergedFileName);
        try
        {
            using var firstReader = new WaveFileReader(validAudioSegments[0]);
            var waveFormat = firstReader.WaveFormat;

            using var writer = new WaveFileWriter(mergedPath, waveFormat);
            await AppendWaveFileAsync(firstReader, writer);

            for (int i = 1; i < validAudioSegments.Count; i++)
            {
                using var reader = new WaveFileReader(validAudioSegments[i]);
                if (reader.WaveFormat.Equals(waveFormat))
                {
                    await AppendWaveFileAsync(reader, writer);
                }
                else
                {
                    // A pause/resume boundary can yield a segment at a different sample-rate or channel
                    // count (device format change). Resample it to the first segment's format and append
                    // instead of dropping it, so audio stays continuous across the pause.
                    LogToFile($"[Finalize] Resampling mismatched audio segment '{validAudioSegments[i]}' ({reader.WaveFormat}) -> {waveFormat}");
                    AppendResampledSegment(reader, waveFormat, writer);
                }
            }
            return mergedPath;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Finalize] Audio merge failed: {ex.Message}");
            return validAudioSegments[0];
        }
    }

    private static bool HasValidAudioData(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var reader = new WaveFileReader(path);
            return reader.Length > 0 && reader.TotalTime > TimeSpan.FromMilliseconds(100);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Invalid audio segment '{path}': {ex.Message}");
            return false;
        }
    }

    private static async Task WriteConcatListFileAsync(string listPath, IEnumerable<string> items)
    {
        StringBuilder sb = new();
        foreach (var item in items)
        {
            sb.AppendLine($"file '{item.Replace("\\", "/")}'");
        }

        await File.WriteAllTextAsync(listPath, sb.ToString());
    }

    private async Task FinalizeByTargetFormatAsync(string mergedMkv, string? mergedAudio, string? cropFilter, bool allowPinSidecar = true)
    {
        _ = cropFilter;
        PinSourceFilePath = null; // reset per finalize; only the gif branch (when pinning) sets a sidecar
        if (_targetFormat == "gif")
        {
            EnsureOutputExtension("gif");
            await FinalizeAsGifAsync(mergedMkv, cropFilter: null);
            // GIF can't hold audio; keep an audio-bearing mp4 sidecar for the pin — but only when this recording
            // will actually be pinned (single-output pin), so Save/Copy and separate-window mode don't pay for it.
            if (allowPinSidecar && BuildPinAudioSidecar)
            {
                await BuildGifAudioSidecarAsync(mergedMkv, mergedAudio);
            }
            return;
        }

        if (_targetFormat == "webm")
        {
            EnsureOutputExtension("webm");
            await FinalizeAsWebmAsync(mergedMkv, mergedAudio);
            return;
        }

        var sourceVideo = File.Exists(mergedMkv) ? mergedMkv : _segments[0];

        // Honor the user's requested container. Hardware-encoded streams occasionally fail to mux into
        // MP4/MOV, so for those encoders we fall back to MKV (always reliable) ONLY if the requested
        // format actually fails — we no longer force MKV up front, so "MP4" really produces MP4 when it can.
        string requested = _targetFormat is ("mp4" or "mov") ? _targetFormat : "mkv";
        bool allowMkvFallback = requested != "mkv" && RequiresMatroskaFinalization(_lastSelectedVideoEncoderName);

        if (await TryFinalizeAsContainerAsync(requested, sourceVideo, mergedAudio, throwOnFailure: !allowMkvFallback))
        {
            FinalizationProgress = 100;
            return;
        }

        LogToFile($"[Finalize] '{requested}' finalize failed for encoder '{_lastSelectedVideoEncoderName}'; falling back to MKV.");
        await TryFinalizeAsContainerAsync("mkv", sourceVideo, mergedAudio, throwOnFailure: true);
        FinalizationProgress = 100;
    }

    // Finalizes the merged video (+ optional audio) into one container. Returns true on success. An audio
    // mux failure degrades to a video-only output in the SAME container; only a video remux/move failure
    // marks the container unusable (so the caller can fall back to MKV). Throws instead of returning false
    // when throwOnFailure is set.
    private async Task<bool> TryFinalizeAsContainerAsync(string format, string sourceVideo, string? mergedAudio, bool throwOnFailure)
    {
        EnsureOutputExtension(GetTargetExtension(format));
        try
        {
            var targetAudio = await PrepareAudioForTargetContainerAsync(mergedAudio, format);
            if (!string.IsNullOrWhiteSpace(targetAudio) && File.Exists(targetAudio))
            {
                try
                {
                    var muxedPath = Path.Combine(_tempDir, $"muxed_with_audio.{GetTargetExtension(format)}");
                    var muxStats = LibavMuxer.MuxVideoAndAudio(sourceVideo, targetAudio, muxedPath, GetMuxerFormatName(format));
                    LogToFile($"[Finalize] Native mux ({format}) success: {muxedPath}, bytes={(File.Exists(muxedPath) ? new FileInfo(muxedPath).Length : 0)}, videoPackets={muxStats.VideoPackets}, audioPackets={muxStats.AudioPackets}");
                    await TryMoveWithRetryAsync(muxedPath, _outputFile);
                    return true;
                }
                catch (Exception ex)
                {
                    // Audio mux failed — keep the video by writing a video-only file in the same container.
                    Debug.WriteLine($"[Finalize] Audio mux ({format}) failed, video-only fallback: {ex.Message}");
                    LogToFile($"[Finalize] Audio mux ({format}) failed, video-only fallback: {ex}");
                }
            }

            if (string.Equals(format, "mkv", StringComparison.OrdinalIgnoreCase))
            {
                await TryMoveWithRetryAsync(sourceVideo, _outputFile);
            }
            else
            {
                string remuxedPath = Path.Combine(_tempDir, $"remuxed_video_only.{GetTargetExtension(format)}");
                var remuxStats = LibavMuxer.RemuxVideo(sourceVideo, remuxedPath, GetMuxerFormatName(format));
                LogToFile($"[Finalize] Native video-only remux ({format}) success: {remuxedPath}, bytes={(File.Exists(remuxedPath) ? new FileInfo(remuxedPath).Length : 0)}, videoPackets={remuxStats.VideoPackets}");
                await TryMoveWithRetryAsync(remuxedPath, _outputFile);
            }

            return true;
        }
        catch (Exception ex)
        {
            // Video remux/move itself failed -> this container is not usable for the captured stream.
            LogToFile($"[Finalize] Finalize as '{format}' failed: {ex}");
            Debug.WriteLine($"[Finalize] Finalize as '{format}' failed: {ex.Message}");
            if (throwOnFailure)
            {
                throw;
            }

            return false;
        }
    }

    internal static bool RequiresMatroskaFinalization(string encoderName)
    {
        if (string.IsNullOrWhiteSpace(encoderName))
        {
            return false;
        }

        return encoderName.Contains("_mf", StringComparison.OrdinalIgnoreCase)
            || encoderName.Contains("_nvenc", StringComparison.OrdinalIgnoreCase)
            || encoderName.Contains("_qsv", StringComparison.OrdinalIgnoreCase)
            || encoderName.Contains("_amf", StringComparison.OrdinalIgnoreCase)
            || encoderName.Contains("_vaapi", StringComparison.OrdinalIgnoreCase)
            || encoderName.Contains("_d3d12va", StringComparison.OrdinalIgnoreCase);
    }

    internal static string GetTargetExtension(string format) =>
        format switch
        {
            "mp4" => "mp4",
            "mov" => "mov",
            "webm" => "webm",
            _ => "mkv"
        };

    internal static string GetMuxerFormatName(string format) =>
        format switch
        {
            "mp4" => "mp4",
            "mov" => "mov",
            "webm" => "webm",
            _ => "matroska"
        };

    private async Task<string?> PrepareAudioForTargetContainerAsync(string? mergedAudio, string effectiveFormat)
    {
        _ = effectiveFormat;
        if (string.IsNullOrWhiteSpace(mergedAudio) || !File.Exists(mergedAudio))
        {
            return null;
        }

        // Transcode the captured PCM/WAV to AAC for every muxed container (mp4/mov/mkv). AAC muxes cleanly
        // into Matroska too; copying raw PCM straight into MKV was producing files with no usable audio.
        try
        {
            string aacPath = Path.Combine(_tempDir, "merged_audio.m4a");
            var quality = _settingsService?.Settings.VideoQuality ?? VideoQuality.Medium;
            await Task.Run(() => LibavAacTranscoder.EncodeWavToM4a(mergedAudio, aacPath, quality));
            return aacPath;
        }
        catch (Exception ex)
        {
            LogToFile($"[Finalize] AAC transcode failed; using video-only fallback: {ex.Message}");
            return null;
        }
    }

    private static async Task AppendWaveFileAsync(WaveFileReader reader, WaveFileWriter writer)
    {
        byte[] buffer = new byte[81920];
        int read;
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            await writer.WriteAsync(buffer, 0, read);
        }
    }

    // Resamples / channel-adapts a segment whose format differs from the merged track and appends it as
    // 16-bit PCM matching the writer's format. Captured segments are always 16-bit PCM (see
    // RecordingService.Audio.cs), so targetFormat is 16-bit and SampleToWaveProvider16 output is compatible.
    private static void AppendResampledSegment(WaveFileReader reader, WaveFormat targetFormat, WaveFileWriter writer)
    {
        ISampleProvider normalized = NormalizeSampleProvider(reader.ToSampleProvider(), targetFormat.SampleRate, targetFormat.Channels);
        var pcm16 = new SampleToWaveProvider16(normalized);

        byte[] buffer = new byte[81920];
        int read;
        while ((read = pcm16.Read(buffer, 0, buffer.Length)) > 0)
        {
            writer.Write(buffer, 0, read);
        }
    }

    private async Task FinalizeAsGifAsync(string mergedMkv, string? cropFilter)
    {
        _ = cropFilter;
        var quality = _settingsService?.Settings.VideoQuality ?? VideoQuality.Medium;
        var (ladderFps, maxWidth) = LibavGifTranscoder.QualityLadder(quality);
        int gifFps = Math.Min(ladderFps, _fps);

        // Trade color smoothness for file size via the dither: error-diffusion (sierra2_4a) looks best but
        // its high-frequency noise compresses poorly (largest files); ordered bayer is far more compressible;
        // none is smallest but bands gradients. Tie it to the quality preset.
        string paletteuseArgs = quality switch
        {
            VideoQuality.High => "dither=sierra2_4a",
            VideoQuality.Low => "dither=none",
            _ => "dither=bayer:bayer_scale=3"
        };
        await Task.Run(() => LibavGifTranscoder.TranscodeToGif(mergedMkv, _outputFile, gifFps, maxWidth, paletteuseArgs));

        FinalizationProgress = 100;
    }

    // A GIF can't carry audio, so alongside the saved (silent) .gif keep an audio-bearing mp4 — the merged
    // video muxed with the captured audio — for the pin to use. A pinned GIF recording then still plays sound
    // and non-GIF re-exports keep it. Best-effort: on any failure (or no captured audio) PinSourceFilePath
    // stays null and the pin falls back to the silent .gif. Written to the persistent pin temp dir (NOT the
    // per-session dir that CleanupTempDirectory deletes) so it outlives finalize.
    private async Task BuildGifAudioSidecarAsync(string mergedMkv, string? mergedAudio)
    {
        if (string.IsNullOrWhiteSpace(mergedAudio) || !File.Exists(mergedAudio) || !File.Exists(mergedMkv))
        {
            return;
        }

        string? sidecar = null;
        try
        {
            string? aac = await PrepareAudioForTargetContainerAsync(mergedAudio, "mp4");
            if (string.IsNullOrWhiteSpace(aac) || !File.Exists(aac))
            {
                return;
            }

            string sidecarDir = Path.Combine(Path.GetTempPath(), "GimmeCapture_Pins");
            Directory.CreateDirectory(sidecarDir);
            sidecar = Path.Combine(sidecarDir, $"gifrec_{Guid.NewGuid():N}.mp4");
            await Task.Run(() => LibavMuxer.MuxVideoAndAudio(mergedMkv, aac, sidecar, GetMuxerFormatName("mp4")));
            if (File.Exists(sidecar) && new FileInfo(sidecar).Length > 0)
            {
                PinSourceFilePath = sidecar;
                LogToFile($"[Finalize] GIF audio sidecar: {sidecar} ({new FileInfo(sidecar).Length} bytes)");
            }
        }
        catch (Exception ex)
        {
            LogToFile($"[Finalize] GIF audio sidecar failed: {ex}"); // pin falls back to the silent .gif
            // MuxVideoAndAudio opens the file before it can throw (e.g. zero audio packets); remove the partial.
            if (!string.IsNullOrEmpty(sidecar))
            {
                try { if (File.Exists(sidecar)) File.Delete(sidecar); }
                catch { /* best effort */ }
            }
        }
    }

    private async Task FinalizeAsWebmAsync(string mergedMkv, string? mergedAudio)
    {
        var quality = _settingsService?.Settings.VideoQuality ?? VideoQuality.Medium;
        int webmFps = Math.Clamp(_fps, 1, 60);
        string videoOnlyPath = Path.Combine(_tempDir, "webm_video_only.webm");

        await Task.Run(() =>
        {
            LibavWebmTranscoder.TranscodeToWebm(mergedMkv, videoOnlyPath, webmFps, quality);
        });

        if (!string.IsNullOrWhiteSpace(mergedAudio) && File.Exists(mergedAudio))
        {
            try
            {
                var muxStats = await Task.Run(() =>
                    LibavWebmTranscoder.MuxWebmWithOpus(videoOnlyPath, mergedAudio!, _outputFile, quality));
                LogToFile($"[Finalize] WebM native mux: {_outputFile}, videoPkts={muxStats.VideoPackets}, audioPkts={muxStats.AudioPackets}");
            }
            catch (Exception muxEx)
            {
                LogToFile($"[Finalize] WebM audio mux failed, video-only fallback: {muxEx}");
                await Task.Run(() => LibavWebmTranscoder.TranscodeToWebm(mergedMkv, _outputFile, webmFps, quality));
            }
            finally
            {
                TryDeleteQuiet(videoOnlyPath);
            }
        }
        else
        {
            LogToFile("[Finalize] WebM: no merged audio; writing video-only file.");
            await TryMoveWithRetryAsync(videoOnlyPath, _outputFile);
        }

        FinalizationProgress = 100;
    }

    private static void TryDeleteQuiet(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignore
        }
    }

    private void EnsureOutputExtension(string expectedExtension)
    {
        string currentExt = Path.GetExtension(_outputFile).ToLowerInvariant().TrimStart('.');
        if (currentExt != expectedExtension)
        {
            _outputFile = Path.ChangeExtension(_outputFile, expectedExtension);
        }
    }

    private async Task TryMoveWithRetryAsync(string sourcePath, string destinationPath)
    {
        Exception? lastEx = null;
        for (int i = 0; i < 5; i++)
        {
            if (TryMoveWithFallback(sourcePath, destinationPath, out lastEx))
            {
                return;
            }

            await Task.Delay(500);
        }

        if (lastEx != null) throw lastEx;
    }

    private bool TryMoveWithFallback(string sourcePath, string destinationPath, out Exception? error)
    {
        try
        {
            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }
        }
        catch (Exception ex)
        {
            error = ex;
            return false;
        }

        if (TryMoveFile(sourcePath, destinationPath, out var moveError))
        {
            error = null;
            return true;
        }

        if (moveError is not IOException)
        {
            error = moveError;
            return false;
        }

        try
        {
            File.Copy(sourcePath, destinationPath, true);
            TryDeleteFile(sourcePath);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex;
            return false;
        }
    }

    private static bool TryMoveFile(string sourcePath, string destinationPath, out Exception? error)
    {
        try
        {
            File.Move(sourcePath, destinationPath);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex;
            return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to delete file '{path}': {ex.Message}");
        }
    }

    private void CleanupTempDirectory()
    {
        try
        {
            string baseDataDir = _settingsService?.BaseDataDirectory ?? AppDomain.CurrentDomain.BaseDirectory;
            string expectedParent = Path.Combine(baseDataDir, "Temp");
            RecordingTempDirectory.TryDeleteSessionDirectory(_tempDir, expectedParent);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to cleanup temp directory '{_tempDir}': {ex.Message}");
        }
    }

    private void LogToFile(string message)
    {
        Debug.WriteLine($"[RecordingService] {message}");
    }
}
