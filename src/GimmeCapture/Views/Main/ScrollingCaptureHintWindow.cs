using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GimmeCapture.Services.Interop;

namespace GimmeCapture.Views.Main;

// Small always-on-top hint shown during manual scrolling capture. It is excluded from
// screen capture (SetWindowCaptureVisibility false) so it never appears in the stitched
// image, and shown without activation so the target window keeps focus for scrolling.
// It also offers Finish/Cancel buttons as a reliable fallback when the keyboard keys
// (F6/Esc) cannot reach the app (e.g. the target window runs elevated).
public sealed class ScrollingCaptureHintWindow : Window
{
    private readonly TextBlock _text;
    private readonly string _baseText;
    private readonly PixelRect _anchor;

    public ScrollingCaptureHintWindow(string hintText, string finishLabel, string cancelLabel, PixelRect anchorPhysical, Action onFinish, Action onCancel)
    {
        _baseText = hintText ?? string.Empty;
        _anchor = anchorPhysical;

        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false; // don't steal focus on show — the target keeps focus to scroll
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _text = new TextBlock
        {
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            Text = _baseText
        };

        var finishButton = new Button
        {
            Content = finishLabel,
            VerticalAlignment = VerticalAlignment.Center
        };
        finishButton.Click += (_, _) => onFinish();

        var cancelButton = new Button
        {
            Content = cancelLabel,
            VerticalAlignment = VerticalAlignment.Center
        };
        cancelButton.Click += (_, _) => onCancel();

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(_text);
        panel.Children.Add(finishButton);
        panel.Children.Add(cancelButton);

        Content = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(235, 24, 24, 24)),
            BorderBrush = new SolidColorBrush(Color.Parse("#E60012")),
            BorderThickness = new Thickness(2),
            Padding = new Thickness(16, 10),
            Child = panel
        };

        Opened += (_, _) =>
        {
            var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            Win32Helpers.SetWindowCaptureVisibility(hwnd, visible: false);
            PositionNearAnchor();
        };
    }

    public void UpdateHint(int capturedRows)
    {
        _text.Text = $"{_baseText}   ({capturedRows}px)";
    }

    // Place the hint just below the captured region (or above it when there's no room),
    // horizontally centred on the region and clamped to the screen — so it stays close to the
    // selection without ever covering it.
    private void PositionNearAnchor()
    {
        var center = new PixelPoint(_anchor.X + (_anchor.Width / 2), _anchor.Y + (_anchor.Height / 2));
        var screen = Screens?.ScreenFromPoint(center)
            ?? Screens?.Primary
            ?? (Screens is { All.Count: > 0 } s ? s.All[0] : null);
        if (screen == null)
        {
            return;
        }

        var wa = screen.WorkingArea;
        double scale = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
        int w = (int)((Bounds.Width > 0 ? Bounds.Width : 440) * scale);
        int h = (int)((Bounds.Height > 0 ? Bounds.Height : 60) * scale);
        int gap = (int)(12 * scale);

        int x = _anchor.X + ((_anchor.Width - w) / 2);
        x = Math.Clamp(x, wa.X, Math.Max(wa.X, wa.Right - w));

        int below = _anchor.Y + _anchor.Height + gap;
        int above = _anchor.Y - gap - h;
        int y;
        if (below + h <= wa.Bottom)
        {
            y = below; // preferred: just under the region
        }
        else if (above >= wa.Y)
        {
            y = above; // no room below: just above the region
        }
        else
        {
            y = Math.Max(wa.Y, wa.Bottom - h); // region fills the screen: pin to bottom
        }

        Position = new PixelPoint(x, y);
    }
}
