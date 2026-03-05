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
using System.Linq;
using System.Reactive.Linq;
using System;
using System.IO;
using SkiaSharp; 
using CliWrap;
using CliWrap.Buffered;
using GimmeCapture.Services.Core.Infrastructure;

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
                bool hasAnnotations = Annotations.Any();
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
                bool hasAnnotations = Annotations.Any();
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
            
            // 2. Render Overlay PNG (using original resolution as base)
            using (var surface = SKSurface.Create(new SKImageInfo((int)OriginalWidth, (int)OriginalHeight, SKColorType.Bgra8888, SKAlphaType.Premul)))
            {
                var canvas = surface.Canvas;
                canvas.Clear(SKColors.Transparent);
                
                DrawAnnotationsOnCanvas(canvas, (float)OriginalWidth, (float)OriginalHeight);
                
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

            /* 
               Filter Strategy:
               Video (MP4/MKV etc):
               [1:v][0:v]scale2ref[ovrl][refv];[refv][ovrl]overlay=0:0:shortest=1[outv]
               -map "[outv]" -map 0:a? -c:v libx264 -c:a copy

               GIF:
               [1:v][0:v]scale2ref[ovrl][refv];[refv][ovrl]overlay=0:0:shortest=1,split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse[outv]
               -map "[outv]" (No audio, no libx264)
            */
            bool isOutputGif = ext.Equals(".gif", StringComparison.OrdinalIgnoreCase);
            
            string filter = isOutputGif 
                ? "[1:v][0:v]scale2ref[ovrl][refv];[refv][ovrl]overlay=0:0:shortest=1,split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse[outv]"
                : "[1:v][0:v]scale2ref[ovrl][refv];[refv][ovrl]overlay=0:0:shortest=1[outv]";
            
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

    private void DrawAnnotationsOnCanvas(SKCanvas canvas, float targetW, float targetH)
    {
        // IMPORTANT: Annotations are recorded relative to the DisplaySize.
        // We must map them to the Target surface (Original video size during export).
        var refW = DisplayWidth;
        var refH = DisplayHeight;
        
        if (refW <= 0 || refH <= 0) return;

        float scaleX = targetW / (float)refW; 
        float scaleY = targetH / (float)refH;

        var annotationsArray = Annotations.ToArray(); // Convert to array to get count for logging
        System.Diagnostics.Debug.WriteLine($"[DrawAnnotations] Drawing {annotationsArray.Length} annotations. Target size: {targetW}x{targetH}, Ref size: {refW}x{refH}");

        try
        {
            foreach (var ann in annotationsArray)
            {
                // Create a fresh paint for each annotation to avoid state leakage (e.g. Fill vs Stroke)
                using var paint = new SkiaSharp.SKPaint
                {
                    Color = new SkiaSharp.SKColor(ann.Color.R, ann.Color.G, ann.Color.B, ann.Color.A),
                    StrokeWidth = (float)(ann.Thickness * scaleX),
                    IsAntialias = true,
                    Style = SkiaSharp.SKPaintStyle.Stroke,
                    StrokeCap = SkiaSharp.SKStrokeCap.Round,
                    StrokeJoin = SkiaSharp.SKStrokeJoin.Round
                };

                switch (ann.Type)
                {
                    case AnnotationType.Rectangle:
                    case AnnotationType.Ellipse:
                        var rect = new SkiaSharp.SKRect(
                            (float)(Math.Min(ann.StartPoint.X, ann.EndPoint.X) * scaleX),
                            (float)(Math.Min(ann.StartPoint.Y, ann.EndPoint.Y) * scaleY),
                            (float)(Math.Max(ann.StartPoint.X, ann.EndPoint.X) * scaleX),
                            (float)(Math.Max(ann.StartPoint.Y, ann.EndPoint.Y) * scaleY));
                        if (ann.Type == AnnotationType.Rectangle) canvas.DrawRect(rect, paint);
                        else canvas.DrawOval(rect, paint);
                        System.Diagnostics.Debug.WriteLine($"[DrawAnnotations] Drew {ann.Type} at {rect}");
                        break;
                    case AnnotationType.Line:
                        canvas.DrawLine((float)(ann.StartPoint.X * scaleX), (float)(ann.StartPoint.Y * scaleY), (float)(ann.EndPoint.X * scaleX), (float)(ann.EndPoint.Y * scaleY), paint);
                        System.Diagnostics.Debug.WriteLine($"[DrawAnnotations] Drew {ann.Type} from {ann.StartPoint} to {ann.EndPoint}");
                        break;
                    case AnnotationType.Arrow:
                        float x1 = (float)(ann.StartPoint.X * scaleX), y1 = (float)(ann.StartPoint.Y * scaleY);
                        float x2 = (float)(ann.EndPoint.X * scaleX), y2 = (float)(ann.EndPoint.Y * scaleY);
                        canvas.DrawLine(x1, y1, x2, y2, paint);
                        var dx = x2 - x1;
                        var dy = y2 - y1;
                        var len = Math.Sqrt((dx * dx) + (dy * dy));
                        if (len > 0.001)
                        {
                            var ux = dx / len;
                            var uy = dy / len;
                            var px = -uy;
                            var py = ux;

                            var headLength = Math.Clamp((8.0 * scaleX) + (ann.Thickness * scaleX * 1.4), 8.0 * scaleX, 18.0 * scaleX);
                            // Softer inner notch angle (less steep).
                            var halfWidth = headLength * 0.36;
                            var notchDepth = headLength * 0.38;

                            var leftX = x2 - (ux * headLength) + (px * halfWidth);
                            var leftY = y2 - (uy * headLength) + (py * halfWidth);
                            var rightX = x2 - (ux * headLength) - (px * halfWidth);
                            var rightY = y2 - (uy * headLength) - (py * halfWidth);
                            var notchX = x2 - (ux * notchDepth);
                            var notchY = y2 - (uy * notchDepth);

                            var path = new SkiaSharp.SKPath();
                            path.MoveTo(x2, y2);
                            path.LineTo((float)leftX, (float)leftY);
                            path.LineTo((float)notchX, (float)notchY);
                            path.LineTo((float)rightX, (float)rightY);
                            path.Close();
                            paint.Style = SkiaSharp.SKPaintStyle.Fill;
                            canvas.DrawPath(path, paint);
                        }
                        System.Diagnostics.Debug.WriteLine($"[DrawAnnotations] Drew {ann.Type} from {ann.StartPoint} to {ann.EndPoint}");
                        break;
                    case AnnotationType.Pen:
                        if (ann.Points.Any())
                        {
                            var pts = ann.Points.Select(p => new SkiaSharp.SKPoint((float)(p.X * scaleX), (float)(p.Y * scaleY))).ToArray();
                            canvas.DrawPoints(SkiaSharp.SKPointMode.Polygon, pts, paint);
                            System.Diagnostics.Debug.WriteLine($"[DrawAnnotations] Drew {ann.Type} with {pts.Length} points.");
                        }
                        break;
                    case AnnotationType.Text:
                        {
                            using var font = new SkiaSharp.SKFont(SkiaSharp.SKTypeface.Default, (float)(ann.FontSize * scaleX));
                            using var textPaint = new SkiaSharp.SKPaint { Color = paint.Color, IsAntialias = true };
                            canvas.DrawText(ann.Text, (float)(ann.StartPoint.X * scaleX), (float)(ann.StartPoint.Y * scaleY + ann.FontSize * scaleY), SkiaSharp.SKTextAlign.Left, font, textPaint);
                            System.Diagnostics.Debug.WriteLine($"[DrawAnnotations] Drew {ann.Type}: '{ann.Text}' at {ann.StartPoint}");
                        }
                        break;
                }
            }
            canvas.Flush();
            System.Diagnostics.Debug.WriteLine($"[DrawAnnotations] Finished rendering {annotationsArray.Length} annotations to canvas.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DrawAnnotations] Error: {ex}");
        }
    }

    private async Task<Bitmap?> GetFlattenedBitmapAsync()
    {
        if (VideoBitmap == null) return null;
        
        return await Task.Run(async () => 
        {
            try 
            {
                using var locked = VideoBitmap.Lock();
                var info = new SKImageInfo(VideoBitmap.PixelSize.Width, VideoBitmap.PixelSize.Height, SKColorType.Bgra8888);
                using var skBitmap = new SKBitmap(info);
                
                unsafe 
                {
                    long len = (long)info.BytesSize;
                    Buffer.MemoryCopy((void*)locked.Address, (void*)skBitmap.GetPixels(), len, len);
                }
                
                using var surface = SKSurface.Create(info);
                using var canvas = surface.Canvas;
                
                canvas.DrawBitmap(skBitmap, 0, 0);
                
                DrawAnnotationsOnCanvas(canvas, (float)VideoBitmap.PixelSize.Width, (float)VideoBitmap.PixelSize.Height);
                
                using var image = surface.Snapshot();
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                using var resultMs = new MemoryStream();
                data.SaveTo(resultMs);
                resultMs.Position = 0;
                
                return new Bitmap(resultMs);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error flattening video frame: {ex}");
                return null;
            }
        });
    }
}
