using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Models;                              // VideoCodec, VideoEditCrop, VideoQuality, Annotation, RedactionTrack, DenoiseMode, SharpenMode
using GimmeCapture.Services.Core.Infrastructure;        // AppLog, CompressSegmentStore, CompressSegmentState
using GimmeCapture.Services.Core.Media.NativeFFmpeg;    // LibavClipExporter, LibavExportOptions, LibavMuxer, LibavGifTranscoder, LibavVideoFramePlayer
using SkiaSharp;                                        // SKBitmap

namespace GimmeCapture.Services.Core.Media;

// Stateless encode core for the Compress tab, extracted out of MainWindowViewModel so the god-class shrinks.
// Every member references only its parameters + static services + the CompressSettingsSnapshot record — no VM
// instance state — so the single-file and batch paths share one code path here.
internal static class CompressPipeline
{
    /// <summary>
    /// Accurate output-size estimate (bytes) for CRF mode: encodes a few short windows of the source at the
    /// snapshot settings, measures the real bytes, and extrapolates to the full duration. Returns -1 on
    /// failure. Far better than the formula because it sees the actual footage. Runs the encode off-thread.
    /// </summary>
    internal static async Task<long> EstimateBySampleAsync(
        string sourcePath, CompressSettingsSnapshot snap,
        IReadOnlyList<(double Start, double End, double Speed)> keptRuns, VideoEditCrop? crop = null)
    {
        double duration = keptRuns.Sum(r => Math.Max(0, r.End - r.Start));
        if (duration <= 0)
        {
            return -1;
        }

        string tempDir = Path.Combine(Path.GetTempPath(), "GimmeCapture_Estimate_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            string sampleOut = Path.Combine(tempDir, "sample.mp4");
            LibavClipExporter.SourceRange[] ranges = BuildSampleRanges(keptRuns, out double sampleDuration);
            if (sampleDuration <= 0)
            {
                return -1;
            }

            // CRF sample (no target bitrate / two-pass): same knobs that drive the real per-file size.
            var options = snap.ToExportOptions();

            bool ok = await Task.Run(() =>
                LibavClipExporter.TryExport(sourcePath, ranges, sampleOut, VideoQuality.Medium, crop: crop, options: options));
            if (!ok || !File.Exists(sampleOut))
            {
                return -1;
            }

            long sampleBytes = new FileInfo(sampleOut).Length;
            return (long)(sampleBytes * (duration / sampleDuration));
        }
        catch (Exception ex)
        {
            AppLog.Error("Compress.EstimateSample", ex);
            return -1;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("Compress.EstimateCleanup", ex);
            }
        }
    }

    // Short kept sets (<= 8s total) sample whole (exact). Longer ones take three 2s windows at 20/50/80% of the
    // concatenated kept timeline, mapped back to source times within the kept runs.
    private static LibavClipExporter.SourceRange[] BuildSampleRanges(
        IReadOnlyList<(double Start, double End, double Speed)> runs, out double sampleDuration)
    {
        double total = runs.Sum(r => Math.Max(0, r.End - r.Start));
        if (total <= 0)
        {
            sampleDuration = 0;
            return Array.Empty<LibavClipExporter.SourceRange>();
        }

        if (total <= 8)
        {
            sampleDuration = total;
            return runs.Select(r => new LibavClipExporter.SourceRange(r.Start, r.End, r.Speed)).ToArray();
        }

        const double window = 2.0;
        var ranges = new List<LibavClipExporter.SourceRange>();
        double sampled = 0;
        foreach (double frac in new[] { 0.2, 0.5, 0.8 })
        {
            double target = total * frac; // concatenated-timeline position
            double cursor = 0;
            foreach ((double start, double end, double speed) in runs)
            {
                double len = end - start;
                if (target <= cursor + len)
                {
                    double srcStart = start + (target - cursor);
                    double srcEnd = Math.Min(end, srcStart + window);
                    if (srcEnd > srcStart)
                    {
                        ranges.Add(new LibavClipExporter.SourceRange(srcStart, srcEnd, speed));
                        sampled += srcEnd - srcStart;
                    }
                    break;
                }
                cursor += len;
            }
        }

        sampleDuration = sampled;
        return ranges.ToArray();
    }

    internal static async Task<double> ProbeInputDurationAsync(string input)
    {
        try
        {
            using var probe = new LibavVideoFramePlayer();
            return await probe.ProbeDurationSecondsAsync(input) ?? 0;
        }
        catch (Exception ex)
        {
            AppLog.Error("Compress.ProbeDuration", ex);
            return 0;
        }
    }

    /// <summary>
    /// Encodes one input to outputPath with the snapshot settings (incl. the H.265 single-pass corrective
    /// re-encode). Returns true on success; throws <see cref="OperationCanceledException"/> if cancelled.
    /// Manages its own temp dir and sets no shared UI state, so single + batch runs share one code path.
    /// </summary>
    internal static async Task<bool> EncodeOneFileAsync(
        string input, string outputPath, CompressSettingsSnapshot s, int targetBitrateKbps,
        double durationSeconds, IProgress<double> progress, CancellationToken token, ManualResetEventSlim? pauseGate,
        int rotationDegrees = 0, IProgress<double>? resumeProgress = null,
        IReadOnlyList<(double Start, double End, double Speed)>? keptRuns = null, VideoEditCrop? crop = null,
        Action<SKBitmap, double>? burnInComposite = null)
    {
        // Kept runs to concatenate (whole file when none). TryExport joins multiple ranges into one output.
        var runs = (keptRuns ?? Array.Empty<(double Start, double End, double Speed)>())
            .Where(r => r.End > r.Start + 0.001)
            .ToList();
        if (runs.Count == 0)
        {
            runs.Add((0, durationSeconds > 0 ? durationSeconds : 24 * 60 * 60, 1.0));
        }
        // Output span honors per-run speed; used by the H.265 target-size corrective re-encode below.
        double effectiveDuration = runs.Sum(r => (r.End - r.Start) / (r.Speed > 0 ? r.Speed : 1.0));
        var ranges = runs.Select(r => new LibavClipExporter.SourceRange(r.Start, r.End, r.Speed)).ToArray();
        bool hasSpeed = runs.Any(r => Math.Abs(r.Speed - 1.0) > 0.001);

        // GIF/WebM: the transcoders take a whole file and apply no edits, so render an all-edits-applied
        // intermediate mp4 first, then transcode. Target-size / two-pass / segmented-resume don't apply here.
        string outFmtExt = Path.GetExtension(outputPath).ToLowerInvariant();
        if (outFmtExt == ".gif" || outFmtExt == ".webm")
        {
            return await EncodeGifWebmAsync(
                input, outputPath, outFmtExt, s, ranges, rotationDegrees, crop, burnInComposite, progress, pauseGate, token);
        }

        // CRF + known duration + a single contiguous, full-speed, un-cropped, un-annotated run → resumable
        // segmented path; target-size / 2-pass / unknown-duration / multi-segment / speed / crop / burn-in
        // keep the whole-file concat path.
        if (!s.UseTargetSize && durationSeconds > 0 && runs.Count == 1 && !hasSpeed && crop == null && burnInComposite == null)
        {
            return await EncodeOneFileSegmentedAsync(
                input, outputPath, s, durationSeconds, progress, token, pauseGate, rotationDegrees, resumeProgress,
                runs[0].Start, runs[0].End);
        }

        string outExt = Path.GetExtension(outputPath);
        string tempDir = Path.Combine(Path.GetTempPath(), "GimmeCapture_Compress_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);

            async Task<string?> EncodeAttemptAsync(int bitrateKbps, int attempt)
            {
                string attemptOut = Path.Combine(tempDir, $"attempt{attempt}{outExt}");
                var options = s.ToExportOptions(rotationDegrees, bitrateKbps, s.UseTwoPass);
                if (attempt > 1)
                {
                    progress.Report(0); // corrective pass restarts the bar
                }
                bool encoded = await Task.Run(() =>
                    LibavClipExporter.TryExport(
                        input, ranges, attemptOut, VideoQuality.Medium, crop: crop,
                        cancellationToken: token, options: options, progress: progress, pauseGate: pauseGate,
                        frameCompositeAfterTransform: burnInComposite), token);
                return encoded && File.Exists(attemptOut) && new FileInfo(attemptOut).Length > 0 ? attemptOut : null;
            }

            string? finalTemp = await EncodeAttemptAsync(targetBitrateKbps, 1);

            // H.265 single-pass corrective re-encode if the first attempt overshot the target.
            if (finalTemp != null && s.UseTargetSize && !s.UseTwoPass)
            {
                double targetBytes = (double)s.TargetSizeMB * 1024 * 1024;
                long actualBytes = new FileInfo(finalTemp).Length;
                if (actualBytes > targetBytes)
                {
                    int refined = CompressEncodeMath.RefineTargetVideoBitrateKbps(targetBitrateKbps, actualBytes, targetBytes, effectiveDuration);
                    if (refined > 0 && refined < targetBitrateKbps)
                    {
                        string? secondTemp = await EncodeAttemptAsync(refined, 2);
                        if (secondTemp != null && new FileInfo(secondTemp).Length < actualBytes)
                        {
                            finalTemp = secondTemp;
                        }
                    }
                }
            }

            if (finalTemp == null)
            {
                return false;
            }

            File.Copy(finalTemp, outputPath, true);
            return true;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("Compress.Cleanup", ex);
            }
        }
    }

    // GIF/WebM output for the compress pipeline. The GIF/WebM encoders take a whole file and apply no edits, so
    // render an all-edits-applied intermediate mp4 (trim/crop/rotate/filters/downscale/fps/burn-in) via the clip
    // exporter, then transcode it (shared with the pin via GifWebmVideoExporter). Quality follows the CRF slider;
    // target-size / two-pass / segmented-resume don't apply (the transcoders are quality-driven).
    private static async Task<bool> EncodeGifWebmAsync(
        string input, string outputPath, string outExt, CompressSettingsSnapshot s,
        LibavClipExporter.SourceRange[] ranges, int rotationDegrees, VideoEditCrop? crop,
        Action<SKBitmap, double>? burnInComposite, IProgress<double> progress,
        ManualResetEventSlim? pauseGate, CancellationToken token)
    {
        bool isGif = outExt == ".gif";
        // Loosely map the CRF slider (x265 scale) to the transcoders' quality tier.
        VideoQuality quality = s.Crf <= 20 ? VideoQuality.High : s.Crf >= 30 ? VideoQuality.Low : VideoQuality.Medium;

        string tempDir = Path.Combine(Path.GetTempPath(), "GimmeCapture_Compress_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);

            // 1. Fully-edited intermediate mp4 (near-lossless CRF to limit generational loss before the transcode).
            string intermediate = Path.Combine(tempDir, "intermediate.mp4");
            var interOptions = new LibavExportOptions
            {
                Codec = VideoCodec.H264,
                CrfOverride = 18,
                Preset = s.Preset,
                MaxHeight = s.MaxHeight,
                MaxFps = s.MaxFps,
                RotationDegrees = rotationDegrees,
                Denoise = s.Denoise,
                Sharpen = s.Sharpen,
                Deblock = s.Deblock,
                Grayscale = s.Grayscale,
                DropAudio = isGif || s.DropAudio, // GIF has no audio; WebM keeps it (unless dropped) for the Opus mux
                AudioBitrateKbps = s.AudioBitrateKbps,
                AudioChannels = s.AudioChannels
            };
            bool built = await Task.Run(() => LibavClipExporter.TryExport(
                input, ranges, intermediate, VideoQuality.Medium, crop: crop, cancellationToken: token,
                options: interOptions, progress: progress, pauseGate: pauseGate,
                frameCompositeAfterTransform: burnInComposite), token);
            if (!built || !File.Exists(intermediate) || new FileInfo(intermediate).Length == 0)
            {
                return false;
            }

            token.ThrowIfCancellationRequested();

            // 2. Transcode the intermediate to the final format INTO the temp dir, then copy to the real output
            //    only on success — so a mid-transcode failure never leaves a partial file at the user's location.
            //    MaxHeight/MaxFps are already baked into the intermediate, so let the GIF ladder only kick in when
            //    the user left resolution/fps at Original.
            string finalTemp = Path.Combine(tempDir, "output" + outExt);
            bool ok;
            if (isGif)
            {
                (int ladderFps, int ladderWidth) = LibavGifTranscoder.QualityLadder(quality);
                int gifFps = s.MaxFps > 0 ? s.MaxFps : ladderFps;
                int maxWidth = s.MaxHeight > 0 ? 0 : ladderWidth;
                ok = await Task.Run(() => GifWebmVideoExporter.TranscodeToGif(intermediate, finalTemp, gifFps, maxWidth), token);
            }
            else
            {
                ok = await Task.Run(() => GifWebmVideoExporter.TranscodeToWebm(
                    intermediate, finalTemp, tempDir, quality, keepAudio: !s.DropAudio, ct: token), token);
            }

            if (!ok || !File.Exists(finalTemp) || new FileInfo(finalTemp).Length == 0)
            {
                return false;
            }

            File.Copy(finalTemp, outputPath, true);
            progress.Report(1.0);
            return true;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("Compress.GifWebmCleanup", ex);
            }
        }
    }

    // ~30s chunks: small enough that an interruption loses little, large enough to keep keyframe/file overhead low.
    private const double CompressChunkSeconds = 30.0;

    /// <summary>
    /// CRF-mode encode that resumes across restarts: encodes the source in <see cref="CompressChunkSeconds"/>
    /// video-only chunks, persisting which are done, then concatenates them and muxes audio once. On resume it
    /// reuses chunks already on disk, so 繼續 continues near the interruption instead of restarting from 0%.
    /// The pause gate / cancellation are honored inside each chunk; partial chunks are kept for resume (the
    /// caller cleans them up only on cancel/success).
    /// </summary>
    private static async Task<bool> EncodeOneFileSegmentedAsync(
        string input, string outputPath, CompressSettingsSnapshot s, double durationSeconds,
        IProgress<double> progress, CancellationToken token, ManualResetEventSlim? pauseGate, int rotationDegrees,
        IProgress<double>? resumeProgress = null, double trimStart = 0, double trimEnd = 0)
    {
        // Encode only the trimmed span (whole file when untrimmed); the chunk grid + audio are offset by spanStart.
        double spanStart = trimStart;
        double spanEnd = trimEnd > trimStart ? trimEnd : durationSeconds;
        double spanDuration = spanEnd - spanStart;
        int chunkCount = Math.Max(1, (int)Math.Ceiling(spanDuration / CompressChunkSeconds));
        string settingsKey = BuildSegmentSettingsKey(s, rotationDegrees, spanStart, spanEnd);

        // Load prior resume state; reset it only if the settings / duration no longer match the chunks. The
        // output path is NOT a validity key — it carries a per-run date stamp, so it legitimately differs each
        // session; the chunks are keyed by input + settings and stay reusable regardless of the output name.
        CompressSegmentState? state = CompressSegmentStore.Load(input);
        if (state == null
            || state.SettingsKey != settingsKey
            || Math.Abs(state.TotalDuration - spanDuration) > 0.5
            || state.ChunkCount != chunkCount)
        {
            CompressSegmentStore.Clear(input);
            state = new CompressSegmentState
            {
                InputPath = input,
                OutputPath = outputPath,
                SettingsKey = settingsKey,
                TotalDuration = spanDuration,
                ChunkSeconds = CompressChunkSeconds,
                ChunkCount = chunkCount,
                CompletedChunks = new List<int>()
            };
            CompressSegmentStore.Save(state);
        }

        var completed = new HashSet<int>(state.CompletedChunks);
        resumeProgress?.Report((double)completed.Count / chunkCount); // current safe resume checkpoint

        var chunkOptions = new LibavExportOptions
        {
            Codec = s.Codec,
            TargetVideoBitrateKbps = 0,   // CRF
            CrfOverride = s.Crf,
            Preset = s.Preset,
            MaxHeight = s.MaxHeight,
            MaxFps = s.MaxFps,
            DropAudio = true,             // video-only chunks; audio is built once at finalize
            TwoPass = false,
            RotationDegrees = rotationDegrees,
            Denoise = s.Denoise,
            Sharpen = s.Sharpen,
            Deblock = s.Deblock,
            Grayscale = s.Grayscale
        };

        for (int i = 0; i < chunkCount; i++)
        {
            token.ThrowIfCancellationRequested();
            string chunkPath = CompressSegmentStore.ChunkPath(input, i);
            if (completed.Contains(i) && File.Exists(chunkPath) && new FileInfo(chunkPath).Length > 0)
            {
                progress.Report((double)(i + 1) / chunkCount); // already done — advance the bar
                continue;
            }

            double startSec = spanStart + i * CompressChunkSeconds;
            double endSec = Math.Min(spanStart + (i + 1) * CompressChunkSeconds, spanEnd);
            var ranges = new[] { new LibavClipExporter.SourceRange(startSec, endSec) };
            int index = i;
            var chunkProgress = new Progress<double>(p => progress.Report((index + Math.Clamp(p, 0, 1)) / chunkCount));

            bool ok = await Task.Run(() => LibavClipExporter.TryExport(
                input, ranges, chunkPath, VideoQuality.Medium,
                cancellationToken: token, options: chunkOptions, progress: chunkProgress, pauseGate: pauseGate), token);
            if (!ok || !File.Exists(chunkPath) || new FileInfo(chunkPath).Length == 0)
            {
                return false;
            }

            completed.Add(i);
            state.CompletedChunks = completed.OrderBy(x => x).ToList();
            CompressSegmentStore.Save(state);
            resumeProgress?.Report((double)completed.Count / chunkCount); // advance the safe checkpoint
        }

        // Finalize: concat the video chunks (lossless), build audio once for the whole file, mux to output.
        token.ThrowIfCancellationRequested();
        string finalizeDir = Path.Combine(Path.GetTempPath(), "GimmeCapture_Compress_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(finalizeDir);
        try
        {
            var chunkPaths = Enumerable.Range(0, chunkCount)
                .Select(i => CompressSegmentStore.ChunkPath(input, i)).ToList();
            if (chunkPaths.Any(p => !File.Exists(p) || new FileInfo(p).Length == 0))
            {
                return false;
            }

            string mergedVideo = Path.Combine(finalizeDir, "merged.mkv");
            bool merged = await Task.Run(() =>
            {
                LibavMuxer.ConcatVideoSegments(chunkPaths, mergedVideo, "matroska");
                return File.Exists(mergedVideo) && new FileInfo(mergedVideo).Length > 0;
            }, token);
            if (!merged)
            {
                return false;
            }

            var audioRanges = new[] { new LibavClipExporter.SourceRange(spanStart, spanEnd) };
            var muxOptions = new LibavExportOptions
            {
                Codec = s.Codec,
                DropAudio = s.DropAudio,
                AudioBitrateKbps = s.AudioBitrateKbps,
                AudioChannels = s.AudioChannels
            };
            bool finalized = await Task.Run(() => LibavClipExporter.TryMuxAudioForRanges(
                input, audioRanges, mergedVideo, outputPath, VideoQuality.Medium, muxOptions, token), token);
            if (!finalized)
            {
                return false;
            }

            progress.Report(1.0);
            CompressSegmentStore.Clear(input); // success — drop the chunks + state
            return true;
        }
        finally
        {
            try { Directory.Delete(finalizeDir, true); }
            catch (Exception ex) { AppLog.Error("Compress.Cleanup", ex); }
        }
    }

    // Settings the chunks were encoded with; a change invalidates persisted chunks (different pixels/bitstream).
    private static string BuildSegmentSettingsKey(CompressSettingsSnapshot s, int rotationDegrees, double trimStart, double trimEnd) =>
        string.Join('|', s.Codec, s.Crf, s.Preset, s.MaxHeight, s.MaxFps,
            s.DropAudio, s.AudioBitrateKbps, s.AudioChannels, rotationDegrees,
            s.Denoise, s.Sharpen, s.Deblock, s.Grayscale,
            Math.Round(trimStart, 2), Math.Round(trimEnd, 2));

    /// <summary>
    /// The per-frame burn-in for a queue item's annotations (surface space) + redaction tracks (normalized),
    /// or null when the item has neither. Runs on the exporter's post-transform (cropped+rotated) frame —
    /// exactly what the editor preview showed. Layers are snapshotted for the worker thread (mirrors the Pin
    /// window's ExportAnnotatedInProcessAsync).
    /// </summary>
    internal static Action<SKBitmap, double>? BuildBurnInComposite(
        IReadOnlyList<Annotation>? annotations, double surfaceW, double surfaceH,
        IReadOnlyList<RedactionTrack>? redactionTracks)
    {
        List<Annotation>? anns = annotations is { Count: > 0 } ? annotations.ToList() : null;
        List<RedactionTrack>? tracks = redactionTracks?.Where(t => t.Keyframes.Count > 0).ToList();
        if (tracks is { Count: 0 })
        {
            tracks = null;
        }

        if (anns == null && tracks == null)
        {
            return null;
        }

        return (sk, t) =>
        {
            if (anns != null && surfaceW > 0 && surfaceH > 0)
            {
                Services.Core.Rendering.AnnotationRenderService.Shared.RenderAnnotationsToBitmap(
                    sk, anns, surfaceW, surfaceH, sk.Width, sk.Height);
            }

            if (tracks != null)
            {
                Services.Core.Rendering.RedactionRenderer.Render(sk, tracks, t);
            }
        };
    }
}
