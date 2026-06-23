using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GimmeCapture.Services.Interop;

namespace GimmeCapture.Views.Main;

// Small always-on-top hint shown during manual scrolling capture. It is excluded from
// screen capture (SetWindowCaptureVisibility false) so it never appears in the stitched
// image, and is non-focusable enough that the target window keeps focus for scrolling.
public sealed class ScrollingCaptureHintWindow : Window
{
    private readonly TextBlock _text;
    private readonly string _baseText;

    public ScrollingCaptureHintWindow(string hintText)
    {
        _baseText = hintText ?? string.Empty;

        Width = 400;
        Height = 56;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false; // never steal focus — the target window must keep focus to scroll
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _text = new TextBlock
        {
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Text = _baseText
        };

        Content = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(230, 24, 24, 24)),
            BorderBrush = new SolidColorBrush(Color.Parse("#E60012")),
            BorderThickness = new Thickness(2),
            Padding = new Thickness(16, 8),
            Child = _text
        };

        Opened += (_, _) =>
        {
            var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            Win32Helpers.SetWindowCaptureVisibility(hwnd, visible: false);
            PositionBottomCenter();
        };
    }

    public void UpdateHint(int capturedRows)
    {
        _text.Text = $"{_baseText}   ({capturedRows}px)";
    }

    private void PositionBottomCenter()
    {
        var screen = Screens?.Primary ?? (Screens is { All.Count: > 0 } s ? s.All[0] : null);
        if (screen == null)
        {
            return;
        }

        var wa = screen.WorkingArea;
        double scale = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
        int w = (int)(Width * scale);
        int h = (int)(Height * scale);
        int x = wa.X + ((wa.Width - w) / 2);
        int y = wa.Bottom - h - (int)(48 * scale);
        Position = new PixelPoint(x, y);
    }
}
