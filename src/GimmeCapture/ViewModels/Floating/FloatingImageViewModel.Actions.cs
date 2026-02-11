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
using SkiaSharp; // Needed for flattening

namespace GimmeCapture.ViewModels.Floating;

public partial class FloatingImageViewModel
{
    private Avalonia.Rect _selectionRect = new Avalonia.Rect();
    public Avalonia.Rect SelectionRect
    {
        get => _selectionRect;
        set 
        {
            this.RaiseAndSetIfChanged(ref _selectionRect, value);
            this.RaisePropertyChanged(nameof(IsSelectionActive));
        }
    }
    public bool IsSelectionActive => SelectionRect.Width > 0 && SelectionRect.Height > 0;

    public ReactiveCommand<Unit, Unit> CopyCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> CutCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> CropCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> PinSelectionCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> SaveCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> CloseCommand { get; private set; } = null!;

    public System.Action? CloseAction { get; set; }
    public System.Action<Bitmap, Avalonia.Rect, Avalonia.Media.Color, double, bool>? OpenPinWindowAction { get; set; }
    public System.Func<Task>? SaveAction { get; set; }
    public Action<Avalonia.PixelPoint, double, double, double, double>? RequestSetWindowRect { get; set; }

    private void InitializeActionCommands()
    {
        CloseCommand = ReactiveCommand.Create(() => CloseAction?.Invoke());

        SaveCommand = ReactiveCommand.CreateFromTask(async () => 
        {
             if (SaveAction != null)
             {
                 // Temporary swap of Image for flattened version if we have annotations
                 var originalImage = Image;
                 var flattened = Annotations.Any() ? await GetFlattenedBitmapAsync() : null;
                 
                 if (flattened != null)
                 {
                     Image = flattened;
                 }
                 
                 try 
                 {
                    await SaveAction();
                 }
                 finally
                 {
                     if (flattened != null)
                     {
                         Image = originalImage;
                         // flattened.Dispose(); // Image property change might have disposed it or UI holds ref? 
                         // To be safe, we let GC handle it or explicit dispose if we know no one else strictly needs it.
                     }
                 }
             }
        });

        CopyCommand = ReactiveCommand.CreateFromTask(CopyAsync);
        CutCommand = ReactiveCommand.CreateFromTask(CutAsync, this.WhenAnyValue(x => x.IsSelectionActive));
        CropCommand = ReactiveCommand.CreateFromTask(CropAsync, this.WhenAnyValue(x => x.IsSelectionActive));
        PinSelectionCommand = ReactiveCommand.CreateFromTask(PinSelectionAsync, this.WhenAnyValue(x => x.IsSelectionActive));
    }

    private async Task CopyAsync()
    {
        if (Image == null) return;

        // Use flattened bitmap if annotations exist, otherwise base image
        var bitmapToCopy = Annotations.Any() ? await GetFlattenedBitmapAsync() : Image;
        if (bitmapToCopy == null) bitmapToCopy = Image;

        if (IsSelectionActive)
        {
             // Strategy: 
             // 1. Get flattened bitmap (entire image + annotations)
             // 2. Crop it using the same logic as GetSelectedBitmapAsync but operating on the new bitmap.
             
             var selected = await GetSelectedBitmapFromAsync(bitmapToCopy);
             if (selected != null)
             {
                 await _clipboardService.CopyImageAsync(selected);
             }
        }
        else
        {
            await _clipboardService.CopyImageAsync(bitmapToCopy);
        }
    }

    private async Task CutAsync()
    {
        if (Image == null || !IsSelectionActive) return;

        // 1. Copy selection to clipboard
        var selected = await GetSelectedBitmapAsync();
        if (selected != null)
        {
            await _clipboardService.CopyImageAsync(selected);
        }

        // 2. Actually crop it (Cut behavior in pinned window = Crop + Copy)
        await CropAsync();
    }

    private async Task CropAsync()
    {
        if (Image == null || !IsSelectionActive) return;

        var cropped = await GetSelectedBitmapAsync();
        if (cropped != null)
        {
            var oldImage = Image;
            var newImage = cropped;
            PushUndoAction(new BitmapHistoryAction(b => Image = b, oldImage, newImage));

            Image = cropped;
            SelectionRect = new Avalonia.Rect();
            IsSelectionMode = false;
        }
    }

