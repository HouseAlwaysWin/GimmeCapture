using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using GimmeCapture.Services.Interop;

namespace GimmeCapture.Views.Main;

// A live thumbnail of the growing stitched strip, shown beside the captured region during manual
// scrolling capture. Like the hint/region chrome it is always-on-top, never activated (so the target
// keeps focus for scrolling) and excluded from screen capture (SetWindowCaptureVisibility false) so it
// never stitches into the image. It reassures the user that stitching is working and lets them SEE a
// misplacement/stall the moment it happens, instead of discovering it only in the finished pin.
//
// Layout (form A): the whole strip is scaled to a fixed narrow width and the window grows downward as the
// strip grows; once it would exceed the on-screen height cap the window stops growing and the image
// scales to fit (Stretch=Uniform letterboxes the now-shorter box), so the entire strip always stays
// visible.
public sealed class ScrollingCapturePreviewWindow : Window
{
    // Fixed display width (DIP). "Narrow" per the agreed spec — a glanceable thumbnail, not a full preview.
    private const double PreviewWidthDip = 210;
    private const double MinHeightDip = 90;
    // Fraction of the working-area height the preview may occupy before it stops growing and scales to fit.
    private const double MaxHeightFraction = 0.72;
    // Chrome eating into the content box (2px border + 4px padding on each side).
    private const double ChromeInset = 12;

    private readonly Image _image;
    private readonly PixelRect _anchor;

    public ScrollingCapturePreviewWindow(PixelRect anchorPhysical)
    {
        _anchor = anchorPhysical;

        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false; // don't steal focus on show — the target keeps focus to scroll
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = PreviewWidthDip;
        Height = MinHeightDip;

        _image = new Image { Stretch = Avalonia.Media.Stretch.Uniform };
        // The heavy downscale to a narrow width already happened in ManualScrollStrip.RenderScaledPreview,
        // so this is only the final fit-to-box — low quality is plenty and cheap.
        RenderOptions.SetBitmapInterpolationMode(_image, BitmapInterpolationMode.LowQuality);

        Content = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(235, 24, 24, 24)),
            BorderBrush = new SolidColorBrush(Color.Parse("#E60012")),
            BorderThickness = new Thickness(2),
            Padding = new Thickness(4),
            Child = _image
        };

        Opened += (_, _) =>
        {
            var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            Win32Helpers.SetWindowCaptureVisibility(hwnd, visible: false);
            UpdatePosition();
        };

        // Height changes as the strip grows; reposition once the real dimensions settle so the flip/clamp
        // math uses the measured size, not a stale one.
        SizeChanged += (_, _) => UpdatePosition();

        Closed += (_, _) =>
        {
            if (_image.Source is IDisposable d)
            {
                _image.Source = null;
                d.Dispose();
            }
        };
    }

    /// <summary>
    /// Swaps in the latest strip thumbnail and resizes the window to the strip's aspect (clamped to the
    /// on-screen height cap). The previous bitmap is disposed — the window owns whatever it is handed.
    /// </summary>
    public void UpdatePreview(Bitmap? bitmap)
    {
        if (bitmap == null)
        {
            return;
        }

        var previous = _image.Source as IDisposable;
        _image.Source = bitmap;
        if (previous != null && !ReferenceEquals(previous, bitmap))
        {
            previous.Dispose();
        }

        double aspect = bitmap.PixelSize.Width > 0
            ? bitmap.PixelSize.Height / (double)bitmap.PixelSize.Width
            : 1.0;
        double innerWidth = PreviewWidthDip - ChromeInset;
        double desired = (innerWidth * aspect) + ChromeInset;
        Height = Math.Clamp(desired, MinHeightDip, MaxHeightDip());
        UpdatePosition();
    }

    private double MaxHeightDip()
    {
        var screen = ResolveScreen();
        double scale = screen == null || screen.Scaling <= 0 ? 1.0 : screen.Scaling;
        double workHeightDip = screen == null ? 720 : screen.WorkingArea.Height / scale;
        return Math.Max(MinHeightDip, workHeightDip * MaxHeightFraction);
    }

    private Avalonia.Platform.Screen? ResolveScreen()
    {
        var center = new PixelPoint(_anchor.X + (_anchor.Width / 2), _anchor.Y + (_anchor.Height / 2));
        return Screens?.ScreenFromPoint(center)
            ?? Screens?.Primary
            ?? (Screens is { All.Count: > 0 } s ? s.All[0] : null);
    }

    // Dock to the RIGHT of the captured region (a screenshot tool's side panel), flipping to the LEFT when
    // there's no room, and finally clamping inside the working area. Kept OUTSIDE the region on the common
    // path so it never sits over the content the user is scrolling — and, on X11 (no capture affinity),
    // never lands inside the grabbed rect. Vertically aligned to the region's top, clamped to the screen.
    private void UpdatePosition()
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        var screen = ResolveScreen();
        if (screen == null)
        {
            return;
        }

        var wa = screen.WorkingArea;
        double scale = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
        int w = (int)(Bounds.Width * scale);
        int h = (int)(Bounds.Height * scale);
        int gap = (int)(12 * scale);

        int rightX = _anchor.X + _anchor.Width + gap;
        int leftX = _anchor.X - gap - w;
        int x;
        if (rightX + w <= wa.Right)
        {
            x = rightX;
        }
        else if (leftX >= wa.X)
        {
            x = leftX;
        }
        else
        {
            // No room on either side — hug the right working-area edge (capture affinity keeps it clean
            // even if it now overlaps the region on Windows).
            x = Math.Max(wa.X, wa.Right - w);
        }

        int y = _anchor.Y;
        y = Math.Clamp(y, wa.Y, Math.Max(wa.Y, wa.Bottom - h));

        Position = new PixelPoint(x, y);
    }
}
