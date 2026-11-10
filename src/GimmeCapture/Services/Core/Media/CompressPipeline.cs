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
    /// Where the run that stands for the whole file ends when its length is not known: past the end of any clip.
    /// </summary>
    internal const double UnknownLengthEndSeconds = 24 * 60 * 60;

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
            runs.Add((0, durationSeconds > 0 ? durationSeconds : UnknownLengthEndSeconds, 1.0));
        }
        // Output span honors per-run speed; used by the H.265 target-size corrective re-encode below.
        double effectiveDuration = runs.Sum(r => (r.End - r.Start) / (r.Speed > 0 ? r.Speed : 1.0));
        var ranges = runs.Select(r => new LibavClipExporter.SourceRange(r.Start, r.End, r.Speed)).ToArray();
        bool hasSpeed = runs.Any(r => Math.Abs(r.Speed - 1.0) > 0.001);

        // GIF/WebM: the transcoders take a whole file and apply no edits, so an edited clip is rendered to an
        // intermediate mp4 first. Target-size / two-pass / segmented-resume don't apply here.
        string outFmtExt = Path.GetExtension(outputPath).ToLowerInvariant();
        if (outFmtExt == ".gif" || outFmtExt == ".webm")
        {
            return await EncodeGifWebmAsync(
                input, outputPath, outFmtExt, s, durationSeconds, ranges, rotationDegrees, crop, burnInComposite, progress,
                pauseGate, token);
        }

        // Audio-only (WAV/MP3/M4A/OGG): extract the ORIGINAL file's audio track whole — editor
        // keep-ranges/speed/crop and every video knob are irrelevant here by design.
        if (AudioOnlyExporter.IsAudioOnlyExtension(outFmtExt))
        {
            return await EncodeAudioOnlyAsync(input, outputPath, s, progress, token);
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

            await CopyToOutputAsync(finalTemp, outputPath);
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

    /// <summary>
    /// Audio-only tail: decode → (temp WAV) → per-extension encode inside a temp dir, publishing to the
    /// real output only on success so a failure never leaves a partial file at the user's location.
    /// </summary>
    private static async Task<bool> EncodeAudioOnlyAsync(
        string input, string outputPath, CompressSettingsSnapshot s,
        IProgress<double> progress, CancellationToken token)
    {
        // Same loose CRF→tier mapping as the GIF/WebM tail (drives the no-explicit-bitrate defaults).
        VideoQuality quality = s.Crf <= 20 ? VideoQuality.High : s.Crf >= 30 ? VideoQuality.Low : VideoQuality.Medium;

        string tempDir = Path.Combine(Path.GetTempPath(), "GimmeCapture_Compress_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            progress.Report(0);

            string staged = Path.Combine(tempDir, "out" + Path.GetExtension(outputPath));
            bool ok = await Task.Run(
                () => AudioOnlyExporter.ExtractAudio(
                    input, staged, tempDir, quality, s.AudioBitrateKbps, s.AudioChannels, token),
                token);
            if (!ok)
            {
                return false;
            }

            await CopyToOutputAsync(staged, outputPath);
            progress.Report(1);
            return File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort temp cleanup */ }
        }
    }

    /// <summary>
    /// The part of a GIF's progress an intermediate covers; the GIF transcode covers the rest. With the Compress tab's
    /// defaults, on screen recordings of 4, 16 and 35 seconds, the intermediate took 18%, 37% and 27% of the time: what a
    /// GIF made from the source saves.
    /// </summary>
    internal const double GifIntermediateShare = 0.3;

    /// <summary>
    /// The same for a WebM, whose VP9 encode is slower: 26%, 14% and 20% on the same recordings.
    /// </summary>
    internal const double WebmIntermediateShare = 0.15;

    /// <summary>
    /// Whether a GIF or WebM can be transcoded from the source itself, with no intermediate: uncropped and unrotated with
    /// nothing drawn on it and no filter. A GIF takes the kept stretches, their speeds, the size and the frame rate itself;
    /// a WebM has to be the whole clip at its own speed keeping its sound stereo (the intermediate's AAC is stereo unless
    /// mono is asked for, and so is the WebM's sound decoded from the source), its caps being
    /// <see cref="CapsLeaveTheSource"/>'s.
    /// </summary>
    internal static bool NeedsNoIntermediate(
        LibavClipExporter.SourceRange[] ranges, double durationSeconds, int rotationDegrees, VideoEditCrop? crop,
        bool burnsIn, CompressSettingsSnapshot s, bool isGif)
    {
        return (isGif || IsWholeClip(ranges, durationSeconds))
            && LibavClipExporter.NormalizeRotation(rotationDegrees) == 0
            && crop == null
            && !burnsIn
            && s.Denoise == DenoiseMode.Off
            && s.Sharpen == SharpenMode.Off
            && !s.Deblock
            && !s.Grayscale
            && (isGif || s.DropAudio || s.AudioChannels != 1);
    }

    /// <summary>How many times its height cap a source can be and a GIF still be made from it directly.</summary>
    internal const int GifSourceScaleLimit = 2;

    private static bool IsWholeClip(LibavClipExporter.SourceRange[] ranges, double durationSeconds)
    {
        double end = durationSeconds > 0 ? durationSeconds : UnknownLengthEndSeconds;
        return ranges.Length == 1
            && ranges[0].StartSeconds <= 0.001
            && ranges[0].EndSeconds >= end - 0.001
            && Math.Abs(ranges[0].Speed - 1.0) <= 0.001;
    }

    /// <summary>
    /// Whether to go from the source given the size cap, and for a WebM the frame-rate cap. A WebM takes neither, so it
    /// needs a source within them. A GIF scales and sets its rate itself, but decodes what it is made from twice: far above
    /// the GIF's size that costs more than a small intermediate saves (a 4K clip made into a 360p GIF took 21 s against
    /// 10), so it goes from the source up to <see cref="GifSourceScaleLimit"/> times the cap. A size or rate not known (0)
    /// counts as capped.
    /// </summary>
    internal static bool CapsLeaveTheSource(CompressSettingsSnapshot s, bool isGif, int sourceHeight, int sourceFps) =>
        (s.MaxHeight <= 0 || (sourceHeight > 0 && sourceHeight <= s.MaxHeight * (isGif ? GifSourceScaleLimit : 1)))
        && (isGif || s.MaxFps <= 0 || (sourceFps > 0 && sourceFps <= s.MaxFps));

    // GIF/WebM output for the compress pipeline, through GifWebmVideoExporter as the pin's. The transcoders take a whole
    // file and apply no edits, so an edited clip is first rendered to an intermediate mp4 with every edit applied
    // (trim/speed/crop/rotate/filters/downscale/fps/burn-in) by the clip exporter; an unedited one is transcoded as it
    // is, which spares an encode of the whole clip and a generation of loss. Quality follows the CRF slider;
    // target-size / two-pass / segmented-resume don't apply (the transcoders are quality-driven).
    private static async Task<bool> EncodeGifWebmAsync(
        string input, string outputPath, string outExt, CompressSettingsSnapshot s, double durationSeconds,
        LibavClipExporter.SourceRange[] ranges, int rotationDegrees, VideoEditCrop? crop,
        Action<SKBitmap, double>? burnInComposite, IProgress<double> progress,
        ManualResetEventSlim? pauseGate, CancellationToken token)
    {
        bool isGif = outExt == ".gif";
        // Loosely map the CRF slider (x265 scale) to the transcoders' quality tier.
        VideoQuality quality = s.Crf <= 20 ? VideoQuality.High : s.Crf >= 30 ? VideoQuality.Low : VideoQuality.Medium;

        bool straight = NeedsNoIntermediate(ranges, durationSeconds, rotationDegrees, crop, burnInComposite != null, s, isGif);
        if (straight && (s.MaxHeight > 0 || (!isGif && s.MaxFps > 0)))
        {
            try
            {
                using var player = new LibavVideoFramePlayer();
                LibavVideoFramePlayer.MediaInfo info = await player.ProbeAsync(input, token);
                straight = CapsLeaveTheSource(s, isGif, info.Height, info.Fps);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppLog.Debug("Compress.GifWebmProbe", ex); // the intermediate's own open reports a file it cannot read
                straight = false;
            }
        }

        string tempDir = Path.Combine(Path.GetTempPath(), "GimmeCapture_Compress_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);

            string transcodeSource = input;
            double intermediateShare = 0;
            if (!straight)
            {
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
                // The intermediate reported the whole bar, so it sat at 100% through the transcode, the slower part.
                intermediateShare = isGif ? GifIntermediateShare : WebmIntermediateShare;
                bool built = await Task.Run(() => LibavClipExporter.TryExport(
                    input, ranges, intermediate, VideoQuality.Medium, crop: crop, cancellationToken: token,
                    options: interOptions, progress: new StageProgress(progress, 0, intermediateShare), pauseGate: pauseGate,
                    frameCompositeAfterTransform: burnInComposite), token);
                if (!built || !File.Exists(intermediate) || new FileInfo(intermediate).Length == 0)
                {
                    return false;
                }

                token.ThrowIfCancellationRequested();
                transcodeSource = intermediate;
            }

            // 2. Transcode to the final format INTO the temp dir, then copy to the real output only on success — so a
            //    mid-transcode failure never leaves a partial file at the user's location. An intermediate already has
            //    MaxHeight/MaxFps baked in, and a GIF from the source takes them itself, so the GIF ladder only kicks
            //    in when the user left resolution/fps at Original.
            //    The transcode gets the token too: it only kept the transcode from starting, so a cancel during it
            //    ran it to the end and the file came out as done. It waits while paused, as the intermediate does.
            string finalTemp = Path.Combine(tempDir, "output" + outExt);
            var transcodeProgress = new StageProgress(progress, intermediateShare, 1 - intermediateShare);
            bool ok;
            if (isGif)
            {
                (int ladderFps, int ladderWidth) = LibavGifTranscoder.QualityLadder(quality);
                int gifFps = s.MaxFps > 0 ? s.MaxFps : ladderFps;
                int maxWidth = s.MaxHeight > 0 ? 0 : ladderWidth;
                ok = await Task.Run(() => GifWebmVideoExporter.TranscodeToGif(
                    transcodeSource, finalTemp, gifFps, maxWidth, quality, transcodeProgress, token, pauseGate,
                    straight && !IsWholeClip(ranges, durationSeconds) ? ranges : null, straight ? s.MaxHeight : 0), token);
            }
            else
            {
                ok = await Task.Run(() => GifWebmVideoExporter.TranscodeToWebm(
                    transcodeSource, finalTemp, tempDir, quality, keepAudio: !s.DropAudio, ct: token, transcodeProgress,
                    pauseGate), token);
            }

            if (!ok || !File.Exists(finalTemp) || new FileInfo(finalTemp).Length == 0)
            {
                return false;
            }

            token.ThrowIfCancellationRequested();
            await CopyToOutputAsync(finalTemp, outputPath);
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
            DropAudio = true,             // video-only chunks; the sound is built once, alongside them
            TwoPass = false,
            RotationDegrees = rotationDegrees,
            Denoise = s.Denoise,
            Sharpen = s.Sharpen,
            Deblock = s.Deblock,
            Grayscale = s.Grayscale
        };

        // The sound is built alongside the chunks, in a folder of its own: built after them, it kept the bar at 100%
        // through the decode and the AAC encode, about 0.9 s a minute of sound. It waits while paused, as they do.
        string soundDir = Path.Combine(Path.GetTempPath(), "GimmeCapture_Compress_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(soundDir);
        var soundOptions = new LibavExportOptions
        {
            Codec = s.Codec,
            DropAudio = s.DropAudio,
            AudioBitrateKbps = s.AudioBitrateKbps,
            AudioChannels = s.AudioChannels
        };
        using var soundCancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<string?> sound = LibavClipExporter.StartBuildingSound(
            input, [new LibavClipExporter.SourceRange(spanStart, spanEnd)], soundDir, VideoQuality.Medium, soundOptions,
            pauseGate, soundCancel.Token);
        try
        {
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

            // Finalize: the video chunks (lossless) and the sound joined straight into the output, in one pass. They
            // were first joined into a merged.mkv of their own, the whole video written once more and read back.
            token.ThrowIfCancellationRequested();
            var chunkPaths = Enumerable.Range(0, chunkCount)
                .Select(i => CompressSegmentStore.ChunkPath(input, i)).ToList();
            if (chunkPaths.Any(p => !File.Exists(p) || new FileInfo(p).Length == 0))
            {
                return false;
            }

            string? soundPath = await sound;
            bool finalized = await Task.Run(
                () => LibavClipExporter.TryJoinWithSound(chunkPaths, soundPath, outputPath), token);
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
            // A chunk that failed, or a cancel, leaves the sound building: it lets go of its files before they go.
            soundCancel.Cancel();
            try
            {
                await sound;
            }
            catch (OperationCanceledException)
            {
                // stopped with the compress
            }

            try { Directory.Delete(soundDir, true); }
            catch (Exception ex) { AppLog.Error("Compress.Cleanup", ex); }
        }
    }

    // Copies a finished encode from its temp folder to the user's output, on the pool: the batch runner resumes on the
    // UI thread, and an output can run to gigabytes. A copy rather than a move, so the output gets the permissions of
    // the folder it lands in; the caller deletes the temp folder either way.
    private static Task CopyToOutputAsync(string stagedPath, string outputPath) =>
        Task.Run(() => File.Copy(stagedPath, outputPath, true));

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

    // A stage's 0..1 as [start, start + share] of the whole, reported on the stage's own thread so that the stages
    // reach the bar in order.
    private sealed class StageProgress(IProgress<double> whole, double start, double share) : IProgress<double>
    {
        public void Report(double value) => whole.Report(Math.Min(1, start + (share * Math.Clamp(value, 0, 1))));
    }
}