    private async Task PinSelectionAsync()
    {
        if (Image == null || !IsSelectionActive || OpenPinWindowAction == null) return;

        var selected = await GetSelectedBitmapAsync();
        if (selected != null)
        {
            var rect = new Avalonia.Rect(0, 0, selected.Size.Width, selected.Size.Height);
            OpenPinWindowAction(selected, rect, BorderColor, BorderThickness, false);
            
            // Clear selection after pinning
            SelectionRect = new Avalonia.Rect();
            IsSelectionMode = false;
        }
    }

    private async Task<Bitmap?> GetFlattenedBitmapAsync()
    {
        if (Image == null) return null;
        
        return await Task.Run(() => 
        {
            try 
            {
                // 1. Save base image to stream to load into SKBitmap
                using var ms = new System.IO.MemoryStream();
                Image.Save(ms);
                ms.Position = 0;
                
                using var skBitmap = SkiaSharp.SKBitmap.Decode(ms);
                if (skBitmap == null) return null;
                
                // 2. Create a surface to draw on
                using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(skBitmap.Width, skBitmap.Height));
                using var canvas = surface.Canvas;
                
                // 3. Draw base image
                canvas.DrawBitmap(skBitmap, 0, 0);
                
                // 4. Draw Annotations
                // Need to map coordinates from Display (View) space to Image (Pixel) space
                var refW = DisplayWidth > 0 ? DisplayWidth : OriginalWidth;
                var refH = DisplayHeight > 0 ? DisplayHeight : OriginalHeight;
                var scaleX = (double)skBitmap.Width / refW;
                var scaleY = (double)skBitmap.Height / refH;
                
                foreach (var ann in Annotations)
                {
                    var paint = new SkiaSharp.SKPaint
                    {
                        Color = new SkiaSharp.SKColor(ann.Color.R, ann.Color.G, ann.Color.B, ann.Color.A),
                        StrokeWidth = (float)(ann.Thickness * scaleX), // Scale thickness too?
                        IsAntialias = true,
                        Style = SkiaSharp.SKPaintStyle.Stroke
                    };
                    
                    if (ann.Type == AnnotationType.Pen)
                    {
                        paint.StrokeCap = SkiaSharp.SKStrokeCap.Round;
                        paint.StrokeJoin = SkiaSharp.SKStrokeJoin.Round;
                    }

                    switch (ann.Type)
                    {
                        case AnnotationType.Rectangle:
                        case AnnotationType.Ellipse:
                            var rect = new SkiaSharp.SKRect(
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
                            // Draw Line
                            float x1 = (float)(ann.StartPoint.X * scaleX);
                            float y1 = (float)(ann.StartPoint.Y * scaleY);
                            float x2 = (float)(ann.EndPoint.X * scaleX);
                            float y2 = (float)(ann.EndPoint.Y * scaleY);
                            canvas.DrawLine(x1, y1, x2, y2, paint);
                            
                            // Draw Arrowhead (Simple approximation)
                            // Calculate angle
                            double angle = Math.Atan2(y2 - y1, x2 - x1);
                            double arrowLen = 15 * scaleX; 
                            double arrowAngle = Math.PI / 6;
                            
                            float ax1 = (float)(x2 - arrowLen * Math.Cos(angle - arrowAngle));
                            float ay1 = (float)(y2 - arrowLen * Math.Sin(angle - arrowAngle));
                            float ax2 = (float)(x2 - arrowLen * Math.Cos(angle + arrowAngle));
                            float ay2 = (float)(y2 - arrowLen * Math.Sin(angle + arrowAngle));
                            
                            var path = new SkiaSharp.SKPath();
                            path.MoveTo(x2, y2);
                            path.LineTo(ax1, ay1);
                            path.LineTo(ax2, ay2);
                            path.Close();
                            
                            paint.Style = SkiaSharp.SKPaintStyle.Fill;
                            canvas.DrawPath(path, paint);
                            break;
                         
                         case AnnotationType.Pen:
                             // Snapshot points to avoid concurrent modification issues and use DrawPoints
                             if (ann.Points.Count > 1)
                             {
                                 var points = ann.Points.Select(p => new SkiaSharp.SKPoint((float)(p.X * scaleX), (float)(p.Y * scaleY))).ToArray();
                                 if (points.Length > 1)
                                 {
                                     canvas.DrawPoints(SkiaSharp.SKPointMode.Polygon, points, paint);
                                 }
                             }
                             break;
                             
                         case AnnotationType.Text:
                             // Simplified text rendering
                             var font = new SkiaSharp.SKFont(SkiaSharp.SKTypeface.Default, (float)(ann.FontSize * scaleX));
                             var textPaint = new SkiaSharp.SKPaint
                             {
                                 Color = paint.Color,
                                 IsAntialias = true,
                             };
                             // Adjust for font family/weight if needed, keeping simple for now
                             canvas.DrawText(ann.Text, (float)(ann.StartPoint.X * scaleX), (float)(ann.StartPoint.Y * scaleY + ann.FontSize * scaleY), SkiaSharp.SKTextAlign.Left, font, textPaint);
                             break;
                    }
                }
                
                // 5. Export result
                using var image = surface.Snapshot();
                using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                using var resultMs = new System.IO.MemoryStream();
                data.SaveTo(resultMs);
                resultMs.Position = 0;
                
                return new Bitmap(resultMs);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error flattening bitmap: {ex}");
                return null;
            }
        });
    }

