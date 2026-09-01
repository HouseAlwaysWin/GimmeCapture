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
using GimmeCapture.Services.Core.Rendering;
using System.Reactive.Linq;
using System;
using SkiaSharp;

namespace GimmeCapture.ViewModels.Floating;

public partial class FloatingImageViewModel
{
    private void InitializeActionCommands()
    {
        // CloseCommand, ToggleToolbarCommand etc are in Base.
        
        // Re-define SaveCommand with specific logic (Flattening)
        SaveCommand?.Dispose();
        SaveCommand = CreateAsyncCommand(async () =>
        {
             if (SaveAction != null)
             {
                 // Temporary swap of Image for flattened version if we have annotations
                 var originalImage = Image;
                 var flattened = Annotations.AsValueEnumerable().Any() ? await GetFlattenedBitmapAsync() : null;
                 
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
        }, null, nameof(SaveCommand));

        var canUseSelection = this.WhenAnyValue(x => x.IsSelectionActive);
        CopyCommand = CreateAsyncCommand(CopyAsync, null, nameof(CopyCommand));
        CutCommand = CreateAsyncCommand(CutAsync, canUseSelection, nameof(CutCommand));
        CropCommand = CreateAsyncCommand(CropAsync, canUseSelection, nameof(CropCommand));
        PinSelectionCommand = CreateAsyncCommand(PinSelectionAsync, canUseSelection, nameof(PinSelectionCommand));
        RotateLeftCommand = CreateAsyncCommand(() => ApplyTransformAsync(270, false, false), null, nameof(RotateLeftCommand));
        RotateRightCommand = CreateAsyncCommand(() => ApplyTransformAsync(90, false, false), null, nameof(RotateRightCommand));
        FlipHorizontalCommand = CreateAsyncCommand(() => ApplyTransformAsync(0, true, false), null, nameof(FlipHorizontalCommand));
        FlipVerticalCommand = CreateAsyncCommand(() => ApplyTransformAsync(0, false, true), null, nameof(FlipVerticalCommand));
    }

    /// <summary>
    /// Rotates/flips the pinned image. Annotations are flattened in first (so they rotate with the picture),
    /// the window is resized when the aspect flips (90°/270°), and the whole change is one undo step.
    /// </summary>
    private async Task ApplyTransformAsync(int rotationDegrees, bool flipH, bool flipV)
    {
        if (Image == null) return;

        // Bake any annotations into the bitmap first so they transform together with the image.
        Bitmap source = (Annotations.AsValueEnumerable().Any() ? await GetFlattenedBitmapAsync() : Image) ?? Image;

        Bitmap? transformed = FloatingBitmapConversionHelper.TransformBitmap(source, rotationDegrees, flipH, flipV);
        if (transformed == null) return;

        var oldImage = Image;
        var oldPos = ScreenPosition ?? new Avalonia.PixelPoint(0, 0);
        var oldDisplayWidth = DisplayWidth;
        var oldDisplayHeight = DisplayHeight;

        bool swap = rotationDegrees == 90 || rotationDegrees == 270;
        double newDisplayWidth = swap ? oldDisplayHeight : oldDisplayWidth;
        double newDisplayHeight = swap ? oldDisplayWidth : oldDisplayHeight;

        Image = transformed;
        OriginalWidth = transformed.Size.Width;
        OriginalHeight = transformed.Size.Height;
        DisplayWidth = newDisplayWidth;
        DisplayHeight = newDisplayHeight;
        RequestSetWindowRect?.Invoke(oldPos, DisplayWidth, DisplayHeight, DisplayWidth, DisplayHeight);

        // Annotations were baked into the bitmap; clear the live layer so they aren't drawn twice.
        ClearAnnotations();

        var bitmapAction = new BitmapHistoryAction(b => Image = b, oldImage, transformed, getCurrentBitmap: () => Image);
        var transformAction = new WindowTransformHistoryAction(
            (pos, w, h, cw, ch) =>
            {
                DisplayWidth = cw;
                DisplayHeight = ch;
                ScreenPosition = pos;
                RequestSetWindowRect?.Invoke(pos, w, h, cw, ch);
            },
            oldPos, oldDisplayWidth, oldDisplayHeight, oldDisplayWidth, oldDisplayHeight,
            oldPos, newDisplayWidth, newDisplayHeight, newDisplayWidth, newDisplayHeight);

        PushUndoAction(new CompositeHistoryAction(new IHistoryAction[] { bitmapAction, transformAction }));
    }

    private async Task CopyAsync()
    {
        if (Image == null) return;

        // Use flattened bitmap if annotations exist, otherwise base image
        var bitmapToCopy = Annotations.AsValueEnumerable().Any() ? await GetFlattenedBitmapAsync() : Image;
        if (bitmapToCopy == null) bitmapToCopy = Image;

        if (IsSelectionActive)
        {
             // Strategy:
             // 1. Get flattened bitmap (entire image + annotations)
             // 2. Crop it using the same logic as GetSelectedBitmapAsync but operating on the new bitmap.

             var selected = await GetSelectedBitmapFromAsync(bitmapToCopy);
             if (selected != null)
             {
                 await CopyBitmapInSelectedFormatAsync(selected);
             }
        }
        else
        {
            await CopyBitmapInSelectedFormatAsync(bitmapToCopy);
        }
    }

    // Puts the image on the clipboard as a file in the toolbar's SelectedImageFormat (so pasting into a folder
    // yields the chosen format, like the pin's Save) alongside the raw bitmap (so pasting into an editor still
    // works). Falls back to a plain image copy if encoding the file fails.
    //
    // A clipboard write can lose the race for the clipboard, and that leaves the PREVIOUS content in place — so
    // the outcome is logged rather than assumed; a pin has no status line to report it on.
    private async Task CopyBitmapInSelectedFormatAsync(Bitmap bitmap)
    {
        string fmt = (SelectedImageFormat ?? "PNG").ToLowerInvariant();
        var (skFormat, quality) = fmt switch
        {
            "jpg" or "jpeg" => (SKEncodedImageFormat.Jpeg, 92),
            "webp" => (SKEncodedImageFormat.Webp, 90),
            _ => (SKEncodedImageFormat.Png, 100)
        };

        if (FloatingBitmapConversionHelper.TryEncodeBitmap(bitmap, skFormat, quality, out var bytes, out _))
        {
            try
            {
                string tempDir = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "GimmeCapture_Copy_" + Guid.NewGuid().ToString("N"));
                System.IO.Directory.CreateDirectory(tempDir);
                string ext = fmt == "jpeg" ? "jpg" : fmt;
                string temp = System.IO.Path.Combine(
                    tempDir, GimmeCapture.Services.Core.Infrastructure.CaptureFileNameService.SuggestedBaseName() + "." + ext);
                await System.IO.File.WriteAllBytesAsync(temp, bytes);
                if (!await _clipboardService.CopyFileAndImageAsync(temp, bitmap))
                {
                    WarnCopyDidNotLand("FloatingImage.CopyFormat");
                }

                return;
            }
            catch (Exception ex)
            {
                GimmeCapture.Services.Core.Infrastructure.AppLog.Error("FloatingImage.CopyFormat", ex);
            }
        }

        if (!await _clipboardService.CopyImageAsync(bitmap))
        {
            WarnCopyDidNotLand("FloatingImage.Copy");
        }
    }

