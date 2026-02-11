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
using SkiaSharp; 

namespace GimmeCapture.ViewModels.Floating;

public partial class FloatingVideoViewModel
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

    public ReactiveCommand<Unit, Unit> CloseCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> CopyCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> SaveCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> CropCommand { get; private set; } = null!; // Future implementation
    public ReactiveCommand<Unit, Unit> PinSelectionCommand { get; private set; } = null!; // Future implementation

    public System.Action? CloseAction { get; set; }
    public System.Func<Task>? CopyAction { get; set; }
    public System.Func<Task>? SaveAction { get; set; }

    private void InitializeActionCommands()
    {
        CloseCommand = ReactiveCommand.Create(() => 
        {
            Dispose();
            CloseAction?.Invoke();
        });

        // Placeholders for now
        CropCommand = ReactiveCommand.Create(() => { });
        PinSelectionCommand = ReactiveCommand.Create(() => { });

        CopyCommand = ReactiveCommand.CreateFromTask(async () => 
        {
            if (CopyAction != null)
            {
                // We don't have direct image access in CopyAction from here usually, 
                // but if we did, we'd handle the flattening here.
                // Currently CopyAction in FloatingVideoWindow probably handles the snapshot?
                // Actually, logic is: CopyAction uses CreateSnapshotFromVideo()
                await CopyAction();
            }
        });

        SaveCommand = ReactiveCommand.CreateFromTask(async () => 
        {
            if (SaveAction != null) await SaveAction();
        });
    }

    private async Task<Bitmap?> GetFlattenedBitmapAsync()
    {
        if (VideoBitmap == null) return null;
        
        return await Task.Run(() => 
        {
            try 
            {
                using var locked = VideoBitmap.Lock();
                var info = new SkiaSharp.SKImageInfo(VideoBitmap.PixelSize.Width, VideoBitmap.PixelSize.Height, SkiaSharp.SKColorType.Bgra8888);
                using var skBitmap = new SkiaSharp.SKBitmap(info);
                
                unsafe 
                {
                    long len = (long)info.BytesSize;
                    Buffer.MemoryCopy((void*)locked.Address, (void*)skBitmap.GetPixels(), len, len);
                }
                
                using var surface = SkiaSharp.SKSurface.Create(info);
                using var canvas = surface.Canvas;
                
                canvas.DrawBitmap(skBitmap, 0, 0);
                
                var refW = DisplayWidth > 0 ? DisplayWidth : OriginalWidth;
                var refH = DisplayHeight > 0 ? DisplayHeight : OriginalHeight;
                var scaleX = (double)_width / refW; 
                var scaleY = (double)_height / refH;
                
                foreach (var ann in Annotations)
                {
                    var paint = new SkiaSharp.SKPaint
                    {
                        Color = new SkiaSharp.SKColor(ann.Color.R, ann.Color.G, ann.Color.B, ann.Color.A),
                        StrokeWidth = (float)(ann.Thickness * scaleX),
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
                            
                            var path = new SkiaSharp.SKPath();
                            path.MoveTo(x2, y2);
                            path.LineTo(ax1, ay1);
                            path.LineTo(ax2, ay2);
                            path.Close();
                            
                            paint.Style = SkiaSharp.SKPaintStyle.Fill;
                            canvas.DrawPath(path, paint);
                            break;

                         case AnnotationType.Pen:
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
                             var font = new SkiaSharp.SKFont(SkiaSharp.SKTypeface.Default, (float)(ann.FontSize * scaleX));
                             var textPaint = new SkiaSharp.SKPaint
                             {
                                 Color = paint.Color,
                                 IsAntialias = true,
                             };
                             canvas.DrawText(ann.Text, (float)(ann.StartPoint.X * scaleX), (float)(ann.StartPoint.Y * scaleY + ann.FontSize * scaleY), SkiaSharp.SKTextAlign.Left, font, textPaint);
                             break;
                    }
                }
                
                using var image = surface.Snapshot();
                using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                using var resultMs = new System.IO.MemoryStream();
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
