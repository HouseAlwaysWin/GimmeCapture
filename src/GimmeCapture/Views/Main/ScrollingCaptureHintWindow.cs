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
    private readonly string _stalledText;
    private readonly PixelRect _anchor;
    private int _lastRows;
    private bool _stalled;

    private static readonly IBrush NormalForeground = Brushes.White;
    private static readonly IBrush StalledForeground = new SolidColorBrush(Color.Parse("#FFC107"));

    public ScrollingCaptureHintWindow(string hintText, string stalledText, string finishLabel, string cancelLabel, PixelRect anchorPhysical, Action onFinish, Action onCancel)
    {
        _baseText = hintText ?? string.Empty;
        _stalledText = stalledText ?? string.Empty;
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
            UpdatePosition();
        };

        // SizeToContent finalises the size after Opened, so reposition once the real
        // dimensions are known — otherwise the placement uses a stale/zero size.
        SizeChanged += (_, _) => UpdatePosition();
    }

    public void UpdateHint(int capturedRows)
    {
        _lastRows = capturedRows;
        Render();
    }

    /// <summary>
    /// Stitching lost track of the content (frames stopped matching the strip — typically after a
    /// too-fast scroll jumped past the strip's edge). Shows a "scroll back a little" warning until
    /// alignment recovers; without this the capture silently stops growing and the user only finds
    /// out after finishing, when the pinned image is a stub.
    /// </summary>
    public void SetStalled(bool stalled)
    {
        if (_stalled == stalled)
        {
            return;
        }

        _stalled = stalled;
        Render();
    }

    private void Render()
    {
        string rows = _lastRows > 0 ? $"   ({_lastRows}px)" : string.Empty;
        _text.Text = _stalled ? $"⚠ {_stalledText}{rows}" : $"{_baseText}{rows}";
        _text.Foreground = _stalled ? StalledForeground : NormalForeground;
    }

    // Attach the hint just outside the captured region, like a screenshot tool's action bar:
    // directly below the region's bottom edge, flipping to just above the top edge when there's
    // no room below. It is always OUTSIDE the region and stays on the region's own screen.
    private void UpdatePosition()
    {
        // Wait until the window has a real measured size.
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

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
        int w = (int)(Bounds.Width * scale);
        int h = (int)(Bounds.Height * scale);
        int gap = (int)(12 * scale);

        // Horizontally centred on the region, clamped to the screen.
        int x = _anchor.X + ((_anchor.Width - w) / 2);
        x = Math.Clamp(x, wa.X, Math.Max(wa.X, wa.Right - w));

        // Vertically: just below the region; if it would run off the screen, just above it.
        int belowY = _anchor.Y + _anchor.Height + gap;
        int aboveY = _anchor.Y - gap - h;
        int y;
        if (belowY + h <= wa.Bottom)
        {
            y = belowY;
        }
        else if (aboveY >= wa.Y)
        {
            y = aboveY;
        }
        else
        {
            // The region is taller than the screen leaves room for on either side; keep the hint
            // outside the bottom edge as far as the screen allows.
            y = Math.Max(wa.Y, wa.Bottom - h);
        }

        Position = new PixelPoint(x, y);
    }
}
