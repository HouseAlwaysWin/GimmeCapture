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
    private void InitializeActionCommands()
    {
        // CloseCommand, ToggleToolbarCommand etc are in Base.
        
        // Re-define SaveCommand with specific logic (Flattening)
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
                         // flattened.Dispose(); 
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
        if (!IsSelectionActive || Image == null) return;

        // 1. Copy selection
        await CopyAsync();

        // 2. Clear selection area (make transparent or fill with background?)
        // For now, we don't have an "Erase Area" function easily accessible on the bitmap directly here without drawing.
        // But we have "Delete" or "PointRemoval".
        // Let's implement Cut as "Copy + Add Mask/Mosaic" or simply "Copy". 
        // Real Cut on a Raster image implies erasing pixels.
        
        // TODO: Implement Erase pixels in selection
    }

    private async Task CropAsync()
    {
        if (!IsSelectionActive || Image == null) return;
        
        var selected = await GetSelectedBitmapAsync();
        if (selected != null)
        {
            // Capture state BEFORE changes for Undo
            var oldImage = Image;
            var oldRect = SelectionRect;
            var oldPos = ScreenPosition ?? new Avalonia.PixelPoint(0, 0);
            var oldDisplayWidth = DisplayWidth;
            var oldDisplayHeight = DisplayHeight;
            
            // Calculate new position (align top-left of crop to where it was on screen)
            var newPos = new Avalonia.PixelPoint(oldPos.X + (int)oldRect.X, oldPos.Y + (int)oldRect.Y);

            // Set new image
            Image = selected;
            
            // Update Data Dimensions
            OriginalWidth = selected.Size.Width;
            OriginalHeight = selected.Size.Height;
            
            // Update Display Dimensions (Resize window)
            DisplayWidth = oldRect.Width;
            DisplayHeight = oldRect.Height;
            
            // Update Position
            ScreenPosition = newPos;

            // Force Window Update
            RequestSetWindowRect?.Invoke(newPos, DisplayWidth, DisplayHeight, DisplayWidth, DisplayHeight);

            // Create History Actions
            // Use captured oldImage
            var bitmapAction = new BitmapHistoryAction(b => Image = b, oldImage, selected);
            
            // Window Transform Action
            var transformAction = new WindowTransformHistoryAction(
                (pos, w, h, cw, ch) => {
                    DisplayWidth = cw;
                    DisplayHeight = ch;
                    ScreenPosition = pos; 
                    RequestSetWindowRect?.Invoke(pos, w, h, cw, ch);
                },
                oldPos, oldDisplayWidth, oldDisplayHeight, oldDisplayWidth, oldDisplayHeight, 
                newPos, DisplayWidth, DisplayHeight, DisplayWidth, DisplayHeight
            );

            // Push Composite Action
            PushUndoAction(new CompositeHistoryAction(new IHistoryAction[] { bitmapAction, transformAction }));
            
            // Reset Selection
            SelectionRect = new Avalonia.Rect();
            
            // Clear Annotations as they won't align
            ClearAnnotations();
        }
    }

    private async Task PinSelectionAsync()
    {
        if (!IsSelectionActive || Image == null) return;

        var selected = await GetSelectedBitmapAsync();
        if (selected != null && OpenPinWindowAction != null)
        {
            // Open new Pin Window with selected content
            // arg5: runAI = false (Do NOT auto-remove background)
            OpenPinWindowAction(selected, SelectionRect, BorderColor, BorderThickness, false);
            
            // Do NOT close the current window.
            // User expects "Pin" to create a NEW window, preserving the source.
            // CloseAction?.Invoke();
        }
    }

    private async Task<Bitmap?> GetSelectedBitmapAsync()
    {
        return await GetSelectedBitmapFromAsync(Image);
    }
    
    private async Task<Bitmap?> GetSelectedBitmapFromAsync(Bitmap? source)
    {
        if (source == null) return null;

        // Calculate actual pixel rect from SelectionRect (which is in Display coordinates)
        // Image is displayed at DisplayWidth x DisplayHeight
        // Actual Image is Image.Size.Width x Image.Size.Height
        
        // If Image is null or W/H is 0, return null
        if (source.Size.Width <= 0 || source.Size.Height <= 0) return null;

        double scaleX = source.Size.Width / (DisplayWidth > 0 ? DisplayWidth : 1);
        double scaleY = source.Size.Height / (DisplayHeight > 0 ? DisplayHeight : 1);
        
        // Use the larger scale or specific axis scale? 
        // usually uniform stretch.
        
        // SelectionRect is relative to local control 0,0.
        
        var pixelRect = new Avalonia.Rect(
            SelectionRect.X * scaleX,
            SelectionRect.Y * scaleY,
            SelectionRect.Width * scaleX,
            SelectionRect.Height * scaleY
        );

        // Intersect with image bounds
        var imageRect = new Avalonia.Rect(0, 0, source.Size.Width, source.Size.Height);
        var intersect = pixelRect.Intersect(imageRect);

        if (intersect.Width <= 0 || intersect.Height <= 0) return null;
        
        try 
        {
            // Crop
            // Avalonia Bitmap doesn't have easy Crop?
            // Use SkiaSharp or WriteableBitmap lookup
            
            return await Task.Run(() => 
            {
                using var stream = new System.IO.MemoryStream();
                source.Save(stream);
                stream.Position = 0;
                
                using var skBitmap = SkiaSharp.SKBitmap.Decode(stream);
                var subset = new SkiaSharp.SKBitmap();
                
                SkiaSharp.SKRectI skRect = new SkiaSharp.SKRectI(
                    (int)intersect.X, (int)intersect.Y, 
                    (int)(intersect.X + intersect.Width), 
                    (int)(intersect.Y + intersect.Height));
                    
                if (skBitmap.ExtractSubset(subset, skRect))
                {
                    // Convert back to Avalonia Bitmap
                    using var valImg = SkiaSharp.SKImage.FromBitmap(subset);
                    using var data = valImg.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                    using var outStream = new System.IO.MemoryStream();
                    data.SaveTo(outStream);
                    outStream.Position = 0;
                    return new Bitmap(outStream);
                }
                return null;
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Crop failed: {ex}");
            return null;
        }
    }
    
    // Create a flattened bitmap including annotations
    private async Task<Bitmap?> GetFlattenedBitmapAsync()
    {
        if (Image == null) return null;
        
        return await Task.Run(() => 
        {
            try 
            {
                // We need to draw the base image and then draw all annotations on top.
                // Using SkiaSharp is the most robust way.
                
                using var stream = new System.IO.MemoryStream();
                Image.Save(stream);
                stream.Position = 0;
                
                using var baseSkBitmap = SkiaSharp.SKBitmap.Decode(stream);
                
                var info = new SkiaSharp.SKImageInfo(baseSkBitmap.Width, baseSkBitmap.Height);
                using var surface = SkiaSharp.SKSurface.Create(info);
                using var canvas = surface.Canvas;
                
                canvas.DrawBitmap(baseSkBitmap, 0, 0);
                
                // Draw Annotations
                // We need to scale annotations from Display Coordinates to Image Coordinates
                 double scaleX = baseSkBitmap.Width / (DisplayWidth > 0 ? DisplayWidth : 1);
                 double scaleY = baseSkBitmap.Height / (DisplayHeight > 0 ? DisplayHeight : 1);
                
                foreach (var ann in Annotations)
                {
                    using var paint = new SkiaSharp.SKPaint();
                    paint.Color = new SkiaSharp.SKColor(ann.Color.R, ann.Color.G, ann.Color.B, ann.Color.A);
                    paint.IsAntialias = true;
                    paint.StrokeWidth = (float)(ann.Thickness * scaleX); // Scale thickness?
                    paint.Style = SkiaSharp.SKPaintStyle.Stroke;
                    
                    if (ann.Type == AnnotationType.Pen) // Highlighter removed if not in enum
                    {
                        if (ann.Points != null && ann.Points.Count > 1)
                        {
                            // If we tracked highlighter properly we'd check it here. 
                            // For now assume Pen.
                            
                            var path = new SkiaSharp.SKPath();
                            path.MoveTo((float)(ann.Points[0].X * scaleX), (float)(ann.Points[0].Y * scaleY));
                            
                            for (int i = 1; i < ann.Points.Count; i++)
                            {
                                path.LineTo((float)(ann.Points[i].X * scaleX), (float)(ann.Points[i].Y * scaleY));
                            }
                            canvas.DrawPath(path, paint);
                        }
                    }
                    else if (ann.Type == AnnotationType.Rectangle)
                    {
                         var rect = new SkiaSharp.SKRect(
                             (float)(Math.Min(ann.StartPoint.X, ann.EndPoint.X) * scaleX),
                             (float)(Math.Min(ann.StartPoint.Y, ann.EndPoint.Y) * scaleY),
                             (float)(Math.Max(ann.StartPoint.X, ann.EndPoint.X) * scaleX),
                             (float)(Math.Max(ann.StartPoint.Y, ann.EndPoint.Y) * scaleY));
                         canvas.DrawRect(rect, paint);
                    }
                    else if (ann.Type == AnnotationType.Ellipse)
                    {
                         var rect = new SkiaSharp.SKRect(
                             (float)(Math.Min(ann.StartPoint.X, ann.EndPoint.X) * scaleX),
                             (float)(Math.Min(ann.StartPoint.Y, ann.EndPoint.Y) * scaleY),
                             (float)(Math.Max(ann.StartPoint.X, ann.EndPoint.X) * scaleX),
                             (float)(Math.Max(ann.StartPoint.Y, ann.EndPoint.Y) * scaleY));
                         canvas.DrawOval(rect, paint);
                    }
                    else if (ann.Type == AnnotationType.Arrow)
                    {
                        // Simple Arrow drawing
                        float x1 = (float)(ann.StartPoint.X * scaleX);
                        float y1 = (float)(ann.StartPoint.Y * scaleY);
                        float x2 = (float)(ann.EndPoint.X * scaleX);
                        float y2 = (float)(ann.EndPoint.Y * scaleY);
                        
                        canvas.DrawLine(x1, y1, x2, y2, paint);
                        
                        // Draw Arrowhead (simple)
                        // ...
                    }
                    else if (ann.Type == AnnotationType.Text)
                    {
                         using var textPaint = new SkiaSharp.SKPaint();
                         textPaint.Color = paint.Color;
                         textPaint.IsAntialias = true;
#pragma warning disable CS0618 // SKPaint.TextSize is obsolete
                         textPaint.TextSize = (float)(ann.FontSize * scaleX);
#pragma warning restore CS0618
                         // textPaint.Typeface = ...
                         
                         // canvas.DrawText(ann.Text, (float)(ann.StartPoint.X * scaleX), (float)(ann.StartPoint.Y * scaleY), textPaint);
                         // Text positioning is usually top-left or baseline. SkiaDrawText is baseline.
                         // Need detailed text layout for perfect match. 
                    }
                    // ... other types
                }
                
                using var image = surface.Snapshot();
                using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                using var outStream = new System.IO.MemoryStream();
                data.SaveTo(outStream);
                outStream.Position = 0;
                return new Bitmap(outStream);
            }
            catch (Exception ex)
            {
                 System.Diagnostics.Debug.WriteLine(ex);
                 return null;
            }
        });
    }
}
