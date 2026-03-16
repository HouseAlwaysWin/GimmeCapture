using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using GimmeCapture.Models;
using ReactiveUI;
using System;
using System.Buffers;
using System.Reactive.Disposables;
using System.Reactive.Linq;

namespace GimmeCapture.Views.Controls;

public partial class AnnotationControl : UserControl
{
    public static readonly StyledProperty<Annotation?> AnnotationProperty =
        AvaloniaProperty.Register<AnnotationControl, Annotation?>(nameof(Annotation));

    public Annotation? Annotation
    {
        get => GetValue(AnnotationProperty);
        set => SetValue(AnnotationProperty, value);
    }

    /// <summary>
    /// Pre-pixelated bitmap for the Mosaic annotation preview.
    /// Generated in code to exactly match the SkiaSharp output algorithm.
    /// </summary>
    public static readonly StyledProperty<WriteableBitmap?> MosaicPreviewBitmapProperty =
        AvaloniaProperty.Register<AnnotationControl, WriteableBitmap?>(nameof(MosaicPreviewBitmap));

    public WriteableBitmap? MosaicPreviewBitmap
    {
        get => GetValue(MosaicPreviewBitmapProperty);
        set => SetValue(MosaicPreviewBitmapProperty, value);
    }

    private IDisposable? _mosaicSubscription;

    public AnnotationControl()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _mosaicSubscription?.Dispose();

        if (DataContext is Annotation ann && ann.Type == AnnotationType.Mosaic)
        {
            // Subscribe to point changes and snapshot changes to regenerate mosaic
            _mosaicSubscription = Observable.CombineLatest(
                ann.WhenAnyValue(a => a.StartPoint),
                ann.WhenAnyValue(a => a.EndPoint),
                ann.WhenAnyValue(a => a.DrawingModeSnapshot),
                (start, end, snapshot) => (start, end, snapshot))
                .Throttle(TimeSpan.FromMilliseconds(33)) // ~30fps to reduce allocation churn
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(tuple => GenerateMosaicPreview(tuple.snapshot, tuple.start, tuple.end));
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _mosaicSubscription?.Dispose();
        _mosaicSubscription = null;

        var old = MosaicPreviewBitmap;
        MosaicPreviewBitmap = null;
        old?.Dispose();
    }

    private void GenerateMosaicPreview(Bitmap? snapshot, Point startPoint, Point endPoint)
    {
        if (snapshot == null) return;

        // Compute annotation rect in logical coordinates (same as output)
        double x1 = Math.Min(startPoint.X, endPoint.X);
        double y1 = Math.Min(startPoint.Y, endPoint.Y);
        double x2 = Math.Max(startPoint.X, endPoint.X);
        double y2 = Math.Max(startPoint.Y, endPoint.Y);

        int annW = (int)(x2 - x1);
        int annH = (int)(y2 - y1);
        if (annW <= 0 || annH <= 0) return;

        const int cellSize = 12;

        int snapPixelW = snapshot.PixelSize.Width;
        int snapPixelH = snapshot.PixelSize.Height;

        // The AnnotationControl's Width/Height = selection rect logical dimensions.
        double selectionW = Width;
        double selectionH = Height;
        if (double.IsNaN(selectionW) || selectionW <= 0) selectionW = snapPixelW;
        if (double.IsNaN(selectionH) || selectionH <= 0) selectionH = snapPixelH;

        double scaleX = snapPixelW / selectionW;
        double scaleY = snapPixelH / selectionH;

        // Create output bitmap at annotation size (logical pixels)
        var result = new WriteableBitmap(
            new PixelSize(annW, annH),
            new Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888,
            Avalonia.Platform.AlphaFormat.Premul);

        // Read only the ROI needed by the annotation (instead of the full snapshot).
        int roiX = Math.Clamp((int)Math.Floor(x1 * scaleX), 0, Math.Max(0, snapPixelW - 1));
        int roiY = Math.Clamp((int)Math.Floor(y1 * scaleY), 0, Math.Max(0, snapPixelH - 1));
        int roiRight = Math.Clamp((int)Math.Ceiling(x2 * scaleX), roiX + 1, snapPixelW);
        int roiBottom = Math.Clamp((int)Math.Ceiling(y2 * scaleY), roiY + 1, snapPixelH);
        int roiW = roiRight - roiX;
        int roiH = roiBottom - roiY;
        if (roiW <= 0 || roiH <= 0) return;

        // Use pooled buffers to avoid repeated large allocations while dragging.
        byte[]? srcPixels = null;
        int srcStride = 0;
        int rentedLength = 0;

        try
        {
            srcStride = roiW * 4;
            rentedLength = srcStride * roiH;
            srcPixels = ArrayPool<byte>.Shared.Rent(rentedLength);
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(srcPixels, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                snapshot.CopyPixels(
                    new PixelRect(roiX, roiY, roiW, roiH),
                    handle.AddrOfPinnedObject(),
                    rentedLength,
                    srcStride);
            }
            finally
            {
                handle.Free();
            }
        }
        catch
        {
            if (srcPixels != null && rentedLength > 0)
            {
                ArrayPool<byte>.Shared.Return(srcPixels);
            }
            throw;
        }

        if (srcPixels == null) return;

        try
        {
            // Fill the mosaic bitmap
            using var dstLock = result.Lock();
            unsafe
            {
                byte* dstPtr = (byte*)dstLock.Address;
                int dstStride = dstLock.RowBytes;

                for (int cy = 0; cy < annH; cy += cellSize)
                {
                    for (int cx = 0; cx < annW; cx += cellSize)
                    {
                        int cw = Math.Min(cellSize, annW - cx);
                        int ch = Math.Min(cellSize, annH - cy);

                        // Sample center pixel (matching SkiaSharp output algorithm)
                        double logicalSampleX = x1 + cx + cw / 2.0;
                        double logicalSampleY = y1 + cy + ch / 2.0;

                        // Map to snapshot pixel coordinates
                        int pixelSampleX = Math.Clamp((int)(logicalSampleX * scaleX), 0, snapPixelW - 1);
                        int pixelSampleY = Math.Clamp((int)(logicalSampleY * scaleY), 0, snapPixelH - 1);
                        int roiSampleX = Math.Clamp(pixelSampleX - roiX, 0, roiW - 1);
                        int roiSampleY = Math.Clamp(pixelSampleY - roiY, 0, roiH - 1);

                        // Read pixel (BGRA)
                        int srcOffset = roiSampleY * srcStride + roiSampleX * 4;
                        byte b = srcPixels[srcOffset + 0];
                        byte g = srcPixels[srcOffset + 1];
                        byte r = srcPixels[srcOffset + 2];
                        byte a = srcPixels[srcOffset + 3];

                        // Fill cell in destination
                        for (int dy = 0; dy < ch; dy++)
                        {
                            for (int dx = 0; dx < cw; dx++)
                            {
                                int destX = cx + dx;
                                int destY = cy + dy;
                                int dstOffset = destY * dstStride + destX * 4;
                                dstPtr[dstOffset + 0] = b;
                                dstPtr[dstOffset + 1] = g;
                                dstPtr[dstOffset + 2] = r;
                                dstPtr[dstOffset + 3] = a;
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            if (srcPixels != null && rentedLength > 0)
            {
                ArrayPool<byte>.Shared.Return(srcPixels);
            }
        }

        var old = MosaicPreviewBitmap;
        MosaicPreviewBitmap = result;
        old?.Dispose();
    }
}
