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
        bool hasAnnotations = Annotations.Any();

        if (hasAnnotations)
        {
            // NEW: Export burnt-in video if there are annotations
            var burntPath = await ExportBurntInVideoAsync();
            if (!string.IsNullOrEmpty(burntPath) && System.IO.File.Exists(burntPath))
            {
                await _clipboardService.CopyFileAndImageAsync(burntPath, await GetFlattenedBitmapAsync() ?? VideoBitmap!);
                return;
            }
        }

        // Standard logic for no annotations or export failure
        if (!string.IsNullOrEmpty(VideoPath) && System.IO.File.Exists(VideoPath))
        {
            await _clipboardService.CopyFileAsync(VideoPath);
        }
        else
        {
            // Fallback for missing video file
            var bitmapToCopy = await GetFlattenedBitmapAsync();
            if (bitmapToCopy != null)
            {
                await _clipboardService.CopyImageAsync(bitmapToCopy);
            }
        }
    }

    private async Task SaveAsync()
    {
        if (PickSaveFileAction == null) return;

        var targetPath = await PickSaveFileAction.Invoke();
        if (string.IsNullOrEmpty(targetPath)) return;

        try
        {
            bool hasAnnotations = Annotations.Any();
            if (hasAnnotations)
            {
                var burntPath = await ExportBurntInVideoAsync();
                if (!string.IsNullOrEmpty(burntPath) && System.IO.File.Exists(burntPath))
                {
                    System.IO.File.Copy(burntPath, targetPath, true);
                    return;
                }
            }

            if (System.IO.File.Exists(VideoPath))
            {
                System.IO.File.Copy(VideoPath, targetPath, true);
            }
            else
            {
                // Fallback to saving current frame as PNG if video file is missing
                var bitmap = await GetFlattenedBitmapAsync();
                if (bitmap != null)
                {
                    using var stream = new System.IO.FileStream(targetPath, System.IO.FileMode.Create);
                    bitmap.Save(stream);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error saving video: {ex}");
        }
    }

    private async Task<string?> ExportBurntInVideoAsync()
    {
        if (string.IsNullOrEmpty(VideoPath) || !System.IO.File.Exists(VideoPath)) return null;

        IsProcessing = true;
        ProcessingText = LocalizationService.Instance["StatusExportingVideo"] ?? "Exporting Video...";
        
        try 
        {
            // 1. Prepare Paths
            string tempDir = Path.Combine(Path.GetTempPath(), "GimmeCapture_Export_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            
            string overlayPath = Path.Combine(tempDir, "overlay.png");
            string outputPath = Path.Combine(tempDir, "output" + Path.GetExtension(VideoPath));
            
            // 2. Render Overlay PNG (at video resolution)
            using (var surface = SKSurface.Create(new SKImageInfo(_width, _height, SKColorType.Bgra8888, SKAlphaType.Premul)))
            {
                var canvas = surface.Canvas;
                canvas.Clear(SKColors.Transparent);
                
                await DrawAnnotationsOnCanvas(canvas, (float)_width, (float)_height);
                
                using (var image = surface.Snapshot())
                using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
                using (var stream = File.OpenWrite(overlayPath))
                {
                    data.SaveTo(stream);
                }
            }
            
            // 3. Run FFmpeg overlay
            // ffmpeg -i input.mp4 -i overlay.png -filter_complex "[0:v][1:v]overlay=0:0" -c:v libx264 -preset ultrafast -c:a copy output.mp4
            var ffmpegPath = FFmpegPath.Replace("ffplay.exe", "ffmpeg.exe");
            string args = $"-y -i \"{VideoPath}\" -i \"{overlayPath}\" -filter_complex \"[0:v][1:v]overlay=0:0\" -c:v libx264 -preset ultrafast -crf 23 -c:a copy \"{outputPath}\"";
            
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            
            var process = System.Diagnostics.Process.Start(psi);
            if (process != null)
            {
                // We could parse progress from stderr if needed, but ultrafast is usually stay-quick
                await process.WaitForExitAsync();
                
                if (process.ExitCode == 0 && File.Exists(outputPath))
                {
                    return outputPath; // Return the path to the newly created video
                }
            }
            
            return null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Export failed: {ex.Message}");
            return null;
        }
        finally
        {
            IsProcessing = false;
        }
    }

    private async Task DrawAnnotationsOnCanvas(SKCanvas canvas, float targetW, float targetH)
    {
        // Use the actual current display dimensions for mapping
        var refW = DisplayWidth > 0 ? DisplayWidth : OriginalWidth;
        var refH = DisplayHeight > 0 ? DisplayHeight : OriginalHeight;
        
        if (refW <= 0 || refH <= 0) return;

        float scaleX = targetW / (float)refW; 
        float scaleY = targetH / (float)refH;

        foreach (var ann in Annotations)
        {
            var paint = new SKPaint
            {
                Color = new SKColor(ann.Color.R, ann.Color.G, ann.Color.B, ann.Color.A),
                StrokeWidth = (float)(ann.Thickness * scaleX),
                IsAntialias = true,
                Style = SKPaintStyle.Stroke
            };
            
            if (ann.Type == AnnotationType.Pen)
            {
                paint.StrokeCap = SKStrokeCap.Round;
                paint.StrokeJoin = SKStrokeJoin.Round;
            }

            switch (ann.Type)
            {
                case AnnotationType.Rectangle:
                case AnnotationType.Ellipse:
                    var rect = new SKRect(
                        (float)(Math.Min(ann.StartPoint.X, ann.EndPoint.X) * scaleX),
                        (float)(Math.Min(ann.StartPoint.Y, ann.EndPoint.Y) * scaleY),
                        (float)(Math.Max(ann.StartPoint.X, ann.EndPoint.X) * scaleX),
                        (float)(Math.Max(ann.StartPoint.Y, ann.EndPoint.Y) * scaleY));
                    
                    if (ann.Type == AnnotationType.Rectangle)
                        canvas.DrawRect(rect, paint);
                    else
                        canvas.DrawOval(rect, paint);
                    break;
                    
                case AnnotationType.Line:
                    canvas.DrawLine(
                        (float)(ann.StartPoint.X * scaleX), (float)(ann.StartPoint.Y * scaleY),
                        (float)(ann.EndPoint.X * scaleX), (float)(ann.EndPoint.Y * scaleY),
                        paint);
                    break;
                    
                case AnnotationType.Arrow:
                    float x1 = (float)(ann.StartPoint.X * scaleX);
                    float y1 = (float)(ann.StartPoint.Y * scaleY);
                    float x2 = (float)(ann.EndPoint.X * scaleX);
                    float y2 = (float)(ann.EndPoint.Y * scaleY);
                    canvas.DrawLine(x1, y1, x2, y2, paint);
                    
                    double angle = Math.Atan2(y2 - y1, x2 - x1);
                    double arrowLen = 15 * scaleX; 
                    double arrowAngle = Math.PI / 6;
                    
                    float ax1 = (float)(x2 - arrowLen * Math.Cos(angle - arrowAngle));
                    float ay1 = (float)(y2 - arrowLen * Math.Sin(angle - arrowAngle));
                    float ax2 = (float)(x2 - arrowLen * Math.Cos(angle + arrowAngle));
                    float ay2 = (float)(y2 - arrowLen * Math.Sin(angle + arrowAngle));
                    
                    var path = new SKPath();
                    path.MoveTo(x2, y2);
                    path.LineTo(ax1, ay1);
                    path.LineTo(ax2, ay2);
                    path.Close();
                    
                    paint.Style = SKPaintStyle.Fill;
                    canvas.DrawPath(path, paint);
                    break;

                 case AnnotationType.Pen:
                     if (ann.Points.Count > 1)
                     {
                         var points = ann.Points.Select(p => new SKPoint((float)(p.X * scaleX), (float)(p.Y * scaleY))).ToArray();
                         if (points.Length > 1)
                         {
                             canvas.DrawPoints(SKPointMode.Polygon, points, paint);
                         }
                     }
                     break;
                     
                 case AnnotationType.Text:
                     var font = new SKFont(SKTypeface.Default, (float)(ann.FontSize * scaleX));
                     var textPaint = new SKPaint
                     {
                         Color = paint.Color,
                         IsAntialias = true,
                     };
                     canvas.DrawText(ann.Text, (float)(ann.StartPoint.X * scaleX), (float)(ann.StartPoint.Y * scaleY + ann.FontSize * scaleY), SKTextAlign.Left, font, textPaint);
                     break;
            }
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
                
                await DrawAnnotationsOnCanvas(canvas, (float)VideoBitmap.PixelSize.Width, (float)VideoBitmap.PixelSize.Height);
                
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