    private async Task<Bitmap?> GetSelectedBitmapAsync()
    {
        if (Image == null || !IsSelectionActive) return null;

        return await Task.Run(() =>
        {
            try
            {
                using var ms = new System.IO.MemoryStream();
                Image.Save(ms);
                ms.Position = 0;

                using var original = SkiaSharp.SKBitmap.Decode(ms);
                if (original == null) return null;

                // Must use current DisplayWidth/Height for scaling the UI selection to pixels
                var refW = DisplayWidth > 0 ? DisplayWidth : OriginalWidth;
                var refH = DisplayHeight > 0 ? DisplayHeight : OriginalHeight;
                var scaleX = (double)Image.PixelSize.Width / refW;
                var scaleY = (double)Image.PixelSize.Height / refH;

                int x = (int)Math.Round(Math.Max(0, SelectionRect.X * scaleX));
                int y = (int)Math.Round(Math.Max(0, SelectionRect.Y * scaleY));
                int w = (int)Math.Round(Math.Min(original.Width - x, SelectionRect.Width * scaleX));
                int h = (int)Math.Round(Math.Min(original.Height - y, SelectionRect.Height * scaleY));

                if (w <= 0 || h <= 0) return null;

                var cropped = new SkiaSharp.SKBitmap(w, h);
                if (original.ExtractSubset(cropped, new SkiaSharp.SKRectI(x, y, x + w, y + h)))
                {
                    using var cms = new System.IO.MemoryStream();
                    using var image = SkiaSharp.SKImage.FromBitmap(cropped);
                    using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                    data.SaveTo(cms);
                    cms.Position = 0;
                    return new Bitmap(cms);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error extracting selection: {ex}");
            }
            return null;
        });
    }

    private async Task<Bitmap?> GetSelectedBitmapFromAsync(Bitmap sourceBitmap)
    {
         if (sourceBitmap == null) return null;
         
         return await Task.Run(() =>
         {
             try
             {
                 using var ms = new System.IO.MemoryStream();
                 sourceBitmap.Save(ms);
                 ms.Position = 0;
                 using var original = SkiaSharp.SKBitmap.Decode(ms);
                 if (original == null) return null;

                var refW = DisplayWidth > 0 ? DisplayWidth : OriginalWidth;
                var refH = DisplayHeight > 0 ? DisplayHeight : OriginalHeight;
                var scaleX = (double)original.Width / refW; 
                var scaleY = (double)original.Height / refH;

                int x = (int)Math.Round(Math.Max(0, SelectionRect.X * scaleX));
                int y = (int)Math.Round(Math.Max(0, SelectionRect.Y * scaleY));
                int w = (int)Math.Round(Math.Min(original.Width - x, SelectionRect.Width * scaleX));
                int h = (int)Math.Round(Math.Min(original.Height - y, SelectionRect.Height * scaleY));

                if (w <= 0 || h <= 0) return null;

                var cropped = new SkiaSharp.SKBitmap(w, h);
                if (original.ExtractSubset(cropped, new SkiaSharp.SKRectI(x, y, x + w, y + h)))
                {
                    using var cms = new System.IO.MemoryStream();
                    using var image = SkiaSharp.SKImage.FromBitmap(cropped);
                    using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                    data.SaveTo(cms);
                    cms.Position = 0;
                    return new Bitmap(cms);
                }
             }
             catch(Exception ex)
             {
                 System.Diagnostics.Debug.WriteLine($"Error extracting selection from bitmap: {ex}");
             }
             return null;
         });
    }
}
