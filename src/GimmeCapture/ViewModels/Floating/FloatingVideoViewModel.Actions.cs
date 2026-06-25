using Avalonia;
using Avalonia.Media.Imaging;
using ReactiveUI;
using System.Reactive;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia.Media;
using GimmeCapture.Models;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core;
using System.Reactive.Linq;
using System;
using System.IO;
using SkiaSharp; 
using CliWrap;
using CliWrap.Buffered;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Media;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using GimmeCapture.Services.Core.Rendering;
using System.Linq;

namespace GimmeCapture.ViewModels.Floating;

public partial class FloatingVideoViewModel
{
    // Base has CloseAction, SaveAction.
    // Base has SelectionRect, IsSelectionActive.
    // Base has CloseCommand, SaveCommand.

    private void InitializeActionCommands()
    {
        // CloseCommand is in Base.

        var canUseSelection = this.WhenAnyValue(x => x.IsSelectionActive);
        // Crop replaces (closes the source window); PinSelection is additive (keeps it).
        CropCommand = ReactiveCommand.CreateFromTask(CropAsync, canUseSelection);
        PinSelectionCommand = ReactiveCommand.CreateFromTask(PinSelectionAsync, canUseSelection);

        CopyCommand = ReactiveCommand.CreateFromTask(CopyAsync);
        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync);
    }

    private Task CropAsync() => ExportAndPinSelectionAsync(closeSource: true);

    private Task PinSelectionAsync() => ExportAndPinSelectionAsync(closeSource: false);

    /// <summary>
    /// Exports the current selection (cropped, honoring the active trim range) to a new
    /// video file and opens it as a new pinned floating window. Annotations are not burnt
    /// in, mirroring the image crop behavior (their coordinates would not align post-crop).
    /// </summary>
    private async Task ExportAndPinSelectionAsync(bool closeSource)
    {
        if (!IsSelectionActive || IsProcessing) return;
        if (OpenPinnedVideoWindowAction == null) return;
        if (!TryGetSelectionPixelRect(out var crop)) return;

        IsProcessing = true;
        ProcessingText = LocalizationService.Instance["StatusProcessing"] ?? "Processing...";
        try
        {
            string? exported = await ExportCroppedRegionAsync(crop, Path.GetExtension(VideoPath));
            if (string.IsNullOrEmpty(exported) || !File.Exists(exported))
            {
                System.Diagnostics.Debug.WriteLine("[Crop] Export produced no output.");
                return;
            }

            // Move the export to a stable per-pin temp file so the GUID export dir can be reused/cleaned.
            string pinDir = Path.Combine(Path.GetTempPath(), "GimmeCapture_Pins");
            Directory.CreateDirectory(pinDir);
            string pinnedPath = Path.Combine(pinDir, $"crop_{Guid.NewGuid():N}{Path.GetExtension(exported)}");
            File.Copy(exported, pinnedPath, true);

            OpenPinnedVideoWindowAction?.Invoke(
                pinnedPath,
                crop.Width,
                crop.Height,
                crop.Width,
                crop.Height,
                BorderColor,
                BorderThickness,
                HidePinDecoration,
                HidePinBorder);

            if (closeSource)
            {
                CloseAction?.Invoke();
            }
            else
            {
                SelectionRect = new Avalonia.Rect();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Crop] Failed: {ex}");
        }
        finally
        {
            IsProcessing = false;
        }
    }

    /// <summary>
    /// Maps the display-space <see cref="FloatingWindowViewModelBase.SelectionRect"/> to even,
    /// in-bounds pixel coordinates suitable for an ffmpeg <c>crop</c> filter / yuv420p output.
    /// </summary>
    private bool TryGetSelectionPixelRect(out PixelRect crop)
    {
        crop = SelectionToPixelCropRect(SelectionRect, DisplayWidth, DisplayHeight, OriginalWidth, OriginalHeight);
        return crop.Width >= 2 && crop.Height >= 2;
    }

    internal static PixelRect SelectionToPixelCropRect(
        Avalonia.Rect selection,
        double displayWidth,
        double displayHeight,
        double originalWidth,
        double originalHeight)
    {
        int videoW = Math.Max(0, (int)Math.Round(originalWidth));
        int videoH = Math.Max(0, (int)Math.Round(originalHeight));
        if (videoW < 2 || videoH < 2) return new PixelRect(0, 0, 0, 0);

        double scaleX = displayWidth > 0 ? originalWidth / displayWidth : 1.0;
        double scaleY = displayHeight > 0 ? originalHeight / displayHeight : 1.0;

        int x = Math.Clamp((int)Math.Round(selection.X * scaleX), 0, videoW - 2);
        int y = Math.Clamp((int)Math.Round(selection.Y * scaleY), 0, videoH - 2);

        // Even width/height (yuv420p), clamped so x+w / y+h stay in bounds.
        int w = (int)Math.Round(selection.Width * scaleX);
        int h = (int)Math.Round(selection.Height * scaleY);
        w = Math.Min(w, videoW - x);
        h = Math.Min(h, videoH - y);
        w = (w / 2) * 2;
        h = (h / 2) * 2;

        return new PixelRect(x, y, Math.Max(0, w), Math.Max(0, h));
    }

    private async Task<string?> ExportCroppedRegionAsync(PixelRect crop, string? targetExtension)
    {
        if (string.IsNullOrEmpty(VideoPath) || !File.Exists(VideoPath)) return null;

        // Multi-segment cut: route crop through the compiler (concat -> crop, audio re-encoded).
        if (UseMultiSegment)
        {
            return await ExportComposedAsync(targetExtension, new VideoEditCrop(crop.X, crop.Y, crop.Width, crop.Height));
        }

        IsExporting = true;
        ExportProgress = 0;
        try
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "GimmeCapture_Export_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            string ext = targetExtension ?? Path.GetExtension(VideoPath);
            if (!ext.StartsWith(".")) ext = "." + ext;
            string outputPath = Path.Combine(tempDir, "output" + ext);

            var ffmpegPath = FFmpegPath;
            if (ffmpegPath.Contains("ffplay.exe")) ffmpegPath = ffmpegPath.Replace("ffplay.exe", "ffmpeg.exe");
            if (!File.Exists(ffmpegPath))
            {
                AppLog.Error("FloatingVideo.ExportCrop", new InvalidOperationException(
                    "No external ffmpeg.exe configured; crop/annotation export is not yet supported in-process."));
                return null;
            }

            (double trimStart, double trimEnd) = SingleSegmentSourceRange();
            bool applyTrim = SingleSegmentIsTrimmed;
            bool isOutputGif = ext.Equals(".gif", StringComparison.OrdinalIgnoreCase);
            string cropFilter = $"crop={crop.Width}:{crop.Height}:{crop.X}:{crop.Y}";

            var result = await Cli.Wrap(ffmpegPath)
                .WithArguments(args =>
                {
                    args.Add("-y");
                    if (applyTrim && trimStart > 0)
                    {
                        args.Add("-ss").Add(trimStart.ToString("F3"));
                    }
                    args.Add("-i").Add(VideoPath);
                    if (applyTrim)
                    {
                        args.Add("-to").Add((trimEnd - trimStart).ToString("F3"));
                    }
                    args.Add("-vf").Add(cropFilter);
                    if (!isOutputGif)
                    {
                        args.Add("-map").Add("0:v:0")
                            .Add("-map").Add("0:a?")
                            .Add("-c:v").Add("libx264")
                            .Add("-preset").Add("ultrafast")
                            .Add("-pix_fmt").Add("yuv420p")
                            .Add("-crf").Add("23")
                            .Add("-c:a").Add("copy");
                    }
                    args.Add(outputPath);
                })
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();

            if (File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
            {
                return outputPath;
            }

            AppLog.Error("FloatingVideo.ExportCrop", new Exception(
                $"ffmpeg exit {result.ExitCode}: {Truncate(result.StandardError, 600)}"));
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Error("FloatingVideo.ExportCrop", ex);
            return null;
        }
        finally
        {
            IsExporting = false;
        }
    }
    
    private async Task CopyAsync()
    {
        if (IsProcessing) return;
        IsProcessing = true;

        // Use Dispatcher.Post to ensure we run AFTER the ContextMenu has fully closed.
        // This is the "standard" way to avoid PlatformImpl null or focus issues.
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                ProcessingText = LocalizationService.Instance["StatusExportingVideo"] ?? "Exporting Video...";
                bool hasAnnotations = Annotations.AsValueEnumerable().Any();
                bool cutRequested = AnyPieceDropped;

                // Trim/cut copy runs fully in-process (no ffmpeg.exe) for plain video sources.
                string copyExt = Path.GetExtension(VideoPath).ToLowerInvariant();
                if (LibavClipExporter.ContainerForExtension(copyExt) == null) copyExt = ".mp4";
                if (cutRequested && CanExportTrimInProcess(hasAnnotations, copyExt))
                {
                    var trimmed = await ExportTrimmedInProcessAsync(copyExt);
                    if (!string.IsNullOrEmpty(trimmed) && System.IO.File.Exists(trimmed))
                    {
                        // Put the trimmed clip on the clipboard. The thumbnail is optional — never let a
                        // null/failed bitmap stop the file from being copied (a stale prior clipboard entry
                        // would otherwise paste the whole video).
                        var thumb = await GetFlattenedBitmapAsync() ?? VideoBitmap;
                        if (thumb != null)
                        {
                            await _clipboardService.CopyFileAndImageAsync(trimmed, thumb);
                        }
                        else
                        {
                            await _clipboardService.CopyFileAsync(trimmed);
                        }
                        AppLog.Information($"FloatingVideo.Copy trimmed clip -> {Path.GetFileName(trimmed)} ({new FileInfo(trimmed).Length} bytes)");

                        // Also persist the copied clip into History (like image copy) so it shows up there.
                        if (AddClipToHistoryAsync != null)
                        {
                            await AddClipToHistoryAsync(trimmed, (int)OriginalWidth, (int)OriginalHeight);
                        }
                        return;
                    }

                    ProcessingText = LocalizationService.Instance["StatusExportFailed"] ?? "Export failed";
                    await Task.Delay(2000);
                    return;
                }

                if (hasAnnotations || cutRequested)
                {
                    var burntPath = await ExportBurntInVideoAsync();
                    if (!string.IsNullOrEmpty(burntPath) && System.IO.File.Exists(burntPath))
                    {
                        await _clipboardService.CopyFileAndImageAsync(burntPath, await GetFlattenedBitmapAsync() ?? VideoBitmap!);
                        return;
                    }

                    // Don't fall back to copying the whole original when an edit was requested — that would
                    // silently put the untrimmed clip on the clipboard. Surface the failure instead.
                    if (cutRequested || hasAnnotations)
                    {
                        ProcessingText = LocalizationService.Instance["StatusExportFailed"] ?? "Export failed";
                        await Task.Delay(2000);
                        return;
                    }
                }

                if (!string.IsNullOrEmpty(VideoPath) && System.IO.File.Exists(VideoPath))
                {
                    await _clipboardService.CopyFileAsync(VideoPath);
                }
                else
                {
                    var bitmapToCopy = await GetFlattenedBitmapAsync();
                    if (bitmapToCopy != null)
                    {
                        await _clipboardService.CopyImageAsync(bitmapToCopy);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error copying video: {ex}");
            }
            finally
            {
                IsProcessing = false;
            }
        });
        
        await Task.CompletedTask;
    }

    // Trim/cut export is supported in-process when there are no annotations to burn in and the target is
    // a plain video container (mp4/mkv/mov). GIF/WebM and annotation/crop burn-in are not migrated yet.
    private bool CanExportTrimInProcess(bool hasAnnotations, string targetExt)
        => !hasAnnotations && LibavClipExporter.ContainerForExtension(targetExt) != null;

    /// <summary>
    /// Frame-accurate trim/concat of the kept runs into a temp file using the in-process libav exporter
    /// (no ffmpeg.exe). Returns the temp output path, or null on failure (logged).
    /// </summary>
    private async Task<string?> ExportTrimmedInProcessAsync(string targetExtension)
    {
        var runs = KeptRuns();
        if (runs.Length == 0) return null;

        var ranges = runs
            .Select(r => new LibavClipExporter.SourceRange(r.SourceStart, r.SourceEnd))
            .ToList();

        string ext = targetExtension.StartsWith('.') ? targetExtension : "." + targetExtension;
        string tempDir = Path.Combine(Path.GetTempPath(), "GimmeCapture_Export_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string outputPath = Path.Combine(tempDir, "output" + ext);
        VideoQuality quality = _appSettingsService?.Settings.VideoQuality ?? VideoQuality.Medium;

        IsExporting = true;
        try
        {
            bool ok = await Task.Run(() => LibavClipExporter.TryExport(VideoPath, ranges, outputPath, quality));
            if (ok)
            {
                return outputPath;
            }

            AppLog.Error("FloatingVideo.ExportTrim", new Exception("In-process clip export produced no output file."));
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Error("FloatingVideo.ExportTrim", ex);
            return null;
        }
        finally
        {
            IsExporting = false;
        }
    }

    private static (int Fps, int MaxWidth) GifSettingsForQuality(VideoQuality q) => q switch
    {
        VideoQuality.High => (24, 0),
        VideoQuality.Low => (10, 480),
        _ => (15, 720),
    };

    /// <summary>
    /// In-process GIF/WebM export: trims the kept runs to a temp mp4 (when a cut was made), then runs the
    /// existing GIF/WebM transcoders on it (WebM re-muxes Opus audio when present). Returns a temp output
    /// path, or null on failure. No ffmpeg.exe.
    /// </summary>
    private async Task<string?> ExportGifWebmInProcessAsync(string targetExt)
    {
        bool isGif = targetExt.Equals(".gif", StringComparison.OrdinalIgnoreCase);
        VideoQuality quality = _appSettingsService?.Settings.VideoQuality ?? VideoQuality.Medium;
        string tempDir = Path.Combine(Path.GetTempPath(), "GimmeCapture_Export_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        IsExporting = true;
        try
        {
            // The transcode source is a trimmed mp4 when a cut was made, else the original recording.
            string source;
            if (AnyPieceDropped)
            {
                var trimmed = await ExportTrimmedInProcessAsync(".mp4");
                if (string.IsNullOrEmpty(trimmed) || !File.Exists(trimmed)) return null;
                source = trimmed;
            }
            else
            {
                source = VideoPath;
            }

            string outputPath = Path.Combine(tempDir, "output" + targetExt);

            if (isGif)
            {
                (int gifFps, int maxWidth) = GifSettingsForQuality(quality);
                await Task.Run(() => LibavGifTranscoder.TranscodeToGif(source, outputPath, gifFps, maxWidth));
                return File.Exists(outputPath) && new FileInfo(outputPath).Length > 0 ? outputPath : null;
            }

            // WebM: VP9 video (CFR at the source fps so duration/speed are preserved), then Opus mux.
            int webmFps = Math.Clamp(LibavClipExporter.ProbeFps(source), 1, 60);
            string videoOnly = Path.Combine(tempDir, "video.webm");
            await Task.Run(() => LibavWebmTranscoder.TranscodeToWebm(source, videoOnly, webmFps, quality));
            if (!File.Exists(videoOnly) || new FileInfo(videoOnly).Length == 0) return null;

            if (await EnsureSourceHasAudioAsync())
            {
                try
                {
                    var pcm = LibavPinAudioPcmDecoder.Decode(source, 0);
                    if (pcm.PcmBytes.Length > 0)
                    {
                        string wav = Path.Combine(tempDir, "audio.wav");
                        using (var writer = new NAudio.Wave.WaveFileWriter(wav, pcm.WaveFormat))
                        {
                            writer.Write(pcm.PcmBytes, 0, pcm.PcmBytes.Length);
                        }

                        string opus = Path.Combine(tempDir, "audio.ogg");
                        await Task.Run(() => LibavOpusTranscoder.EncodeWavToOpusOgg(wav, opus, quality));
                        await Task.Run(() => LibavMuxer.MuxVideoAndAudio(videoOnly, opus, outputPath, "webm"));
                        if (File.Exists(outputPath) && new FileInfo(outputPath).Length > 0) return outputPath;
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Warning("FloatingVideo.ExportWebmAudio", ex); // fall back to video-only
                }
            }

            File.Copy(videoOnly, outputPath, true);
            return File.Exists(outputPath) && new FileInfo(outputPath).Length > 0 ? outputPath : null;
        }
        catch (Exception ex)
        {
            AppLog.Error("FloatingVideo.ExportGifWebm", ex);
            return null;
        }
        finally
        {
            IsExporting = false;
        }
    }

    private async Task SaveAsync()
    {
        if (PickSaveFileAction == null || IsProcessing) return;

        // IMPORTANT: Let the UI event finish (menu close) before opening another modal dialog.
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            var targetPath = await PickSaveFileAction.Invoke();
            if (string.IsNullOrEmpty(targetPath)) return;

            IsProcessing = true;
            ProcessingText = LocalizationService.Instance["StatusProcessing"] ?? "Processing...";

            try
            {
                bool hasAnnotations = Annotations.AsValueEnumerable().Any();
                string sourceExt = Path.GetExtension(VideoPath).ToLowerInvariant();
                string targetExt = Path.GetExtension(targetPath).ToLowerInvariant();
                bool needsConversion = sourceExt != targetExt;
                bool cutRequested = AnyPieceDropped;

                // Trim/cut export runs fully in-process (no ffmpeg.exe) for plain video targets.
                if (cutRequested && CanExportTrimInProcess(hasAnnotations, targetExt))
                {
                    var trimmed = await ExportTrimmedInProcessAsync(targetExt);
                    if (!string.IsNullOrEmpty(trimmed) && System.IO.File.Exists(trimmed))
                    {
                        System.IO.File.Copy(trimmed, targetPath, true);
                        FileLocationService.RevealInFileExplorer(targetPath);
                        return;
                    }

                    ProcessingText = LocalizationService.Instance["StatusExportFailed"] ?? "Export failed";
                    await Task.Delay(2000);
                    return;
                }

                // GIF/WebM export also runs in-process (no ffmpeg.exe): trim to mp4 if a cut was made,
                // then transcode with the existing GIF/WebM encoders. Annotations still excluded here.
                if (!hasAnnotations && (targetExt == ".gif" || targetExt == ".webm"))
                {
                    var produced = await ExportGifWebmInProcessAsync(targetExt);
                    if (!string.IsNullOrEmpty(produced) && System.IO.File.Exists(produced))
                    {
                        System.IO.File.Copy(produced, targetPath, true);
                        FileLocationService.RevealInFileExplorer(targetPath);
                        return;
                    }

                    ProcessingText = LocalizationService.Instance["StatusExportFailed"] ?? "Export failed";
                    await Task.Delay(2000);
                    return;
                }

                if (hasAnnotations || needsConversion || cutRequested)
                {
                    var processedPath = await ExportBurntInVideoAsync(targetExt);
                    if (!string.IsNullOrEmpty(processedPath) && System.IO.File.Exists(processedPath))
                    {
                        System.IO.File.Copy(processedPath, targetPath, true);
                        FileLocationService.RevealInFileExplorer(targetPath);
                        return;
                    }

                    // A cut/annotation export was requested but failed. Copying the original whole clip
                    // here would silently produce the WRONG (untrimmed) video, which looks exactly like
                    // "the edit did nothing". Surface the failure instead of masking it.
                    if (cutRequested || hasAnnotations)
                    {
                        ProcessingText = LocalizationService.Instance["StatusExportFailed"] ?? "Export failed";
                        await Task.Delay(2000);
                        return;
                    }
                }

                if (System.IO.File.Exists(VideoPath))
                {
                    System.IO.File.Copy(VideoPath, targetPath, true);
                    FileLocationService.RevealInFileExplorer(targetPath);
                }
                else
                {
                    var bitmap = await GetFlattenedBitmapAsync();
                    if (bitmap != null)
                    {
                        using var stream = new System.IO.FileStream(targetPath, System.IO.FileMode.Create);
                        bitmap.Save(stream);
                        FileLocationService.RevealInFileExplorer(targetPath);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error saving video: {ex}");
                ProcessingText = "Save Failed: " + ex.Message;
                IsProcessing = true;
                await Task.Delay(2000);
            }
            finally
            {
                IsProcessing = false;
            }
        });
        
        await Task.CompletedTask;
    }

    private async Task<string?> ExportBurntInVideoAsync(string? targetExtension = null)
    {
        if (string.IsNullOrEmpty(VideoPath) || !System.IO.File.Exists(VideoPath)) return null;

        // Multi-segment cut: route annotations/trim through the compiler (concat, audio re-encoded).
        if (UseMultiSegment)
        {
            return await ExportComposedAsync(targetExtension, crop: null);
        }

        // IsProcessing controlled by caller (CopyAsync/SaveAsync) to prevent flickering
        IsExporting = true;
        ExportProgress = 0;
        
        try 
        {
            // 1. Prepare Paths
            string tempDir = Path.Combine(Path.GetTempPath(), "GimmeCapture_Export_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            
            string overlayPath = Path.Combine(tempDir, "overlay.png");
            string ext = targetExtension ?? Path.GetExtension(VideoPath);
            if (!ext.StartsWith(".")) ext = "." + ext;
            string outputPath = Path.Combine(tempDir, "output" + ext);
            var overlayAnnotations = Annotations
                .Where(a => a.Type is not AnnotationType.Mosaic and not AnnotationType.Blur)
                .ToArray();
            
            // 2. Render vector/text overlay PNG only. Redaction effects are applied per-frame by FFmpeg.
            using (var overlayBitmap = new SKBitmap((int)OriginalWidth, (int)OriginalHeight, SKColorType.Bgra8888, SKAlphaType.Premul))
            {
                overlayBitmap.Erase(SKColors.Transparent);
                AnnotationRenderService.Shared.RenderAnnotationsToBitmap(
                    overlayBitmap,
                    overlayAnnotations,
                    DisplayWidth,
                    DisplayHeight,
                    overlayBitmap.Width,
                    overlayBitmap.Height);

                using (var image = SKImage.FromBitmap(overlayBitmap))
                using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
                using (var stream = File.OpenWrite(overlayPath))
                {
                    data.SaveTo(stream);
                }
            }
            
            // 3. Run FFmpeg overlay with robust scaling and audio preservation
            var ffmpegPath = FFmpegPath;
            if (ffmpegPath.Contains("ffplay.exe")) ffmpegPath = ffmpegPath.Replace("ffplay.exe", "ffmpeg.exe");
            if (!File.Exists(ffmpegPath))
            {
                AppLog.Error("FloatingVideo.Export", new InvalidOperationException(
                    "No external ffmpeg.exe configured; annotation burn-in export is not yet supported in-process."));
                return null;
            }

            // Log for diagnostics
            System.Diagnostics.Debug.WriteLine($"[Export] Start: {VideoPath} -> {outputPath}");

            bool isOutputGif = ext.Equals(".gif", StringComparison.OrdinalIgnoreCase);
            string filter = VideoAnnotationFilterBuilder.BuildFilter(
                Annotations,
                DisplayWidth,
                DisplayHeight,
                (int)OriginalWidth,
                (int)OriginalHeight,
                includeOverlayInput: true,
                isOutputGif: isOutputGif);
            
            // Single-segment trim: fast input-level -ss/-to (the kept segment's source range).
            (double trimStart, double trimEnd) = SingleSegmentSourceRange();
            bool applyTrim = SingleSegmentIsTrimmed;

            var result = await Cli.Wrap(ffmpegPath)
                .WithArguments(args =>
                {
                    args.Add("-y");

                    if (applyTrim && trimStart > 0)
                    {
                        args.Add("-ss").Add(trimStart.ToString("F3"));
                    }

                    args.Add("-i").Add(VideoPath);

                    args.Add("-loop").Add("1")
                        .Add("-i").Add(overlayPath)
                        .Add("-filter_complex").Add(filter)
                        .Add("-map").Add("[outv]");

                    if (!isOutputGif)
                    {
                        args.Add("-map").Add("0:a?")    // Keep audio if present
                            .Add("-c:v").Add("libx264")
                            .Add("-preset").Add("ultrafast")
                            .Add("-pix_fmt").Add("yuv420p")
                            .Add("-crf").Add("23")
                            .Add("-c:a").Add("copy");    // Preserve audio quality
                    }

                    // Output-side duration limit. As an OUTPUT option this caps the kept range to
                    // (trimEnd - trimStart) seconds after the input -ss seek; placing it between inputs
                    // would (wrongly) attach it to the looped overlay PNG instead of the video.
                    if (applyTrim)
                    {
                        args.Add("-t").Add((trimEnd - trimStart).ToString("F3"));
                    }

                    args.Add(outputPath);
                })
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();

            if (result.ExitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
            {
                System.Diagnostics.Debug.WriteLine($"[Export] Success: {outputPath}");
                return outputPath; 
            }
            else
            {
                // Fallback for non-critical exit codes if file exists
                if (File.Exists(outputPath) && new FileInfo(outputPath).Length > 0) return outputPath;
                AppLog.Error("FloatingVideo.Export", new Exception(
                    $"ffmpeg exit {result.ExitCode}: {Truncate(result.StandardError, 600)}"));
            }

            return null;
        }
        catch (Exception ex)
        {
            AppLog.Error("FloatingVideo.Export", ex);
            return null;
        }
        finally
        {
            IsExporting = false;
        }
    }

    /// <summary>
    /// Multi-segment export: concatenates the kept segments via the pure <see cref="VideoEditFilterCompiler"/>,
    /// then (for the annotation path) runs the proven redaction/overlay chain on the joined video via
    /// <see cref="VideoAnnotationFilterBuilder"/>. Crop and annotations never mix, mirroring the
    /// single-segment paths. Audio is re-encoded (aac) because atrim+concat cannot stream-copy.
    /// </summary>
    private async Task<string?> ExportComposedAsync(string? targetExtension, VideoEditCrop? crop)
    {
        if (string.IsNullOrEmpty(VideoPath) || !File.Exists(VideoPath)) return null;

        IsExporting = true;
        ExportProgress = 0;
        try
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "GimmeCapture_Export_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            string ext = targetExtension ?? Path.GetExtension(VideoPath);
            if (!ext.StartsWith(".")) ext = "." + ext;
            string outputPath = Path.Combine(tempDir, "output" + ext);
            bool isOutputGif = ext.Equals(".gif", StringComparison.OrdinalIgnoreCase);

            // Annotations are burnt in only when NOT cropping (crop & annotations never mix, like today).
            bool burnAnnotations = crop is null && Annotations.AsValueEnumerable().Any();
            string? overlayPath = null;
            if (burnAnnotations)
            {
                overlayPath = Path.Combine(tempDir, "overlay.png");
                RenderAnnotationOverlayPng(overlayPath);
            }

            bool hasAudio = await EnsureSourceHasAudioAsync() && !isOutputGif;

            VideoEditProject project = BuildEditProject(crop);
            VideoEditFilterCompiler.CompiledEdit compiled = VideoEditFilterCompiler.Compile(project, hasAudio);

            string filterComplex;
            string videoMap;
            if (burnAnnotations)
            {
                string vIn = compiled.VideoMap.Trim('[', ']');
                string tail = VideoAnnotationFilterBuilder.BuildFilter(
                    Annotations,
                    DisplayWidth,
                    DisplayHeight,
                    (int)OriginalWidth,
                    (int)OriginalHeight,
                    includeOverlayInput: true,
                    isOutputGif: isOutputGif,
                    inputVideoLabel: vIn);
                filterComplex = compiled.FilterComplex + ";" + tail;
                videoMap = "[outv]";
            }
            else if (isOutputGif)
            {
                string vIn = compiled.VideoMap.Trim('[', ']');
                filterComplex = compiled.FilterComplex
                    + $";[{vIn}]split[gsp0][gsp1];[gsp0]palettegen[gpal];[gsp1][gpal]paletteuse[outv]";
                videoMap = "[outv]";
            }
            else
            {
                filterComplex = compiled.FilterComplex;
                videoMap = compiled.VideoMap;
            }

            var ffmpegPath = FFmpegPath;
            if (ffmpegPath.Contains("ffplay.exe")) ffmpegPath = ffmpegPath.Replace("ffplay.exe", "ffmpeg.exe");
            if (!File.Exists(ffmpegPath))
            {
                AppLog.Error("FloatingVideo.ExportComposed", new InvalidOperationException(
                    "No external ffmpeg.exe configured; annotation/multi-segment CLI export is not yet supported in-process."));
                return null;
            }

            var result = await Cli.Wrap(ffmpegPath)
                .WithArguments(args =>
                {
                    args.Add("-y");
                    args.Add("-i").Add(VideoPath);
                    if (burnAnnotations)
                    {
                        args.Add("-loop").Add("1").Add("-i").Add(overlayPath!);
                    }

                    args.Add("-filter_complex").Add(filterComplex);
                    args.Add("-map").Add(videoMap);
                    if (compiled.AudioMap != null && !isOutputGif)
                    {
                        args.Add("-map").Add(compiled.AudioMap);
                    }

                    if (!isOutputGif)
                    {
                        args.Add("-c:v").Add("libx264")
                            .Add("-preset").Add("ultrafast")
                            .Add("-pix_fmt").Add("yuv420p")
                            .Add("-crf").Add("23");
                        if (compiled.AudioMap != null)
                        {
                            args.Add("-c:a").Add("aac");
                        }
                    }

                    args.Add(outputPath);
                })
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();

            if (File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
            {
                return outputPath;
            }

            AppLog.Error("FloatingVideo.ExportComposed", new Exception(
                $"ffmpeg exit {result.ExitCode}: {Truncate(result.StandardError, 600)}"));
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Error("FloatingVideo.ExportComposed", ex);
            return null;
        }
        finally
        {
            IsExporting = false;
        }
    }

    // Trims ffmpeg stderr for the log so a multi-KB dump doesn't bloat the Serilog file.
    private static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        text = text.Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }

    /// <summary>Renders the vector/text annotations (not redaction effects) to a transparent PNG.</summary>
    private void RenderAnnotationOverlayPng(string overlayPath)
    {
        var overlayAnnotations = Annotations
            .Where(a => a.Type is not AnnotationType.Mosaic and not AnnotationType.Blur)
            .ToArray();

        using var overlayBitmap = new SKBitmap((int)OriginalWidth, (int)OriginalHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        overlayBitmap.Erase(SKColors.Transparent);
        AnnotationRenderService.Shared.RenderAnnotationsToBitmap(
            overlayBitmap,
            overlayAnnotations,
            DisplayWidth,
            DisplayHeight,
            overlayBitmap.Width,
            overlayBitmap.Height);

        using var image = SKImage.FromBitmap(overlayBitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.OpenWrite(overlayPath);
        data.SaveTo(stream);
    }



    private async Task<Bitmap?> GetFlattenedBitmapAsync()
    {
        if (VideoBitmap == null) return null;
        
        return await Task.Run(() => 
        {
            try 
            {
                if (!FloatingBitmapConversionHelper.TryCopyToSkBitmap(VideoBitmap, out var skBitmap, out _)
                    || skBitmap == null)
                    return null;

                using (skBitmap)
                {
                    AnnotationRenderService.Shared.RenderAnnotationsToBitmap(
                        skBitmap,
                        Annotations,
                        DisplayWidth,
                        DisplayHeight,
                        skBitmap.Width,
                        skBitmap.Height);

                    if (!FloatingBitmapConversionHelper.TryCreateDetachedBitmapFromSkBitmap(skBitmap, out var detachedBitmap, out _))
                        return null;

                    return detachedBitmap;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error flattening video frame: {ex}");
                return null;
            }
        });
    }
}