    private static void WarnCopyDidNotLand(string context) =>
        GimmeCapture.Services.Core.Infrastructure.AppLog.Warning(
            context,
            new InvalidOperationException(
                "Clipboard write did not land — the clipboard still holds its previous content."));

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
            var bitmapAction = new BitmapHistoryAction(b => Image = b, oldImage, selected, getCurrentBitmap: () => Image);
            
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
            OpenPinWindowAction(selected, SelectionRect, BorderColor, BorderThickness, false, false, PinnedText, InferredFontSize, null);
            
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
            return await Task.Run(() =>
            {
                int sourceWidth = (int)source.Size.Width;
                int sourceHeight = (int)source.Size.Height;

                int left = Math.Clamp((int)Math.Floor(intersect.X), 0, Math.Max(0, sourceWidth - 1));
                int top = Math.Clamp((int)Math.Floor(intersect.Y), 0, Math.Max(0, sourceHeight - 1));
                int right = Math.Clamp((int)Math.Ceiling(intersect.X + intersect.Width), left + 1, sourceWidth);
                int bottom = Math.Clamp((int)Math.Ceiling(intersect.Y + intersect.Height), top + 1, sourceHeight);

                int cropWidth = right - left;
                int cropHeight = bottom - top;
                if (cropWidth <= 0 || cropHeight <= 0) return null;

                var outBitmap = new Avalonia.Media.Imaging.WriteableBitmap(
                    new Avalonia.PixelSize(cropWidth, cropHeight),
                    new Avalonia.Vector(96, 96),
                    Avalonia.Platform.PixelFormat.Bgra8888,
                    Avalonia.Platform.AlphaFormat.Premul);

                using var lockedOut = outBitmap.Lock();
                source.CopyPixels(
                    new Avalonia.PixelRect(left, top, cropWidth, cropHeight),
                    lockedOut.Address,
                    lockedOut.RowBytes * lockedOut.Size.Height,
                    lockedOut.RowBytes);

                return outBitmap;
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
                if (!FloatingBitmapConversionHelper.TryCopyToSkBitmap(Image, out var baseSkBitmap, out _)
                    || baseSkBitmap == null)
                    return null;

                using (baseSkBitmap)
                {
                    AnnotationRenderService.Shared.RenderAnnotationsToBitmap(
                        baseSkBitmap,
                        Annotations,
                        DisplayWidth,
                        DisplayHeight,
                        baseSkBitmap.Width,
                        baseSkBitmap.Height);

                    if (!FloatingBitmapConversionHelper.TryCreateDetachedBitmapFromSkBitmap(baseSkBitmap, out var detachedBitmap, out _))
                        return null;

                    return detachedBitmap;
                }
            }
            catch (Exception ex)
            {
                 System.Diagnostics.Debug.WriteLine(ex);
                 return null;
            }
        });
    }
}
