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
            OpenPinWindowAction(selected, SelectionRect, BorderColor, BorderThickness, false, false, PinnedText, InferredFontSize);
            
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
                
                using var locked = Image.Lock();
                var info = new SkiaSharp.SKImageInfo(locked.Size.Width, locked.Size.Height, SkiaSharp.SKColorType.Bgra8888);
                using var baseSkBitmap = new SkiaSharp.SKBitmap(info);
                
                unsafe 
                {
                    long len = (long)info.BytesSize;
                    Buffer.MemoryCopy((void*)locked.Address, (void*)baseSkBitmap.GetPixels(), len, len);
                }
                
                using var surface = SkiaSharp.SKSurface.Create(info);
                using var canvas = surface.Canvas;
                
                canvas.DrawBitmap(baseSkBitmap, 0, 0);
                
                // Draw Annotations
                // We need to scale annotations from Display Coordinates to Image Coordinates
                GimmeCapture.Services.Core.Rendering.AnnotationRenderHelper.DrawAnnotationsOnCanvas(canvas, Annotations, DisplayWidth, DisplayHeight, baseSkBitmap.Width, baseSkBitmap.Height);
                
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
