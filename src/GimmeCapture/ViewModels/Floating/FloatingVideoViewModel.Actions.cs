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
        
        // Placeholders for now
        CropCommand = ReactiveCommand.Create(() => { });
        PinSelectionCommand = ReactiveCommand.Create(() => { });

        CopyCommand = ReactiveCommand.CreateFromTask(CopyAsync);
        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync);
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
                bool needsTrim = IsTrimmingMode && (TrimStartSeconds > 0 || TrimEndSeconds < _totalDuration.TotalSeconds);

                if (hasAnnotations || needsTrim)
                {
                    var burntPath = await ExportBurntInVideoAsync();
                    if (!string.IsNullOrEmpty(burntPath) && System.IO.File.Exists(burntPath))
                    {
                        await _clipboardService.CopyFileAndImageAsync(burntPath, await GetFlattenedBitmapAsync() ?? VideoBitmap!);
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
                bool needsTrim = IsTrimmingMode && (TrimStartSeconds > 0 || TrimEndSeconds < _totalDuration.TotalSeconds);

                if (hasAnnotations || needsConversion || needsTrim)
                {
                    var processedPath = await ExportBurntInVideoAsync(targetExt);
                    if (!string.IsNullOrEmpty(processedPath) && System.IO.File.Exists(processedPath))
                    {
                        System.IO.File.Copy(processedPath, targetPath, true);
                        FileLocationService.RevealInFileExplorer(targetPath);
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
            using (var surface = SKSurface.Create(new SKImageInfo((int)OriginalWidth, (int)OriginalHeight, SKColorType.Bgra8888, SKAlphaType.Premul)))
            {
                var canvas = surface.Canvas;
                canvas.Clear(SKColors.Transparent);
                
                AnnotationRenderHelper.DrawAnnotationsOnCanvas(
                    canvas,
                    overlayAnnotations,
                    DisplayWidth,
                    DisplayHeight,
                    (float)OriginalWidth,
                    (float)OriginalHeight);
                
                using (var image = surface.Snapshot())
                using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
                using (var stream = File.OpenWrite(overlayPath))
                {
                    data.SaveTo(stream);
                }
            }
            
            // 3. Run FFmpeg overlay with robust scaling and audio preservation
            var ffmpegPath = FFmpegPath;
            if (ffmpegPath.Contains("ffplay.exe")) ffmpegPath = ffmpegPath.Replace("ffplay.exe", "ffmpeg.exe");
            
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
            
            // 裁切參數：僅在裁切模式下套用
            bool applyTrim = IsTrimmingMode && (TrimStartSeconds > 0 || TrimEndSeconds < _totalDuration.TotalSeconds);

            var result = await Cli.Wrap(ffmpegPath)
                .WithArguments(args => 
                {
                    args.Add("-y");

                    // 加入裁切起始點
                    if (applyTrim && TrimStartSeconds > 0)
                    {
                        args.Add("-ss").Add(TrimStartSeconds.ToString("F3"));
                    }

                    args.Add("-i").Add(VideoPath);

                    // 加入裁切終點
                    if (applyTrim)
                    {
                        args.Add("-to").Add((TrimEndSeconds - TrimStartSeconds).ToString("F3"));
                    }

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
                System.Diagnostics.Debug.WriteLine($"[Export] FFmpeg failed. Code: {result.ExitCode}");
                System.Diagnostics.Debug.WriteLine($"[Export] Errors: {result.StandardError}");
                // Fallback for non-critical exit codes if file exists
                if (File.Exists(outputPath) && new FileInfo(outputPath).Length > 0) return outputPath;
            }
            
            return null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Export failed: {ex}");
            return null;
        }
        finally
        {
            IsExporting = false;
        }
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
