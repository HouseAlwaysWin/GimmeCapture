using Avalonia.Media.Imaging;
using ReactiveUI;
using ReactiveUI.Reactive;
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
using System.Linq;
using System.Threading;
using SkiaSharp;

namespace GimmeCapture.ViewModels.Floating;

public partial class FloatingImageViewModel
{
    // Edits that replace Image run one at a time, now that their pixel work is off the UI thread: each starts from
    // the image the previous one produced instead of racing it.
    private readonly SemaphoreSlim _imageEditGate = new(1, 1);

    private void InitializeActionCommands()
    {
        // CloseCommand, ToggleToolbarCommand etc are in Base.
        
        // The window asks where to save, then has the picture encoded with its annotations (EncodeForSaveAsync).
        SaveCommand?.Dispose();
        SaveCommand = CreateAsyncCommand(async () =>
        {
            if (SaveAction != null)
            {
                await SaveAction();
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
        await _imageEditGate.WaitAsync();
        try
        {
            await ApplyTransformCoreAsync(rotationDegrees, flipH, flipV);
        }
        finally
        {
            _imageEditGate.Release();
        }
    }

    private async Task ApplyTransformCoreAsync(int rotationDegrees, bool flipH, bool flipV)
    {
        var oldImage = Image;
        if (oldImage == null) return;

        // Bake any annotations into the bitmap first so they transform together with the image.
        Bitmap source = (Annotations.AsValueEnumerable().Any() ? await GetFlattenedBitmapAsync() : oldImage) ?? oldImage;

        // Off the UI thread: three full copies plus the rotated draw, ~0.13 s for a 4K pin.
        Bitmap? transformed = await Task.Run(() => FloatingBitmapConversionHelper.TransformBitmap(source, rotationDegrees, flipH, flipV));
        if (!ReferenceEquals(source, oldImage))
        {
            // The annotations flattened into a copy, which is drawn into the result now: it was left to the collector.
            source.Dispose();
        }

        if (transformed == null) return;
        if (!ReferenceEquals(Image, oldImage))
        {
            // The image changed meanwhile (an undo): this result no longer applies to it.
            transformed.Dispose();
            return;
        }

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

    // Puts the picture, with its annotations drawn in and cut to the selection when there is one, on the clipboard as a
    // file in the toolbar's SelectedImageFormat (so pasting into a folder yields the chosen format, like the pin's Save)
    // alongside the picture itself (so pasting into an editor still works); the picture alone if the file cannot be made.
    //
    // A clipboard write can lose the race for the clipboard, and that leaves the PREVIOUS content in place — so
    // the outcome is logged rather than assumed; a pin has no status line to report it on.
    private async Task CopyAsync()
    {
        // Read on the UI thread, where they change; the pool thread only sees this snapshot.
        var image = Image;
        if (image == null) return;

        Avalonia.PixelRect? selection = null;
        if (IsSelectionActive)
        {
            selection = SelectionPixelRect(image);
            if (selection == null) return;
        }

        var annotations = Annotations.ToArray();
        double displayWidth = DisplayWidth;
        double displayHeight = DisplayHeight;
        string fmt = (SelectedImageFormat ?? "PNG").ToLowerInvariant();
        var (skFormat, quality) = fmt switch
        {
            "jpg" or "jpeg" => (SKEncodedImageFormat.Jpeg, 92),
            "webp" => (SKEncodedImageFormat.Webp, 90),
            _ => (SKEncodedImageFormat.Png, 100)
        };

        // Off the UI thread: a 4K PNG took over 2 s, about 0.33 s with the fast settings the clipboard's gets.
        var (png, file) = await Task.Run(() =>
            EncodeForCopy(image, annotations, displayWidth, displayHeight, selection, skFormat, quality));
        if (png == null)
        {
            WarnCopyDidNotLand("FloatingImage.Copy");
            return;
        }

        string? temp = null;
        if (file != null)
        {
            try
            {
                string tempDir = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "GimmeCapture_Copy_" + Guid.NewGuid().ToString("N"));
                System.IO.Directory.CreateDirectory(tempDir);
                string ext = fmt == "jpeg" ? "jpg" : fmt;
                temp = System.IO.Path.Combine(
                    tempDir, GimmeCapture.Services.Core.Infrastructure.CaptureFileNameService.SuggestedBaseName() + "." + ext);
                await System.IO.File.WriteAllBytesAsync(temp, file);
            }
            catch (Exception ex)
            {
                GimmeCapture.Services.Core.Infrastructure.AppLog.Error("FloatingImage.CopyFormat", ex);
                temp = null;
            }
        }

        if (!await _clipboardService.CopyPngAsync(png, temp))
        {
            WarnCopyDidNotLand(temp != null ? "FloatingImage.CopyFormat" : "FloatingImage.Copy");
        }
    }

    /// <summary>
    /// What a copy puts on the clipboard: the PNG, and the file in <paramref name="format"/>, the same bytes for a PNG and
    /// null where that encode fails; nulls where the picture cannot be read. One copy of the picture, of the selection
    /// alone when nothing is drawn on it, with the annotations drawn in and the selection cut out of it in place. A copy
    /// used to make a whole new picture for the annotations and another for the selection, which it never disposed, and
    /// encoded the PNG twice: once for the file, and again in the clipboard service.
    /// </summary>
    private static (byte[]? Png, byte[]? File) EncodeForCopy(
        Bitmap image, Annotation[] annotations, double displayWidth, double displayHeight, Avalonia.PixelRect? selection,
        SKEncodedImageFormat format, int quality)
    {
        bool drawnOn = annotations.Length > 0;
        SKBitmap? pixels = !drawnOn && selection is { } region
            ? FloatingBitmapConversionHelper.CopyRegionToSkBitmap(image, region)
            : FloatingBitmapConversionHelper.ToSkBitmap(image);
        if (pixels == null)
        {
            return (null, null);
        }

        using (pixels)
        {
            SKBitmap source = pixels;
            using var cut = new SKBitmap();
            if (drawnOn)
            {
                AnnotationRenderService.Shared.RenderAnnotationsToBitmap(
                    pixels, annotations, displayWidth, displayHeight, pixels.Width, pixels.Height);
                if (selection is { } part)
                {
                    // Shares the pixels it is cut from: no copy.
                    if (!pixels.ExtractSubset(cut, new SKRectI(part.X, part.Y, part.Right, part.Bottom)))
                    {
                        return (null, null);
                    }

                    source = cut;
                }
            }

            if (!FloatingBitmapConversionHelper.TryEncodeSkBitmap(source, SKEncodedImageFormat.Png, 100, out var png, out _, fastPng: true))
            {
                return (null, null);
            }

            if (format == SKEncodedImageFormat.Png)
            {
                return (png, png);
            }

            return (png, FloatingBitmapConversionHelper.TryEncodeSkBitmap(source, format, quality, out var file, out _) ? file : null);
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
        await _imageEditGate.WaitAsync();
        try
        {
            await CropCoreAsync();
        }
        finally
        {
            _imageEditGate.Release();
        }
    }

    private async Task CropCoreAsync()
    {
        if (!IsSelectionActive || Image == null) return;

        // Capture state BEFORE changes for Undo
        var oldImage = Image;
        var oldRect = SelectionRect;
        var selected = await GetSelectedBitmapAsync();
        if (selected != null && !ReferenceEquals(Image, oldImage))
        {
            selected.Dispose(); // the image changed meanwhile (an undo): the crop no longer applies to it
            return;
        }

        if (selected != null)
        {
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

    /// <summary>
    /// The selection (<see cref="SelectionRect"/>, in the pin's display coordinates) in the pixels of
    /// <paramref name="source"/>, clipped to it; null when it misses the picture.
    /// </summary>
    private Avalonia.PixelRect? SelectionPixelRect(Bitmap source)
    {
        // The picture is drawn at DisplayWidth x DisplayHeight.
        if (source.Size.Width <= 0 || source.Size.Height <= 0) return null;

        double scaleX = source.Size.Width / (DisplayWidth > 0 ? DisplayWidth : 1);
        double scaleY = source.Size.Height / (DisplayHeight > 0 ? DisplayHeight : 1);
        var pixelRect = new Avalonia.Rect(
            SelectionRect.X * scaleX,
            SelectionRect.Y * scaleY,
            SelectionRect.Width * scaleX,
            SelectionRect.Height * scaleY);
        var intersect = pixelRect.Intersect(new Avalonia.Rect(0, 0, source.Size.Width, source.Size.Height));
        if (intersect.Width <= 0 || intersect.Height <= 0) return null;

        int sourceWidth = (int)source.Size.Width;
        int sourceHeight = (int)source.Size.Height;
        int left = Math.Clamp((int)Math.Floor(intersect.X), 0, Math.Max(0, sourceWidth - 1));
        int top = Math.Clamp((int)Math.Floor(intersect.Y), 0, Math.Max(0, sourceHeight - 1));
        int right = Math.Clamp((int)Math.Ceiling(intersect.X + intersect.Width), left + 1, sourceWidth);
        int bottom = Math.Clamp((int)Math.Ceiling(intersect.Y + intersect.Height), top + 1, sourceHeight);
        return right - left <= 0 || bottom - top <= 0 ? null : new Avalonia.PixelRect(left, top, right - left, bottom - top);
    }
    
    private async Task<Bitmap?> GetSelectedBitmapFromAsync(Bitmap? source)
    {
        if (source == null || SelectionPixelRect(source) is not { } crop) return null;

        try
        {
            return await Task.Run(() =>
            {
                var outBitmap = new Avalonia.Media.Imaging.WriteableBitmap(
                    new Avalonia.PixelSize(crop.Width, crop.Height),
                    new Avalonia.Vector(96, 96),
                    Avalonia.Platform.PixelFormat.Bgra8888,
                    Avalonia.Platform.AlphaFormat.Premul);

                using var lockedOut = outBitmap.Lock();
                source.CopyPixels(crop, lockedOut.Address, lockedOut.RowBytes * lockedOut.Size.Height, lockedOut.RowBytes);
                return (Bitmap?)outBitmap;
            });
        }
        catch (Exception ex)
        {
            AppLog.Warning("FloatingImage.Crop", ex);
            return null;
        }
    }
    
    /// <summary>
    /// The pin's picture encoded as <paramref name="format"/>, with its annotations drawn in when it has any: what a save
    /// writes, which the window asks for once a file is picked. The annotations are drawn into the copy that is encoded,
    /// which goes once it is. The save command used to swap a flattened copy in as the pin's picture for the whole
    /// dialog, cancelled or not: the pin drew its annotations twice meanwhile, smart selection lost its prepared picture
    /// and its points, and the copy, 33 MB at 4K, was left to the collector.
    /// </summary>
    internal async Task<(bool Encoded, byte[] Bytes, string? Error)> EncodeForSaveAsync(SKEncodedImageFormat format, int quality)
    {
        // Read on the UI thread, where they change; the pool thread only sees this snapshot.
        var image = Image;
        if (image == null)
        {
            return (false, Array.Empty<byte>(), "The pin has no picture.");
        }

        var annotations = Annotations.ToArray();
        double displayWidth = DisplayWidth;
        double displayHeight = DisplayHeight;

        // Off the UI thread: a 4K PNG takes over 2 s at full compression.
        return await Task.Run(() =>
        {
            if (!FloatingBitmapConversionHelper.TryCopyToSkBitmap(image, out var pixels, out var error) || pixels == null)
            {
                return (false, Array.Empty<byte>(), error);
            }

            using (pixels)
            {
                if (annotations.Length > 0)
                {
                    AnnotationRenderService.Shared.RenderAnnotationsToBitmap(
                        pixels, annotations, displayWidth, displayHeight, pixels.Width, pixels.Height);
                }

                bool encoded = FloatingBitmapConversionHelper.TryEncodeSkBitmap(pixels, format, quality, out var bytes, out error);
                return (encoded, bytes, error);
            }
        });
    }

    // Create a flattened bitmap including annotations
    private async Task<Bitmap?> GetFlattenedBitmapAsync()
    {
        // Read on the UI thread, where they change; the pool thread only sees this snapshot.
        var image = Image;
        if (image == null) return null;
        var annotations = Annotations.ToArray();
        double displayWidth = DisplayWidth;
        double displayHeight = DisplayHeight;

        return await Task.Run(() => 
        {
            try 
            {
                if (!FloatingBitmapConversionHelper.TryCopyToSkBitmap(image, out var baseSkBitmap, out _)
                    || baseSkBitmap == null)
                    return null;

                using (baseSkBitmap)
                {
                    AnnotationRenderService.Shared.RenderAnnotationsToBitmap(
                        baseSkBitmap,
                        annotations,
                        displayWidth,
                        displayHeight,
                        baseSkBitmap.Width,
                        baseSkBitmap.Height);

                    if (!FloatingBitmapConversionHelper.TryCreateDetachedBitmapFromSkBitmap(baseSkBitmap, out var detachedBitmap, out _))
                        return null;

                    return detachedBitmap;
                }
            }
            catch (Exception ex)
            {
                 AppLog.Warning("FloatingImage.Flatten", ex);
                 return null;
            }
        });
    }
}
